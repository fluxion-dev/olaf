using Olaf.Core;

namespace Olaf.Parsers;

public sealed class ParserRegistry
{
    private readonly IReadOnlyList<IEcosystemParser> _parsers;

    public ParserRegistry(IEnumerable<IEcosystemParser> parsers)
    {
        _parsers = parsers.ToList();
    }

    public ParserRegistry()
        : this(new IEcosystemParser[] { new NpmParser(), new NuGetParser(), new PipParser() })
    {
    }

    public IReadOnlyList<Dependency> Scan(string path, string? ecosystem = null)
    {
        var parsers = FilterByEcosystem(ecosystem);

        if (File.Exists(path))
        {
            var fileName = Path.GetFileName(path);
            var matches = parsers.Where(p => p.CanHandle(fileName)).ToList();
            if (matches.Count == 0)
            {
                throw new InvalidOperationException($"No parser found for '{path}'.");
            }

            var all = new List<Dependency>();
            foreach (var parser in matches)
            {
                all.AddRange(parser.Parse(path));
            }

            return all;
        }

        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path);
            var matched = parsers
                .Where(p => files.Any(f => p.CanHandle(Path.GetFileName(f))))
                .ToList();
            if (matched.Count == 0)
            {
                throw new InvalidOperationException($"No manifests found in '{path}'.");
            }

            var all = new List<Dependency>();
            foreach (var parser in matched)
            {
                var owned = files.Where(f => parser.CanHandle(Path.GetFileName(f))).ToList();
                foreach (var file in owned)
                {
                    all.AddRange(parser.Parse(file));
                }
            }

            return all;
        }

        throw new FileNotFoundException($"Path not found: '{path}'.", path);
    }

    private IReadOnlyList<IEcosystemParser> FilterByEcosystem(string? ecosystem)
    {
        if (string.IsNullOrWhiteSpace(ecosystem))
        {
            return _parsers;
        }

        return _parsers
            .Where(p => string.Equals(p.Ecosystem, ecosystem, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
