using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class GoParser : IEcosystemParser
{
    private static readonly Regex RequireLineRegex = new(@"^(\S+)\s+(\S+)\s*$");

    public string Ecosystem => "go";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "go.mod", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "go.sum", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var modPath = Path.Combine(inputPath, "go.mod");
            var sumPath = Path.Combine(inputPath, "go.sum");
            var hasMod = File.Exists(modPath);
            var hasSum = File.Exists(sumPath);

            if (hasMod)
            {
                var modDeps = ParseGoMod(modPath);
                if (!hasSum)
                {
                    return modDeps;
                }

                var presence = ParseGoSumKeys(sumPath);
                return JoinWithGoSum(modDeps, presence);
            }

            if (hasSum)
            {
                return DepsFromGoSumKeys(ParseGoSumKeys(sumPath));
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

    internal static IReadOnlyList<Dependency> ParseFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "go.sum", StringComparison.OrdinalIgnoreCase))
        {
            return DepsFromGoSumKeys(ParseGoSumKeys(path));
        }

        return ParseGoMod(path);
    }

    internal static IReadOnlyList<Dependency> JoinWithGoSum(
        IReadOnlyList<Dependency> modDeps,
        HashSet<(string Name, string Version)> presence)
    {
        var result = new List<Dependency>(modDeps.Count);
        var seen = new HashSet<(string Name, string Version)>();
        foreach (var dep in modDeps)
        {
            var key = (dep.Name, dep.Version);
            if (!seen.Add(key))
            {
                continue;
            }

            // AND-table: direct always false; indirect true only if present in go.sum.
            var transitive = dep.IsTransitive && presence.Contains(key);
            result.Add(dep with { IsTransitive = transitive });
        }

        return result;
    }

    internal static IReadOnlyList<Dependency> DepsFromGoSumKeys(
        HashSet<(string Name, string Version)> keys)
    {
        return keys
            .OrderBy(k => k.Name, StringComparer.Ordinal)
            .ThenBy(k => k.Version, StringComparer.Ordinal)
            .Select(k => new Dependency("go", k.Name, k.Version, IsTransitive: true))
            .ToArray();
    }

    internal static HashSet<(string Name, string Version)> ParseGoSumKeys(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new HashSet<(string Name, string Version)>();
            }

            var keys = new HashSet<(string Name, string Version)>();
            foreach (var raw in File.ReadAllLines(path))
            {
                if (TryParseGoSumLine(raw, out var name, out var version)
                    && name is not null && version is not null)
                {
                    keys.Add((name, version));
                }
            }

            return keys;
        }
        catch (IOException)
        {
            // Covers FileNotFoundException + DirectoryNotFoundException (both derive from IOException).
            return new HashSet<(string Name, string Version)>();
        }
        catch (UnauthorizedAccessException)
        {
            return new HashSet<(string Name, string Version)>();
        }
    }

    internal static bool TryParseGoSumLine(string line, out string? name, out string? version)
    {
        name = null;
        version = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var text = line.Trim();
        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        var module = parts[0];
        var versionToken = parts[1];
        var hash = parts[2];

        // Hash is syntactically validated (h1: prefix) but NOT stored — parsed-but-deferred.
        if (!hash.StartsWith("h1:", StringComparison.Ordinal) || hash.Length <= 3)
        {
            return false;
        }

        string ver;
        if (versionToken.EndsWith("/go.mod", StringComparison.Ordinal))
        {
            ver = versionToken.Substring(0, versionToken.Length - "/go.mod".Length);
        }
        else
        {
            ver = versionToken;
        }

        if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(ver))
        {
            return false;
        }

        name = module;
        version = ver;
        return true;
    }

    internal static IReadOnlyList<Dependency> ParseGoMod(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var deps = new List<Dependency>();
            var inRequireBlock = false;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!inRequireBlock && line.StartsWith("require (", StringComparison.Ordinal))
                {
                    inRequireBlock = true;
                    var rest = line.Substring("require (".Length).Trim();
                    if (rest.Length > 0 && !rest.StartsWith("//", StringComparison.Ordinal))
                    {
                        TryAddRequireLine(deps, rest);
                    }

                    continue;
                }

                if (inRequireBlock && line.StartsWith(')'))
                {
                    inRequireBlock = false;
                    continue;
                }

                if (inRequireBlock)
                {
                    TryAddRequireLine(deps, line);
                    continue;
                }

                if (line.StartsWith("require ", StringComparison.Ordinal))
                {
                    TryAddRequireLine(deps, line.Substring("require ".Length).Trim());
                }
            }

            return deps;
        }
        catch (IOException)
        {
            // Covers FileNotFoundException + DirectoryNotFoundException (both derive from IOException).
            return Array.Empty<Dependency>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<Dependency>();
        }
    }

    private static void TryAddRequireLine(List<Dependency> deps, string line)
    {
        var (name, version, indirect) = SplitRequireLine(line);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        deps.Add(new Dependency("go", name, version, IsTransitive: indirect));
    }

    internal static (string? Name, string? Version, bool Indirect) SplitRequireLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal))
        {
            return (null, null, false);
        }

        var indirect = text.Contains("// indirect", StringComparison.Ordinal);
        var comment = text.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
        {
            text = text.Substring(0, comment).Trim();
        }

        var match = RequireLineRegex.Match(text);
        if (!match.Success)
        {
            return (null, null, false);
        }

        return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim(), indirect);
    }
}
