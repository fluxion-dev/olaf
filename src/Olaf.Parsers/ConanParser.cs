using System.Text.Json;
using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class ConanParser : IEcosystemParser
{
    public string Ecosystem => "conan";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "conanfile.txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "conanfile.py", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "conan.lock", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var lockPath = Path.Combine(inputPath, "conan.lock");
            if (File.Exists(lockPath))
            {
                return ParseLock(lockPath);
            }

            var txtPath = Path.Combine(inputPath, "conanfile.txt");
            if (File.Exists(txtPath))
            {
                return ParseConanfileTxt(txtPath);
            }

            var pyPath = Path.Combine(inputPath, "conanfile.py");
            if (File.Exists(pyPath))
            {
                return ParseConanfilePy(pyPath);
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
        if (string.Equals(name, "conan.lock", StringComparison.OrdinalIgnoreCase))
        {
            return ParseLock(path);
        }

        if (string.Equals(name, "conanfile.py", StringComparison.OrdinalIgnoreCase))
        {
            return ParseConanfilePy(path);
        }

        return ParseConanfileTxt(path);
    }

    internal static IReadOnlyList<Dependency> ParseConanfileTxt(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inRequires = false;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith('['))
                {
                    inRequires = line.Equals("[requires]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inRequires)
                {
                    continue;
                }

                var (name, version) = SplitRef(line);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                byName.TryAdd(name, version ?? "*");
            }

            return byName.Select(kv => new Dependency("conan", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
    }

    internal static (string? Name, string? Version) SplitRef(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            return (null, null);
        }

        // Strip inline comments ("fmt/11.0.2 # comment").
        var hash = line.IndexOf('#');
        var slash = line.IndexOf('/');
        if (hash >= 0 && (slash < 0 || hash < slash))
        {
            return (null, null);
        }

        if (hash >= 0)
        {
            line = line.Substring(0, hash).Trim();
        }

        var comma = line.IndexOf(',');
        if (comma >= 0)
        {
            line = line.Substring(0, comma).Trim();
        }

        if (line.Length == 0)
        {
            return (null, null);
        }

        var m = Regex.Match(line, @"^([A-Za-z0-9_][A-Za-z0-9_\-\+\.]*)\/([^\s@#,\]]+)");
        if (!m.Success)
        {
            return (null, null);
        }

        var name = m.Groups[1].Value.Trim();
        var version = m.Groups[2].Value.Trim().Trim('"', '\'').Trim();
        if (name.Length == 0 || version.Length == 0)
        {
            return (null, null);
        }

        return (name, version);
    }

    internal static IReadOnlyList<Dependency> ParseConanfilePy(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var text = File.ReadAllText(path);
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // requires = "fmt/11.0.2" | requires = ("fmt/11.0.2", "zlib/1.3.1")
            // | requires = [...] | requires = "a/1.0", "b/2.0" (bare tuple).
            foreach (Match assign in Regex.Matches(text, @"requires\s*=\s*(\([^)]*\)|\[[^\]]*\]|""[^""]*""(?:\s*,\s*""[^""]*"")*|'[^']*'(?:\s*,\s*'[^']*')*)", RegexOptions.Singleline))
            {
                foreach (Match q in Regex.Matches(assign.Groups[1].Value, @"[""']([^""']+)[""']"))
                {
                    var (name, version) = SplitRef(q.Groups[1].Value.Trim());
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    byName.TryAdd(name, version ?? "*");
                }
            }

            return byName.Select(kv => new Dependency("conan", kv.Key, kv.Value, IsTransitive: false)).ToList();
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
    }

    internal static IReadOnlyList<Dependency> ParseLock(string path)
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

            var refs = new List<string>();
            if (doc.RootElement.TryGetProperty("graph_lock", out var graph)
                && graph.ValueKind == JsonValueKind.Object
                && graph.TryGetProperty("nodes", out var nodes)
                && nodes.ValueKind == JsonValueKind.Object)
            {
                foreach (var node in nodes.EnumerateObject())
                {
                    if (node.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    // Node 0 is the consumer itself — never a dependency.
                    if (node.Value.TryGetProperty("recipe", out var recipe)
                        && recipe.ValueKind == JsonValueKind.String
                        && recipe.GetString()?.Equals("Consumer", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        continue;
                    }

                    if (node.Value.TryGetProperty("ref", out var r)
                        && r.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(r.GetString()))
                    {
                        refs.Add(r.GetString()!);
                    }
                }
            }

            foreach (var key in new[] { "requires", "dependencies" })
            {
                if (doc.RootElement.TryGetProperty(key, out var arr)
                    && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            refs.Add(item.GetString()!);
                        }
                    }
                }
            }

            var deps = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in refs)
            {
                var (name, version) = SplitRef(r.Trim());
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (seen.Add(name))
                {
                    deps.Add(new Dependency("conan", name, version ?? "*", IsTransitive: true));
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
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
    }
}
