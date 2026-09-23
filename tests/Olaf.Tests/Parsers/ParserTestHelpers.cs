using System.Reflection;
using Olaf.Core;

namespace Olaf.Tests.Parsers;

/// <summary>
/// TDD Red helpers: resolve parsers/registry via reflection so the
/// test assembly compiles while Olaf.Parsers is still empty.
/// When the type is missing we throw NotImplementedException — that is
/// the *right* Red failure (missing impl, not compile error).
/// </summary>
internal static class ParserTestHelpers
{
    private static void EnsureParsersLoaded()
    {
        try
        {
            AppDomain.CurrentDomain.Load("Olaf.Parsers");
        }
        catch
        {
        }
    }

    internal static IEcosystemParser ResolveParser(string ecosystem)
    {
        EnsureParsersLoaded();
        var parser = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => SafeGetTypes(a))
            .Where(t => typeof(IEcosystemParser).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .Select(t =>
            {
                try
                {
                    return Activator.CreateInstance(t) as IEcosystemParser;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is NotImplementedException)
                {
                    throw ex.InnerException;
                }
            })
            .FirstOrDefault(p => p is not null && string.Equals(p.Ecosystem, ecosystem, StringComparison.OrdinalIgnoreCase));

        if (parser is null)
        {
            throw new NotImplementedException($"No IEcosystemParser for '{ecosystem}' (missing parser).");
        }

        return parser;
    }

    internal static IReadOnlyList<IEcosystemParser> ResolveAllParsers()
    {
        EnsureParsersLoaded();
        var parsers = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => SafeGetTypes(a))
            .Where(t => typeof(IEcosystemParser).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .Select(t => Activator.CreateInstance(t) as IEcosystemParser)
            .Where(p => p is not null)
            .Cast<IEcosystemParser>()
            .ToList();

        if (parsers.Count == 0)
        {
            throw new NotImplementedException("No IEcosystemParser implementations found (missing parsers).");
        }

        return parsers;
    }

    internal static Type RequireRegistryType()
    {
        EnsureParsersLoaded();
        var registry = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => SafeGetTypes(a))
            .FirstOrDefault(t => string.Equals(t.Name, "ParserRegistry", StringComparison.Ordinal));

        if (registry is null)
        {
            throw new NotImplementedException("ParserRegistry not implemented (missing file|dir scan).");
        }

        return registry;
    }

    internal static string FixturePath(params string[] segments)
    {
        var dir = AppContext.BaseDirectory;
        string? root = null;
        var current = new DirectoryInfo(dir);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "olaf.slnx")))
            {
                root = current.FullName;
                break;
            }

            current = current.Parent;
        }

        root ??= Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", ".."));

        return Path.Combine(new[] { root, "tests", "Olaf.Tests", "Fixtures" }.Concat(segments).ToArray());
    }

    internal static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "olaf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string CopyFixtureToTempDir(string fixtureFile, string destFileName)
    {
        var tempDir = CreateTempDir();
        File.Copy(fixtureFile, Path.Combine(tempDir, destFileName));
        return tempDir;
    }

    internal static void CopyFixtureToDir(string fixtureFile, string destDir, string destFileName)
    {
        Directory.CreateDirectory(destDir);
        File.Copy(fixtureFile, Path.Combine(destDir, destFileName), overwrite: true);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
    }
}
