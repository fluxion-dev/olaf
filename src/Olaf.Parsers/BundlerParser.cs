using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class BundlerParser : IEcosystemParser
{
    public string Ecosystem => "bundler";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.Equals(name, "Gemfile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Gemfile.lock", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.EndsWith(".gemspec", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var lockPath = Path.Combine(inputPath, "Gemfile.lock");
            if (File.Exists(lockPath))
            {
                return ParseGemfileLock(lockPath);
            }

            var manifestPath = Path.Combine(inputPath, "Gemfile");
            if (File.Exists(manifestPath))
            {
                return ParseGemfile(manifestPath);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(inputPath);
            }
            catch (IOException)
            {
                return Array.Empty<Dependency>();
            }

            var gemspec = files.FirstOrDefault(CanHandle);
            if (gemspec is not null)
            {
                return ParseGemspec(gemspec);
            }

            return Array.Empty<Dependency>();
        }

        return ParseFile(inputPath);
    }

    private IReadOnlyList<Dependency> ParseFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "Gemfile.lock", StringComparison.OrdinalIgnoreCase))
        {
            return ParseGemfileLock(path);
        }

        if (name.EndsWith(".gemspec", StringComparison.OrdinalIgnoreCase))
        {
            return ParseGemspec(path);
        }

        return ParseGemfile(path);
    }

    internal static IReadOnlyList<Dependency> ParseGemfile(string path)
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
                var (name, version) = SplitGemLine(raw);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                byName.TryAdd(name, version ?? "*");
            }

            return byName.Select(kv => new Dependency("bundler", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
    }

    internal static (string? Name, string? Version) SplitGemLine(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            return (null, null);
        }

        var match = Regex.Match(line, @"^\s*gem\s+['""]([^'""]+)['""]\s*(.*)$");
        if (!match.Success)
        {
            return (null, null);
        }

        var name = match.Groups[1].Value.Trim();
        var rest = match.Groups[2].Value.Trim();
        if (name.Length == 0)
        {
            return (null, null);
        }

        var versionMatch = Regex.Match(rest, @"['""]([^'""]+)['""]");
        var version = versionMatch.Success ? versionMatch.Groups[1].Value.Trim() : "*";
        if (version.Length == 0)
        {
            version = "*";
        }

        return (name, version);
    }

    internal static IReadOnlyList<Dependency> ParseGemfileLock(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var deps = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inGemSection = false;
            var inSpecs = false;

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
                    inGemSection = string.Equals(trimmed, "GEM", StringComparison.OrdinalIgnoreCase);
                    inSpecs = false;
                    continue;
                }

                if (!inGemSection)
                {
                    continue;
                }

                if (string.Equals(trimmed, "specs:", StringComparison.OrdinalIgnoreCase))
                {
                    inSpecs = true;
                    continue;
                }

                if (!inSpecs)
                {
                    continue;
                }

                // Spec entries are indented deeper than "specs:" (4+ spaces).
                var indent = line.Length - line.TrimStart().Length;
                if (indent < 4)
                {
                    // A new top-level subsection (e.g. "remote:") ends the specs list.
                    break;
                }

                // Nested dependency lines are indented further (6+ spaces); skip them.
                if (indent > 4)
                {
                    continue;
                }

                var match = Regex.Match(trimmed, @"^([A-Za-z0-9_.\-]+)\s*\(([^)]+)\)\s*$");
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

                if (seen.Add(name))
                {
                    deps.Add(new Dependency("bundler", name, version, IsTransitive: true));
                }
            }

            return deps;
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
    }

    internal static IReadOnlyList<Dependency> ParseGemspec(string path)
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
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var match = Regex.Match(
                    line,
                    @"add_(?:runtime_|development_)?dependency\s*\(?\s*['""]([^'""]+)['""]\s*(.*)$");
                if (!match.Success)
                {
                    continue;
                }

                var name = match.Groups[1].Value.Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                var versionMatch = Regex.Match(match.Groups[2].Value, @"['""]([^'""]+)['""]");
                var version = versionMatch.Success ? versionMatch.Groups[1].Value.Trim() : "*";
                if (version.Length == 0)
                {
                    version = "*";
                }

                byName.TryAdd(name, version);
            }

            return byName.Select(kv => new Dependency("bundler", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
    }
}
