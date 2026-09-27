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
        : this(new IEcosystemParser[] { new NpmParser(), new NuGetParser(), new PipParser(), new GoParser(), new CargoParser(), new MavenParser(), new GradleParser(), new ComposerParser(), new BundlerParser(), new SwiftParser(), new CocoaPodsParser(), new VcpkgParser(), new ConanParser() })
    {
    }

    public IReadOnlyList<Dependency> Scan(string path, string? ecosystem = null)
    {
        var parsers = FilterByEcosystem(ecosystem);

        if (File.Exists(path))
        {
            return ScanSingleFile(path, parsers);
        }

        if (Directory.Exists(path))
        {
            return ScanDirectory(path, parsers);
        }

        throw new FileNotFoundException($"Path not found: '{path}'.", path);
    }

    private static IReadOnlyList<Dependency> ScanSingleFile(string path, IReadOnlyList<IEcosystemParser> parsers)
    {
        var fileName = Path.GetFileName(path);
        var matchedParsers = parsers.Where(p => p.CanHandle(fileName)).ToList();
        if (matchedParsers.Count == 0)
        {
            throw new InvalidOperationException($"No parser found for '{path}'.");
        }

        var dependencies = new List<Dependency>();
        foreach (var parser in matchedParsers)
        {
            TryAddDependencies(dependencies, parser, path);
        }

        return DeduplicateAndSort(dependencies);
    }

    private static IReadOnlyList<Dependency> ScanDirectory(string path, IReadOnlyList<IEcosystemParser> parsers)
    {
        var candidateFiles = EnumerateCandidateFiles(path);

        // Group by directory; call parser.Parse(dir) ONCE per parser per dir so each
        // parser applies its lock preference (npm: lock > manifest, nuget: lock >
        // config > csproj, pip: Pipfile.lock authoritative-first, then poetry/uv merged >
        // requirements > pyproject > environment) and never double-counts.
        var parseWork = candidateFiles
            .GroupBy(f => Path.GetDirectoryName(f) ?? path)
            .SelectMany(g => parsers
                .Where(p => g.Any(f => p.CanHandle(Path.GetFileName(f))))
                .Select(p => (Directory: g.Key, Parser: p)))
            .ToList();
        if (parseWork.Count == 0)
        {
            throw new InvalidOperationException($"No manifests found in '{path}'.");
        }

        var dependencies = new List<Dependency>();
        foreach (var (directory, parser) in parseWork)
        {
            TryAddDependencies(dependencies, parser, directory);
        }

        return DeduplicateAndSort(dependencies);
    }

    private static void TryAddDependencies(List<Dependency> dependencies, IEcosystemParser parser, string path)
    {
        try
        {
            dependencies.AddRange(parser.Parse(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Malformed/inaccessible manifest: preserve swallow, never throw.
        }
    }

    private static string[] EnumerateCandidateFiles(string directory)
    {
        // Recursive walk; skip denied/missing subdirs gracefully.
        var candidateFiles = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var currentDirectory = pending.Pop();
            string[] filesInDirectory;
            string[] subdirectories;
            try
            {
                filesInDirectory = Directory.GetFiles(currentDirectory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                continue;
            }

            candidateFiles.AddRange(filesInDirectory);

            try
            {
                subdirectories = Directory.GetDirectories(currentDirectory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                pending.Push(subdirectory);
            }
        }

        return candidateFiles.ToArray();
    }

    private static IReadOnlyList<Dependency> DeduplicateAndSort(List<Dependency> dependencies)
    {
        // Dedup on (Ecosystem, Name, Version) ordinal — explicitly ignore IsTransitive.
        return dependencies
            .DistinctBy(d => (d.Ecosystem, d.Name, d.Version))
            .OrderBy(d => d.Ecosystem, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Version, StringComparer.Ordinal)
            .ToList();
    }

    private static string? NormalizeEcosystem(string? ecosystem)
    {
        if (string.IsNullOrWhiteSpace(ecosystem))
        {
            return null;
        }

        var trimmed = ecosystem.Trim();
        if (string.Equals(trimmed, "pypi", StringComparison.OrdinalIgnoreCase))
        {
            return "pip";
        }

        return trimmed;
    }

    private IReadOnlyList<IEcosystemParser> FilterByEcosystem(string? ecosystem)
    {
        var normalized = NormalizeEcosystem(ecosystem);
        if (normalized is null)
        {
            return _parsers;
        }

        return _parsers
            .Where(p => string.Equals(p.Ecosystem, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
