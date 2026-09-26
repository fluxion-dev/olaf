using System.Xml.Linq;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class MavenParser : IEcosystemParser
{
    public string Ecosystem => "maven";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "pom.xml", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var pomPath = Path.Combine(inputPath, "pom.xml");
            if (File.Exists(pomPath))
            {
                return ParsePom(pomPath);
            }

            var candidate = Directory.GetFiles(inputPath).FirstOrDefault(CanHandle);
            if (candidate is not null)
            {
                return ParsePom(candidate);
            }

            return Array.Empty<Dependency>();
        }

        return ParsePom(inputPath);
    }

    internal static IReadOnlyList<Dependency> ParsePom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null)
            {
                return Array.Empty<Dependency>();
            }

            var properties = CollectProperties(root);
            var byName = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var dependency in root.Descendants().Where(e => e.Name.LocalName == "dependency"))
            {
                // <dependencyManagement> pins versions only — not actual deps.
                if (dependency.Ancestors().Any(a => a.Name.LocalName == "dependencyManagement"))
                {
                    continue;
                }

                var groupId = ChildValue(dependency, "groupId");
                var artifactId = ChildValue(dependency, "artifactId");
                if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(artifactId))
                {
                    continue;
                }

                var rawVersion = ChildValue(dependency, "version");
                var version = ResolveVersion(rawVersion, properties, root);
                byName.TryAdd($"{groupId.Trim()}:{artifactId.Trim()}", version);
            }

            return byName.Select(kv => new Dependency("maven", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (IOException)
        {
            return Array.Empty<Dependency>();
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<Dependency>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<Dependency>();
        }
    }

    private static Dictionary<string, string> CollectProperties(XElement root)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var propertiesEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "properties");
        if (propertiesEl is null)
        {
            return properties;
        }

        foreach (var prop in propertiesEl.Elements())
        {
            var key = prop.Name.LocalName;
            var value = prop.Value?.Trim();
            if (!string.IsNullOrEmpty(key) && value is not null)
            {
                properties.TryAdd(key, value);
            }
        }

        return properties;
    }

    private static string? ChildValue(XElement parent, string localName)
    {
        return parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
    }

    internal static string ResolveVersion(string? rawVersion, Dictionary<string, string> properties, XElement root)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return "*";
        }

        var version = rawVersion.Trim();
        if (!version.Contains("${", StringComparison.Ordinal))
        {
            return version.Length == 0 ? "*" : version;
        }

        // Resolve a single ${...} placeholder (the common pom idiom).
        var start = version.IndexOf("${", StringComparison.Ordinal);
        var end = version.IndexOf('}', start);
        if (start < 0 || end < 0)
        {
            return version;
        }

        var key = version.Substring(start + 2, end - start - 2);
        string? resolved = null;
        if (key.Equals("project.version", StringComparison.Ordinal)
            || key.Equals("pom.version", StringComparison.Ordinal))
        {
            resolved = ChildValue(root, "version");
        }
        else if (!properties.TryGetValue(key, out resolved))
        {
            resolved = null;
        }

        if (string.IsNullOrWhiteSpace(resolved) || resolved.Contains("${", StringComparison.Ordinal))
        {
            return "*";
        }

        return start == 0 && end == version.Length - 1
            ? resolved.Trim()
            : (version.Substring(0, start) + resolved.Trim() + version.Substring(end + 1)).Trim();
    }
}
