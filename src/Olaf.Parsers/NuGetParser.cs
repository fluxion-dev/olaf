using System.Text.Json;
using System.Xml.Linq;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class NuGetParser : IEcosystemParser
{
    public string Ecosystem => "nuget";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.Equals(name, "packages.lock.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "packages.config", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var lockPath = Path.Combine(inputPath, "packages.lock.json");
            if (File.Exists(lockPath))
            {
                return ParseLock(lockPath);
            }

            var configPath = Path.Combine(inputPath, "packages.config");
            if (File.Exists(configPath))
            {
                return ParseConfig(configPath);
            }

            var csproj = Directory.GetFiles(inputPath, "*.csproj").FirstOrDefault()
                ?? Directory.GetFiles(inputPath).FirstOrDefault(CanHandle);
            if (csproj is not null)
            {
                return ParseFile(csproj);
            }

            return Array.Empty<Dependency>();
        }

        return ParseFile(inputPath);
    }

    private IReadOnlyList<Dependency> ParseFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "packages.lock.json", StringComparison.OrdinalIgnoreCase))
        {
            return ParseLock(path);
        }

        if (string.Equals(name, "packages.config", StringComparison.OrdinalIgnoreCase))
        {
            return ParseConfig(path);
        }

        return ParseCsproj(path);
    }

    private static IReadOnlyList<Dependency> ParseCsproj(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var doc = XDocument.Load(path);
            var deps = new List<Dependency>();

            foreach (var el in doc.Descendants("PackageReference"))
            {
                var include = el.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                var version = el.Attribute("Version")?.Value;
                if (string.IsNullOrWhiteSpace(version) || version.Contains('*'))
                {
                    version = "*";
                }

                deps.Add(new Dependency("nuget", include.Trim(), version.Trim(), IsTransitive: false));
            }

            return deps;
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<Dependency>();
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
    }

    private static IReadOnlyList<Dependency> ParseLock(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var deps = new List<Dependency>();

            if (!doc.RootElement.TryGetProperty("dependencies", out var frameworks)
                || frameworks.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<Dependency>();
            }

            foreach (var framework in frameworks.EnumerateObject())
            {
                if (framework.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var pkg in framework.Value.EnumerateObject())
                {
                    var name = pkg.Name;
                    if (deps.Any(d => string.Equals(d.Name, name, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    string? resolved = null;
                    string? type = null;
                    if (pkg.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (pkg.Value.TryGetProperty("resolved", out var r)
                            && r.ValueKind == JsonValueKind.String)
                        {
                            resolved = r.GetString();
                        }

                        if (pkg.Value.TryGetProperty("type", out var t)
                            && t.ValueKind == JsonValueKind.String)
                        {
                            type = t.GetString();
                        }

                        if (string.IsNullOrWhiteSpace(resolved)
                            && pkg.Value.TryGetProperty("requested", out var req)
                            && req.ValueKind == JsonValueKind.String)
                        {
                            resolved = ExtractVersion(req.GetString());
                        }
                    }

                    if (string.IsNullOrWhiteSpace(resolved))
                    {
                        resolved = "*";
                    }

                    var direct = string.Equals(type, "Direct", StringComparison.OrdinalIgnoreCase);
                    deps.Add(new Dependency("nuget", name, resolved.Trim(), IsTransitive: !direct));
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

    private static IReadOnlyList<Dependency> ParseConfig(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var doc = XDocument.Load(path);
            var deps = new List<Dependency>();

            foreach (var el in doc.Descendants("package"))
            {
                var id = el.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var version = el.Attribute("version")?.Value;
                if (string.IsNullOrWhiteSpace(version))
                {
                    version = "*";
                }

                deps.Add(new Dependency("nuget", id.Trim(), version.Trim(), IsTransitive: false));
            }

            return deps;
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<Dependency>();
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
    }

    private static string ExtractVersion(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return "*";
        }

        var token = new string(requested.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '+').ToArray());
        return string.IsNullOrWhiteSpace(token) ? "*" : token;
    }
}
