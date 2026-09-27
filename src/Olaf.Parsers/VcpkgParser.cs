using System.Text.Json;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class VcpkgParser : IEcosystemParser
{
    public string Ecosystem => "vcpkg";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "vcpkg.json", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var manifestPath = Path.Combine(inputPath, "vcpkg.json");
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

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<Dependency>();
            }

            if (!doc.RootElement.TryGetProperty("dependencies", out var deps)
                || deps.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<Dependency>();
            }

            var result = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in deps.EnumerateArray())
            {
                var (name, version) = SplitDependency(entry);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (seen.Add(name))
                {
                    result.Add(new Dependency("vcpkg", name, version, IsTransitive: false));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<Dependency>();
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
    }

    internal static (string? Name, string Version) SplitDependency(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String)
        {
            var s = entry.GetString();
            if (string.IsNullOrWhiteSpace(s))
            {
                return (null, "*");
            }

            return (s.Trim(), "*");
        }

        if (entry.ValueKind == JsonValueKind.Object)
        {
            if (!entry.TryGetProperty("name", out var n)
                || n.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(n.GetString()))
            {
                return (null, "*");
            }

            var name = n.GetString()!.Trim();
            foreach (var key in new[] { "version>=", "version", "version-string", "version-semver" })
            {
                if (entry.TryGetProperty(key, out var v)
                    && v.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString()))
                {
                    return (name, v.GetString()!.Trim());
                }
            }

            return (name, "*");
        }

        return (null, "*");
    }
}
