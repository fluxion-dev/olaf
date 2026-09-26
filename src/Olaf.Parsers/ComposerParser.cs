using System.Text.Json;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class ComposerParser : IEcosystemParser
{
    public string Ecosystem => "composer";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "composer.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "composer.lock", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var lockPath = Path.Combine(inputPath, "composer.lock");
            if (File.Exists(lockPath))
            {
                return ParseLock(lockPath);
            }

            var manifestPath = Path.Combine(inputPath, "composer.json");
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

    private IReadOnlyList<Dependency> ParseFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "composer.lock", StringComparison.OrdinalIgnoreCase))
        {
            return ParseLock(path);
        }

        return ParseManifest(path);
    }

    internal IReadOnlyList<Dependency> ParseManifest(string path)
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

            var deps = new List<Dependency>();
            foreach (var section in new[] { "require", "require-dev" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var obj)
                    || obj.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var prop in obj.EnumerateObject())
                {
                    if (IsPlatformPackage(prop.Name))
                    {
                        continue;
                    }

                    var version = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        version = "*";
                    }

                    deps.Add(new Dependency("composer", prop.Name, version.Trim(), IsTransitive: false));
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

    internal IReadOnlyList<Dependency> ParseLock(string path)
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

            var deps = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in new[] { "packages", "packages-dev" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var arr)
                    || arr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in arr.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!entry.TryGetProperty("name", out var n)
                        || n.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(n.GetString()))
                    {
                        continue;
                    }

                    var name = n.GetString()!.Trim();
                    string version = "*";
                    if (entry.TryGetProperty("version", out var v)
                        && v.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(v.GetString()))
                    {
                        version = v.GetString()!.Trim().TrimStart('v', 'V');
                        if (version.Length == 0)
                        {
                            version = "*";
                        }
                    }

                    if (seen.Add(name))
                    {
                        deps.Add(new Dependency("composer", name, version, IsTransitive: true));
                    }
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

    internal static bool IsPlatformPackage(string name)
    {
        if (string.Equals(name, "php", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.StartsWith("ext-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("lib-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("composer-", StringComparison.OrdinalIgnoreCase);
    }
}
