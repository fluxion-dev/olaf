using System.Text.Json;
using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class NpmParser : IEcosystemParser
{
    public string Ecosystem => "npm";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "package-lock.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "yarn.lock", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "bun.lock", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "bun.lockb", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            // Lock > manifest: any lock present wins; all present locks merge deduped.
            var lockFiles = new[]
                {
                    Path.Combine(inputPath, "package-lock.json"),
                    Path.Combine(inputPath, "pnpm-lock.yaml"),
                    Path.Combine(inputPath, "yarn.lock"),
                    Path.Combine(inputPath, "bun.lock"),
                    Path.Combine(inputPath, "bun.lockb"),
                }
                .Where(File.Exists)
                .ToList();
            if (lockFiles.Count > 0)
            {
                return ParseLocks(lockFiles);
            }

            var manifestPath = Path.Combine(inputPath, "package.json");
            if (File.Exists(manifestPath))
            {
                return ParseManifest(manifestPath);
            }

            var candidate = Directory.GetFiles(inputPath)
                .FirstOrDefault(f => CanHandle(f));
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
        if (string.Equals(name, "package-lock.json", StringComparison.OrdinalIgnoreCase))
        {
            return ParseLock(path);
        }

        if (string.Equals(name, "pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePnpmLock(path);
        }

        if (string.Equals(name, "yarn.lock", StringComparison.OrdinalIgnoreCase))
        {
            return ParseYarnLock(path);
        }

        if (string.Equals(name, "bun.lock", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "bun.lockb", StringComparison.OrdinalIgnoreCase))
        {
            return ParseBunLock(path);
        }

        return ParseManifest(path);
    }

    private IReadOnlyList<Dependency> ParseLocks(IReadOnlyList<string> paths)
    {
        var merged = new List<Dependency>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            foreach (var dep in ParseFile(path))
            {
                if (seen.Add(dep.Name))
                {
                    merged.Add(dep);
                }
            }
        }

        return merged;
    }

    private IReadOnlyList<Dependency> ParseManifest(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var deps = new List<Dependency>();

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<Dependency>();
            }

            foreach (var section in new[] { "dependencies", "devDependencies" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var obj)
                    || obj.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var prop in obj.EnumerateObject())
                {
                    var version = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        version = "*";
                    }

                    deps.Add(new Dependency("npm", prop.Name, version, IsTransitive: false));
                }
            }

            return deps;
        }
        catch (JsonException)
        {
            return Array.Empty<Dependency>();
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
    }

    private IReadOnlyList<Dependency> ParseLock(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var deps = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<Dependency>();
            }

            if (doc.RootElement.TryGetProperty("packages", out var packages)
                && packages.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in packages.EnumerateObject())
                {
                    if (string.IsNullOrEmpty(prop.Name))
                    {
                        continue;
                    }

                    const string prefix = "node_modules/";
                    var name = prop.Name.StartsWith(prefix, StringComparison.Ordinal)
                        ? prop.Name.Substring(prefix.Length)
                        : prop.Name;
                    if (name.Contains('/'))
                    {
                        // Allow scoped packages like "@scope/name" but skip invalid paths like "foo/bar"
                        if (!name.StartsWith("@", StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }

                    if (prop.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!prop.Value.TryGetProperty("version", out var v)
                        || v.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(v.GetString()))
                    {
                        continue;
                    }

                    if (seen.Add(name))
                    {
                        deps.Add(new Dependency("npm", name, v.GetString()!, IsTransitive: true));
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("dependencies", out var legacy)
                && legacy.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in legacy.EnumerateObject())
                {
                    if (!seen.Add(prop.Name))
                    {
                        continue;
                    }

                    string? version = null;
                    if (prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("version", out var v)
                        && v.ValueKind == JsonValueKind.String)
                    {
                        version = v.GetString();
                    }

                    if (string.IsNullOrWhiteSpace(version))
                    {
                        continue;
                    }

                    deps.Add(new Dependency("npm", prop.Name, version, IsTransitive: true));
                }
            }

            return deps;
        }
        catch (JsonException)
        {
            return Array.Empty<Dependency>();
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
    }

    private IReadOnlyList<Dependency> ParsePnpmLock(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var deps = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var inPackages = false;
            string? pendingName = null;
            string? pendingVersion = null;

            void Flush()
            {
                if (!string.IsNullOrWhiteSpace(pendingName)
                    && !string.IsNullOrWhiteSpace(pendingVersion)
                    && seen.Add(pendingName))
                {
                    deps.Add(new Dependency("npm", pendingName, pendingVersion, IsTransitive: true));
                }

                pendingName = null;
                pendingVersion = null;
            }

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0 || line.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                var indent = raw.Length - raw.TrimStart().Length;
                var trimmed = raw.Trim();
                if (indent == 0)
                {
                    Flush();
                    inPackages = string.Equals(trimmed, "packages:", StringComparison.Ordinal);
                    continue;
                }

                if (!inPackages)
                {
                    continue;
                }

                if (indent == 2 && trimmed.EndsWith(':'))
                {
                    Flush();
                    var key = trimmed.Substring(0, trimmed.Length - 1).Trim().Trim('\'', '"');
                    if (key.StartsWith('/'))
                    {
                        key = key.Substring(1);
                    }

                    var (name, version) = SplitPnpmKey(key);
                    pendingName = name;
                    pendingVersion = version;
                    continue;
                }

                if (pendingName is not null
                    && (string.IsNullOrWhiteSpace(pendingVersion) || pendingVersion == "*")
                    && trimmed.StartsWith("version:", StringComparison.Ordinal))
                {
                    var v = trimmed.Substring("version:".Length).Trim().Trim('\'', '"');
                    var comma = v.IndexOf(',');
                    if (comma >= 0)
                    {
                        v = v.Substring(0, comma).Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        pendingVersion = v;
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

    internal static (string? Name, string? Version) SplitPnpmKey(string key)
    {
        // Keys look like "express@4.18.2", "@scope/name@1.2.3",
        // or peer-suffixed "express@4.18.2(peer@1.0.0)". Version is after the last '@'.
        var paren = key.IndexOf('(');
        if (paren >= 0)
        {
            key = key.Substring(0, paren).Trim();
        }

        var at = key.LastIndexOf('@');
        if (at <= 0)
        {
            return (string.IsNullOrWhiteSpace(key) ? null : key, null);
        }

        var name = key.Substring(0, at).Trim();
        var version = key.Substring(at + 1).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, null);
        }

        return (name, string.IsNullOrWhiteSpace(version) ? null : version);
    }

    private IReadOnlyList<Dependency> ParseYarnLock(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var deps = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new List<string>();

            void Flush(string? version)
            {
                if (!string.IsNullOrWhiteSpace(version))
                {
                    foreach (var name in pending)
                    {
                        if (seen.Add(name))
                        {
                            deps.Add(new Dependency("npm", name, version, IsTransitive: true));
                        }
                    }
                }

                pending.Clear();
            }

            foreach (var raw in File.ReadAllLines(path))
            {
                if (raw.Length == 0 || raw.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                var indent = raw.Length - raw.TrimStart().Length;
                var trimmed = raw.Trim();
                if (indent == 0)
                {
                    if (!trimmed.EndsWith(':'))
                    {
                        continue;
                    }

                    Flush(null);
                    var header = trimmed.Substring(0, trimmed.Length - 1);
                    foreach (var part in header.Split(','))
                    {
                        var name = SplitYarnSpec(part.Trim().Trim('"', '\''));
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            pending.Add(name);
                        }
                    }

                    continue;
                }

                if (pending.Count == 0)
                {
                    continue;
                }

                var m = Regex.Match(trimmed, "^version\\s*[:\\s]\\s*[\"']?([^\"'\\s#,]+)");
                if (m.Success)
                {
                    Flush(m.Groups[1].Value.Trim());
                }
            }

            Flush(null);
            return deps;
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
    }

    internal static string? SplitYarnSpec(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        // Berry protocol form: "express@npm:^4.18.2" -> express.
        var npmMarker = spec.IndexOf("@npm:", StringComparison.Ordinal);
        if (npmMarker > 0)
        {
            return spec.Substring(0, npmMarker);
        }

        var start = spec.StartsWith('@') ? 1 : 0;
        var at = spec.IndexOf('@', start);
        if (at <= 0)
        {
            return spec;
        }

        return spec.Substring(0, at);
    }

    private IReadOnlyList<Dependency> ParseBunLock(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            // bun.lockb is binary: text fallback must never throw.
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Contains((byte)0))
            {
                return Array.Empty<Dependency>();
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes);
            var deps = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string? name, string? version)
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
                {
                    return;
                }

                const string prefix = "node_modules/";
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    name = name.Substring(prefix.Length);
                }

                if (string.IsNullOrWhiteSpace(name) || seen.Add(name) == false)
                {
                    return;
                }

                deps.Add(new Dependency("npm", name, version, IsTransitive: true));
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("packages", out var packages)
                    && packages.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in packages.EnumerateObject())
                    {
                        var (name, version) = SplitBunPackage(prop.Name, prop.Value);
                        Add(name, version);
                    }

                    return deps;
                }
            }
            catch (JsonException)
            {
                // Fall through to quoted name@version regex below.
            }

            foreach (Match m in Regex.Matches(text, "[\"'](@?[^\"'\\s@]+(?:/[^\"'\\s@]+)?)@(\\d+[^\"'\\s,;]*)[\"']"))
            {
                Add(m.Groups[1].Value, m.Groups[2].Value);
            }

            return deps;
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<Dependency>();
        }
    }

    internal static (string? Name, string? Version) SplitBunPackage(string key, JsonElement value)
    {
        var name = key;
        const string prefix = "node_modules/";
        if (name.StartsWith(prefix, StringComparison.Ordinal))
        {
            name = name.Substring(prefix.Length);
        }

        string? version = null;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                (_, version) = SplitPnpmKey(value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString() ?? string.Empty;
                        if (s.Contains('@'))
                        {
                            (_, version) = SplitPnpmKey(s);
                            break;
                        }
                    }
                    else if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("version", out var v)
                        && v.ValueKind == JsonValueKind.String)
                    {
                        version = v.GetString();
                        break;
                    }
                }

                break;
            case JsonValueKind.Object:
                if (value.TryGetProperty("version", out var objVersion)
                    && objVersion.ValueKind == JsonValueKind.String)
                {
                    version = objVersion.GetString();
                }

                break;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            (_, version) = SplitPnpmKey(key);
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
        {
            return (null, null);
        }

        return (name, version);
    }
}
