using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class CocoaPodsParser : IEcosystemParser
{
    public string Ecosystem => "cocoapods";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "Podfile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Podfile.lock", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var lockPath = Path.Combine(inputPath, "Podfile.lock");
            if (File.Exists(lockPath))
            {
                return ParsePodfileLock(lockPath);
            }

            var manifestPath = Path.Combine(inputPath, "Podfile");
            if (File.Exists(manifestPath))
            {
                return ParsePodfile(manifestPath);
            }

            var candidate = Directory.GetFiles(inputPath).FirstOrDefault(CanHandle);
            if (candidate is not null)
            {
                return ParseFile(candidate);
            }

            return Array.Empty<Dependency>();
        }

        return ParseFile(inputPath);
    }

    private static IReadOnlyList<Dependency> ParseFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "Podfile.lock", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePodfileLock(path);
        }

        return ParsePodfile(path);
    }

    internal static IReadOnlyList<Dependency> ParsePodfile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(path))
            {
                var (name, version) = SplitPodLine(raw);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                byName.TryAdd(name, version ?? "*");
            }

            return byName.Select(kv => new Dependency("cocoapods", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
    }

    internal static (string? Name, string? Version) SplitPodLine(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            return (null, null);
        }

        var match = Regex.Match(line, @"^\s*pod\s+['""]([^'""]+)['""]\s*(?:,(.*))?$");
        if (!match.Success)
        {
            return (null, null);
        }

        var name = match.Groups[1].Value.Trim();
        if (name.Length == 0)
        {
            return (null, null);
        }

        var rest = match.Groups[2].Value;
        var versionMatch = Regex.Match(rest, @"['""]([^'""]+)['""]");
        var version = versionMatch.Success ? versionMatch.Groups[1].Value.Trim() : "*";
        if (version.Length == 0)
        {
            version = "*";
        }

        return (name, version);
    }

    internal static IReadOnlyList<Dependency> ParsePodfileLock(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var deps = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inPods = false;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.TrimEnd();
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (!char.IsWhiteSpace(line, 0))
                {
                    // Top-level sections: only PODS: entries are dependencies.
                    inPods = string.Equals(trimmed, "PODS:", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inPods)
                {
                    continue;
                }

                var indent = line.Length - line.TrimStart().Length;

                // Subspec dependency lines are indented deeper; skip them.
                if (indent > 2)
                {
                    continue;
                }

                var match = Regex.Match(trimmed, @"^-\s*([^\s(]+)\s*\(([^)]+)\)\s*:?\s*$");
                if (!match.Success)
                {
                    continue;
                }

                var name = match.Groups[1].Value.Trim();
                var version = match.Groups[2].Value.Trim();
                if (name.Length == 0 || version.Length == 0)
                {
                    continue;
                }

                // Subspec entries (Name/Subspec) belong to their parent pod.
                if (name.Contains('/'))
                {
                    continue;
                }

                if (seen.Add(name))
                {
                    deps.Add(new Dependency("cocoapods", name, version, IsTransitive: true));
                }
            }

            return deps;
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
    }
}
