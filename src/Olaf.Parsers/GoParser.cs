using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Parsers;

public sealed class GoParser : IEcosystemParser
{
    public string Ecosystem => "go";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "go.mod", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var modPath = Path.Combine(inputPath, "go.mod");
            if (File.Exists(modPath))
            {
                return ParseGoMod(modPath);
            }

            var candidate = Directory.GetFiles(inputPath).FirstOrDefault(CanHandle);
            if (candidate is not null)
            {
                return ParseGoMod(candidate);
            }

            return Array.Empty<Dependency>();
        }

        return ParseGoMod(inputPath);
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
                if (line.Length == 0 || line.StartsWith("//"))
                {
                    continue;
                }

                if (!inRequireBlock && line.StartsWith("require (", StringComparison.Ordinal))
                {
                    inRequireBlock = true;
                    var rest = line.Substring("require (".Length).Trim();
                    if (rest.Length > 0 && !rest.StartsWith("//"))
                    {
                        TryAddRequireLine(deps, rest);
                    }

                    continue;
                }

                if (!inRequireBlock && string.Equals(line, "require (", StringComparison.Ordinal))
                {
                    inRequireBlock = true;
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
        if (text.Length == 0 || text.StartsWith("//"))
        {
            return (null, null, false);
        }

        var indirect = text.Contains("// indirect", StringComparison.Ordinal);
        var comment = text.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
        {
            text = text.Substring(0, comment).Trim();
        }

        var match = Regex.Match(text, @"^(\S+)\s+(\S+)\s*$");
        if (!match.Success)
        {
            return (null, null, false);
        }

        return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim(), indirect);
    }
}
