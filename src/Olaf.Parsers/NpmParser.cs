using System.Text.Json;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class NpmParser : IEcosystemParser
{
    public string Ecosystem => "npm";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "package-lock.json", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var lockPath = Path.Combine(inputPath, "package-lock.json");
            if (File.Exists(lockPath))
            {
                return ParseLock(lockPath);
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

        return ParseManifest(path);
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
                        continue;
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
}
