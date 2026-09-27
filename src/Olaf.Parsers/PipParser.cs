using System.Text.Json;
using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class PipParser : IEcosystemParser
{
    public string Ecosystem => "pip";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "requirements.txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "pyproject.toml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "poetry.lock", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "uv.lock", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "environment.yml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "environment.yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Pipfile.lock", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            // Pipfile.lock is authoritative: resolved lock output wins outright with
            // no companion fallback (no Pipfile-presence check). Return immediately
            // when non-empty so co-present poetry.lock/uv.lock/requirements are never
            // double-counted.
            var pipfileLockPath = Path.Combine(inputPath, "Pipfile.lock");
            if (File.Exists(pipfileLockPath))
            {
                var pipfileDeps = ParsePipfileLock(pipfileLockPath);
                if (pipfileDeps.Count > 0)
                {
                    return pipfileDeps;
                }
            }

            // Lock > requirements > pyproject > environment: first present tier wins.
            var poetryPath = Path.Combine(inputPath, "poetry.lock");
            var uvPath = Path.Combine(inputPath, "uv.lock");
            var hasPoetry = File.Exists(poetryPath);
            var hasUv = File.Exists(uvPath);
            if (hasPoetry || hasUv)
            {
                var merged = new List<Dependency>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var lockPath in new[] { poetryPath, uvPath })
                {
                    if (!File.Exists(lockPath))
                    {
                        continue;
                    }

                    foreach (var dep in ParseTomlPackageLock(lockPath))
                    {
                        if (seen.Add(dep.Name))
                        {
                            merged.Add(dep);
                        }
                    }
                }

                return merged;
            }

            var reqPath = Path.Combine(inputPath, "requirements.txt");
            if (File.Exists(reqPath))
            {
                return ParseRequirements(reqPath);
            }

            var tomlPath = Path.Combine(inputPath, "pyproject.toml");
            if (File.Exists(tomlPath))
            {
                return ParsePyproject(tomlPath);
            }

            var envYml = Path.Combine(inputPath, "environment.yml");
            if (File.Exists(envYml))
            {
                return ParseEnvironmentYml(envYml);
            }

            var envYaml = Path.Combine(inputPath, "environment.yaml");
            if (File.Exists(envYaml))
            {
                return ParseEnvironmentYml(envYaml);
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

    private IReadOnlyList<Dependency> ParseFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "Pipfile.lock", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePipfileLock(path);
        }

        if (string.Equals(name, "poetry.lock", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "uv.lock", StringComparison.OrdinalIgnoreCase))
        {
            return ParseTomlPackageLock(path);
        }

        if (string.Equals(name, "pyproject.toml", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePyproject(path);
        }

        if (string.Equals(name, "environment.yml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "environment.yaml", StringComparison.OrdinalIgnoreCase))
        {
            return ParseEnvironmentYml(path);
        }

        return ParseRequirements(path);
    }

    private static IReadOnlyList<Dependency> ParseRequirements(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var deps = new List<Dependency>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('-'))
                {
                    continue;
                }

                var hash = line.IndexOf('#');
                if (hash >= 0)
                {
                    line = line.Substring(0, hash).Trim();
                }

                if (line.Length == 0 || line.StartsWith('-'))
                {
                    continue;
                }

                var semi = line.IndexOf(';');
                if (semi >= 0)
                {
                    line = line.Substring(0, semi).Trim();
                }

                if (line.Length == 0)
                {
                    continue;
                }

                var (name, version) = SplitRequirement(line);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))
                {
                    continue;
                }

                deps.Add(new Dependency("pip", name, version, IsTransitive: false));
            }

            return deps;
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
    }

    internal static (string Name, string Version) SplitRequirement(string spec)
    {
        var s = spec.Trim();
        var match = Regex.Match(s, @"^([A-Za-z0-9_.\-]+)(\[[^\]]*\])?\s*(.*)$");
        if (!match.Success)
        {
            return (s, "*");
        }

        var name = match.Groups[1].Value.Trim();
        var rest = match.Groups[3].Value.Trim().TrimEnd(',').Trim();
        if (rest.Length == 0)
        {
            return (name, "*");
        }

        if (rest.StartsWith("==", StringComparison.Ordinal))
        {
            var v = rest.Substring(2).Trim();
            var comma = v.IndexOf(',');
            if (comma >= 0)
            {
                v = v.Substring(0, comma).Trim();
            }

            return (name, v.Length == 0 ? "*" : v);
        }

        return (name, rest);
    }

    private static IReadOnlyList<Dependency> ParsePyproject(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var text = File.ReadAllText(path);
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var req in ExtractProjectDependencies(text))
            {
                var (name, version) = SplitRequirement(req);
                if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "python", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byName.TryAdd(name, version);
            }

            foreach (var (name, version) in ExtractPoetryDependencies(text))
            {
                byName.TryAdd(name, version);
            }

            return byName.Select(kv => new Dependency("pip", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
    }

    private static IEnumerable<string> ExtractProjectDependencies(string text)
    {
        var results = new List<string>();
        var lines = text.Split('\n');
        var section = string.Empty;
        var inArray = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = line;
                inArray = false;
                if (line.StartsWith("[project]", StringComparison.OrdinalIgnoreCase))
                {
                    section = "[project]";
                }

                continue;
            }

            if (!string.Equals(section, "[project]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!inArray)
            {
                var idx = line.IndexOf("dependencies", StringComparison.Ordinal);
                if (idx < 0)
                {
                    continue;
                }

                var bracket = line.IndexOf('[', idx);
                if (bracket < 0)
                {
                    continue;
                }

                inArray = true;
                line = line.Substring(bracket + 1);
            }

            if (line.Contains(']'))
            {
                line = line.Substring(0, line.IndexOf(']'));
                foreach (var q in ExtractQuoted(line))
                {
                    results.Add(q);
                }

                inArray = false;
                continue;
            }

            foreach (var q in ExtractQuoted(line))
            {
                results.Add(q);
            }
        }

        return results;
    }

    private static IEnumerable<(string Name, string Version)> ExtractPoetryDependencies(string text)
    {
        var results = new List<(string, string)>();
        var lines = text.Split('\n');
        var inPoetry = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inPoetry = line.StartsWith("[tool.poetry.dependencies]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inPoetry || line.Length == 0 || line.StartsWith('#'))
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
            if (string.Equals(key, "python", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (key.Length == 0)
            {
                continue;
            }

            string? version = null;
            if (value.StartsWith('{'))
            {
                var m = Regex.Match(value, @"version\s*=\s*""([^""]+)""");
                if (!m.Success)
                {
                    m = Regex.Match(value, @"version\s*=\s*'([^']+)'");
                }

                if (m.Success)
                {
                    version = m.Groups[1].Value.Trim();
                }
            }
            else if (value.StartsWith('"') || value.StartsWith('\''))
            {
                version = value.Trim('"', '\'', ' ', '\t');
            }
            else
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                version = "*";
            }

            results.Add((key, version));
        }

        return results;
    }

    private static IEnumerable<string> ExtractQuoted(string line)
    {
        foreach (Match m in Regex.Matches(line, @"""([^""]+)"""))
        {
            yield return m.Groups[1].Value;
        }
    }

    private static IReadOnlyList<Dependency> ParsePipfileLock(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<Dependency>();
            }

            // default wins on collision: insert default first, develop only if absent.
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in new[] { "default", "develop" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var sectionEl)
                    || sectionEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var entry in sectionEl.EnumerateObject())
                {
                    if (byName.ContainsKey(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var version = "*";
                    if (entry.Value.TryGetProperty("version", out var versionEl)
                        && versionEl.ValueKind == JsonValueKind.String)
                    {
                        var raw = (versionEl.GetString() ?? string.Empty).Trim();
                        if (raw.Length > 0)
                        {
                            var stripped = raw.TrimStart('=');
                            version = stripped.Length == 0 ? "*" : stripped;
                        }
                    }
                    // No version key (git/file/path/editable) → "*" (resolved VCS/local ref).

                    // deferred: hashes validated not stored (#86)

                    byName[entry.Name] = version;
                }
            }

            return byName.Select(kv => new Dependency("pip", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (JsonException)
        {
            // Malformed lock → empty; registry does not swallow Json.
            return Array.Empty<Dependency>();
        }
        catch (IOException)
        {
            // Covers File/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<Dependency>();
        }
    }

    private static IReadOnlyList<Dependency> ParseTomlPackageLock(string path)
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

            void Flush()
            {
                if (inPackage && !string.IsNullOrWhiteSpace(name))
                {
                    deps.Add(new Dependency("pip", name.Trim(), string.IsNullOrWhiteSpace(version) ? "*" : version.Trim(), IsTransitive: true));
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
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
    }

    private static IReadOnlyList<Dependency> ParseEnvironmentYml(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var deps = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inDependencies = false;
            var inPipSection = false;
            var pipIndent = -1;

            void Add(string name, string version)
            {
                if (string.IsNullOrWhiteSpace(name)
                    || string.Equals(name, "python", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "pip", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (seen.Add(name))
                {
                    deps.Add(new Dependency("pip", name, version, IsTransitive: false));
                }
            }

            foreach (var raw in File.ReadAllLines(path))
            {
                var trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var indent = raw.Length - raw.TrimStart().Length;
                if (indent == 0)
                {
                    inDependencies = string.Equals(trimmed, "dependencies:", StringComparison.Ordinal);
                    inPipSection = false;
                    continue;
                }

                if (!inDependencies)
                {
                    continue;
                }

                if (!trimmed.StartsWith('-'))
                {
                    continue;
                }

                var entry = trimmed.Substring(1).Trim().Trim('\'', '"');
                if (entry.Length == 0)
                {
                    continue;
                }

                if (string.Equals(entry, "pip:", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry, "pip", StringComparison.OrdinalIgnoreCase))
                {
                    inPipSection = true;
                    pipIndent = indent;
                    continue;
                }

                if (inPipSection && indent > pipIndent)
                {
                    var (pipName, pipVersion) = SplitRequirement(entry);
                    if (!string.IsNullOrWhiteSpace(pipName))
                    {
                        Add(pipName, pipVersion);
                    }

                    continue;
                }

                inPipSection = false;
                var (condaName, condaVersion) = SplitCondaSpec(entry);
                if (!string.IsNullOrWhiteSpace(condaName))
                {
                    Add(condaName, condaVersion);
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

    internal static (string Name, string Version) SplitCondaSpec(string spec)
    {
        var s = spec.Trim();
        var match = Regex.Match(s, @"^([A-Za-z0-9_.\-]+)\s*(.*)$");
        if (!match.Success)
        {
            return (s, "*");
        }

        var name = match.Groups[1].Value.Trim();
        var rest = match.Groups[2].Value.Trim();
        if (rest.Length == 0)
        {
            return (name, "*");
        }

        // Conda single '=' pins (requests=2.31.0); '=='/' ' and ranges pass through trimmed.
        if (rest.StartsWith("==", StringComparison.Ordinal))
        {
            rest = rest.Substring(2).Trim();
        }
        else if (rest.StartsWith('='))
        {
            rest = rest.Substring(1).Trim();
        }

        if (rest.Length == 0)
        {
            return (name, "*");
        }

        var comma = rest.IndexOf(',');
        if (comma >= 0)
        {
            rest = rest.Substring(0, comma).Trim();
        }

        return (name, rest.Length == 0 ? "*" : rest);
    }
}
