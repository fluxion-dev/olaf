using System.Text.Json;
using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class SwiftParser : IEcosystemParser
{
    public string Ecosystem => "swift";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "Package.swift", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Package.resolved", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var lockPath = Path.Combine(inputPath, "Package.resolved");
            if (File.Exists(lockPath))
            {
                return ParseResolved(lockPath);
            }

            var manifestPath = Path.Combine(inputPath, "Package.swift");
            if (File.Exists(manifestPath))
            {
                return ParseManifest(manifestPath);
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
        if (string.Equals(name, "Package.resolved", StringComparison.OrdinalIgnoreCase))
        {
            return ParseResolved(path);
        }

        return ParseManifest(path);
    }

    internal static IReadOnlyList<Dependency> ParseManifest(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var text = File.ReadAllText(path);
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // .package(url: "...", from: "x") — the call may span multiple lines.
            foreach (Match match in Regex.Matches(text, @"\.package\s*\(\s*url\s*:\s*""([^""]+)""([^)]*)\)", RegexOptions.Singleline))
            {
                var url = match.Groups[1].Value.Trim();
                if (url.Length == 0)
                {
                    continue;
                }

                var version = ExtractManifestVersion(match.Groups[2].Value);
                byName.TryAdd(DeriveName(null, url), version);
            }

            return byName.Select(kv => new Dependency("swift", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
    }

    internal static string ExtractManifestVersion(string args)
    {
        var m = Regex.Match(args, @"(?:from|exact)\s*:\s*""([^""]+)""");
        if (m.Success && m.Groups[1].Value.Trim().Length > 0)
        {
            return m.Groups[1].Value.Trim();
        }

        // Requirement helpers: .upToNextMajor(from: "1.0.0"), "1.0.0"..."2.0.0".
        m = Regex.Match(args, @"""([^""]+)""");
        if (m.Success && m.Groups[1].Value.Trim().Length > 0)
        {
            return m.Groups[1].Value.Trim();
        }

        return "*";
    }

    internal static IReadOnlyList<Dependency> ParseResolved(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<Dependency>();
            }

            // v1 nests pins under "object"; v2/v3 keep them at the top level.
            JsonElement pins = default;
            var found = false;
            if (root.TryGetProperty("pins", out var top) && top.ValueKind == JsonValueKind.Array)
            {
                pins = top;
                found = true;
            }
            else if (root.TryGetProperty("object", out var obj)
                && obj.ValueKind == JsonValueKind.Object
                && obj.TryGetProperty("pins", out var nested)
                && nested.ValueKind == JsonValueKind.Array)
            {
                pins = nested;
                found = true;
            }

            if (!found)
            {
                return Array.Empty<Dependency>();
            }

            var deps = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in pins.EnumerateArray())
            {
                if (pin.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // v2/v3: identity + location; v1: package + repositoryURL.
                var identity = GetString(pin, "identity") ?? GetString(pin, "package");
                var location = GetString(pin, "location") ?? GetString(pin, "repositoryURL");
                if (string.IsNullOrWhiteSpace(identity) && string.IsNullOrWhiteSpace(location))
                {
                    continue;
                }

                string version = "*";
                if (pin.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
                {
                    version = GetString(state, "version")
                        ?? GetString(state, "revision")
                        ?? "*";
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        version = "*";
                    }
                    else
                    {
                        version = version.Trim();
                    }
                }

                var name = DeriveName(identity, location);
                if (seen.Add(name))
                {
                    deps.Add(new Dependency("swift", name, version, IsTransitive: true));
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

    internal static string DeriveName(string? identity, string? location)
    {
        if (!string.IsNullOrWhiteSpace(location))
        {
            // GitHub URLs (https + ssh) carry owner/repo — keep both so the
            // license probe can query api.github.com/repos/{owner}/{repo}.
            var gh = Regex.Match(
                location.Trim(),
                @"github\.com[/:]([^/\s]+)/([^/\s]+?)(?:\.git)?\s*$",
                RegexOptions.IgnoreCase);
            if (gh.Success)
            {
                return $"{gh.Groups[1].Value.Trim()}/{gh.Groups[2].Value.Trim()}";
            }

            var trimmed = location.Trim().TrimEnd('/');
            var last = trimmed.Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last))
            {
                last = last.Trim();
                if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    last = last.Substring(0, last.Length - 4);
                }

                if (last.Length > 0)
                {
                    return last;
                }
            }
        }

        return string.IsNullOrWhiteSpace(identity) ? "*" : identity.Trim();
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        return null;
    }
}
