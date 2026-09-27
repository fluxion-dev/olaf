using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class GradleParser : IEcosystemParser
{
    public string Ecosystem => "gradle";

    private static readonly Regex DependencyRegex = new(
        @"(?:^|[\s;(])(?:\w+\.)?(implementation|api|compileOnly|runtimeOnly|testImplementation|testRuntimeOnly|androidTestImplementation|debugImplementation|releaseImplementation|kapt|annotationProcessor|compile|runtime|testCompile|testRuntime|testCompileOnly|testRuntimeClasspath|implementationClasspath)\s*(?:\(\s*)?[""']([^""']+)[""']",
        RegexOptions.Compiled);

    private static readonly Regex BlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "build.gradle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "build.gradle.kts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "libs.versions.toml", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in new[] { "build.gradle", "build.gradle.kts", "libs.versions.toml" })
            {
                var full = Path.Combine(inputPath, file);
                if (File.Exists(full))
                {
                    foreach (var dep in ParseFile(full))
                    {
                        merged.TryAdd($"{dep.Name}@{dep.Version}", dep.Version);
                    }
                }
            }

            if (merged.Count > 0)
            {
                return merged
                    .Select(kv =>
                    {
                        var sep = kv.Key.LastIndexOf('@');
                        return new Dependency("gradle", kv.Key.Substring(0, sep), kv.Value, IsTransitive: false);
                    })
                    .ToList();
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
        if (string.Equals(name, "libs.versions.toml", StringComparison.OrdinalIgnoreCase))
        {
            return ParseVersionCatalog(path);
        }

        return ParseGradleFile(path);
    }

    internal static IReadOnlyList<Dependency> ParseGradleFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var content = BlockCommentRegex.Replace(File.ReadAllText(path), string.Empty);
            var byName = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in DependencyRegex.Matches(line))
                {
                    var coords = match.Groups[2].Value.Trim();
                    var (name, version) = SplitCoordinates(coords);
                    if (name is not null && version is not null)
                    {
                        byName.TryAdd(name, version);
                    }
                }
            }

            return byName.Select(kv => new Dependency("gradle", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
    }

    internal static (string? Name, string? Version) SplitCoordinates(string coords)
    {
        var text = coords.Trim();
        if (text.Length == 0)
        {
            return (null, null);
        }

        // Version-catalog accessors (libs.foo) and project deps carry no version.
        if (!text.Contains(':'))
        {
            return (null, null);
        }

        var parts = text.Split(':');
        if (parts.Length < 3)
        {
            return (null, null);
        }

        var group = parts[0].Trim();
        var name = parts[1].Trim();
        var version = parts[2].Trim();
        if (group.Length == 0 || name.Length == 0 || version.Length == 0)
        {
            return (null, null);
        }

        // Unresolved variables / property placeholders have no pinned version.
        if (version.Contains('$') || version.Contains("${", StringComparison.Ordinal))
        {
            return (null, null);
        }

        return ($"{group}:{name}", version);
    }

    internal static IReadOnlyList<Dependency> ParseVersionCatalog(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var versions = new Dictionary<string, string>(StringComparer.Ordinal);
            var byName = new Dictionary<string, string>(StringComparer.Ordinal);
            var section = string.Empty;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = StripTomlComment(raw).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith('['))
                {
                    section = line.Trim('[', ']', ' ').Trim().ToLowerInvariant();
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                var value = line.Substring(eq + 1).Trim();
                if (section == "versions")
                {
                    var key = line.Substring(0, eq).Trim().Trim('"', '\'');
                    var parsed = Unquote(value);
                    if (key.Length > 0 && parsed is not null)
                    {
                        versions.TryAdd(key, parsed);
                    }
                }
                else if (section == "libraries")
                {
                    var (name, version) = ParseLibraryEntry(value, versions);
                    if (name is not null && version is not null)
                    {
                        byName.TryAdd(name, version);
                    }
                }
            }

            return byName.Select(kv => new Dependency("gradle", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
    }

    private static (string? Name, string? Version) ParseLibraryEntry(string value, Dictionary<string, string> versions)
    {
        var v = value.Trim();

        // Short form: alias = "group:name:version".
        if ((v.StartsWith('"') && v.EndsWith('"') && v.Length >= 2)
            || (v.StartsWith('\'') && v.EndsWith('\'') && v.Length >= 2))
        {
            return SplitCoordinates(Unquote(v) ?? string.Empty);
        }

        if (!v.StartsWith('{'))
        {
            return (null, null);
        }

        var group = ExtractInlineField(v, "group");
        var name = ExtractInlineField(v, "name");
        var module = ExtractInlineField(v, "module");
        if (module is not null)
        {
            var sep = module.IndexOf(':');
            if (sep > 0)
            {
                group ??= module.Substring(0, sep).Trim();
                name ??= module.Substring(sep + 1).Trim();
            }
        }

        var version = ExtractInlineField(v, "version");
        var versionRef = ExtractInlineField(v, "version.ref");
        if (version is null && versionRef is not null)
        {
            versions.TryGetValue(versionRef, out version);
        }

        if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
        {
            return (null, null);
        }

        return ($"{group!.Trim()}:{name!.Trim()}", version!.Trim());
    }

    private static string? ExtractInlineField(string inlineTable, string field)
    {
        var match = Regex.Match(inlineTable, $@"{Regex.Escape(field)}\s*=\s*(""([^""]*)""|'([^']*)')");
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
    }

    private static string? Unquote(string value)
    {
        var v = value.Trim();
        if (v.Length >= 2
            && ((v.StartsWith('"') && v.EndsWith('"')) || (v.StartsWith('\'') && v.EndsWith('\''))))
        {
            return v.Substring(1, v.Length - 2).Trim();
        }

        return v.Length == 0 ? null : v;
    }

    private static string StripTomlComment(string line)
    {
        // Quote-aware: '#' inside quoted strings is data.
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '#' && !inSingle && !inDouble)
            {
                return line.Substring(0, i);
            }
        }

        return line;
    }
}
