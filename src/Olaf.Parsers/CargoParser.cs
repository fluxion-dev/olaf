using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class CargoParser : IEcosystemParser
{
    public string Ecosystem => "cargo";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "Cargo.toml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Cargo.lock", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var lockPath = Path.Combine(inputPath, "Cargo.lock");
            if (File.Exists(lockPath))
            {
                return ParseCargoLock(lockPath);
            }

            var manifestPath = Path.Combine(inputPath, "Cargo.toml");
            if (File.Exists(manifestPath))
            {
                return ParseCargoToml(manifestPath);
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
        if (string.Equals(name, "Cargo.lock", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCargoLock(path);
        }

        return ParseCargoToml(path);
    }

    internal static IReadOnlyList<Dependency> ParseCargoToml(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var byName = new Dictionary<string, string>(StringComparer.Ordinal);
            var inDependencies = false;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith('['))
                {
                    inDependencies = line.Equals("[dependencies]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inDependencies)
                {
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                var key = line.Substring(0, eq).Trim().Trim('"', '\'');
                var value = line.Substring(eq + 1).Trim();
                if (key.Length == 0 || value.Length == 0)
                {
                    continue;
                }

                var version = ExtractTomlVersion(value);
                if (version is null)
                {
                    continue;
                }

                byName.TryAdd(key, version);
            }

            return byName.Select(kv => new Dependency("cargo", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
    }

    internal static string? ExtractTomlVersion(string value)
    {
        var v = value.Trim();
        var comment = v.IndexOf('#');
        if (comment >= 0)
        {
            v = v.Substring(0, comment).Trim();
        }

        if (v.Length == 0)
        {
            return null;
        }

        if (v.StartsWith('{'))
        {
            var m = Regex.Match(v, @"version\s*=\s*""([^""]+)""");
            if (!m.Success)
            {
                m = Regex.Match(v, @"version\s*=\s*'([^']+)'");
            }

            return m.Success ? m.Groups[1].Value.Trim() : "*";
        }

        if ((v.StartsWith('"') && v.Length >= 2) || (v.StartsWith('\'') && v.Length >= 2))
        {
            var inner = v.Trim('"', '\'').Trim();
            return inner.Length == 0 ? "*" : inner;
        }

        return null;
    }

    internal static IReadOnlyList<Dependency> ParseCargoLock(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var deps = new List<Dependency>();
            string? name = null;
            string? version = null;
            var inPackage = false;
            var rootName = FindSiblingPackageName(path);

            void Flush()
            {
                if (inPackage && !string.IsNullOrWhiteSpace(name))
                {
                    var trimmed = name.Trim();
                    if (rootName is null || !string.Equals(trimmed, rootName, StringComparison.OrdinalIgnoreCase))
                    {
                        deps.Add(new Dependency("cargo", trimmed, string.IsNullOrWhiteSpace(version) ? "*" : version.Trim(), IsTransitive: true));
                    }
                }

                name = null;
                version = null;
            }

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (string.Equals(line, "[[package]]", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    inPackage = true;
                    continue;
                }

                if (line.StartsWith('['))
                {
                    Flush();
                    inPackage = false;
                    continue;
                }

                if (!inPackage)
                {
                    continue;
                }

                if (line.StartsWith("name", StringComparison.Ordinal))
                {
                    var m = Regex.Match(line, @"name\s*=\s*""([^""]+)""");
                    if (m.Success)
                    {
                        name = m.Groups[1].Value;
                    }
                }
                else if (line.StartsWith("version", StringComparison.Ordinal))
                {
                    var m = Regex.Match(line, @"version\s*=\s*""([^""]+)""");
                    if (m.Success)
                    {
                        version = m.Groups[1].Value;
                    }
                }
            }

            Flush();
            return deps;
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
    }

    private static string? FindSiblingPackageName(string lockPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(lockPath);
            if (dir is null)
            {
                return null;
            }

            var manifest = Path.Combine(dir, "Cargo.toml");
            if (!File.Exists(manifest))
            {
                return null;
            }

            var inPackage = false;
            foreach (var raw in File.ReadAllLines(manifest))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inPackage = line.Equals("[package]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inPackage || line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith("name", StringComparison.Ordinal))
                {
                    var m = Regex.Match(line, @"name\s*=\s*""([^""]+)""");
                    if (m.Success)
                    {
                        return m.Groups[1].Value.Trim();
                    }
                }
            }

            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
