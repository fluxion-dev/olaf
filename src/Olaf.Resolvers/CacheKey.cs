using Olaf.Core;

namespace Olaf.Resolvers;

// Issue #78: single home for disk-cache key normalization (B1).
// Trim + LowerInvariant + pip/pypi alias unification. Supersedes the inline
// key in CachingLicenseResolver:23 (which had no Trim); the in-memory L1
// migrates to this function so L1 and L2 agree.
public static class CacheKey
{
    public static string Of(Dependency dependency)
        => Of(dependency.Ecosystem, dependency.Name, dependency.Version);

    public static string Of(string ecosystem, string name, string version)
    {
        var eco = NormalizeEcosystem(ecosystem);
        return string.Join(':', eco, name.Trim().ToLowerInvariant())
            + "@" + version.Trim().ToLowerInvariant();
    }

    private static string NormalizeEcosystem(string ecosystem)
    {
        var eco = ecosystem.Trim().ToLowerInvariant();
        // Single-home alias direction matches CreatePrimary (pypi -> pip).
        return eco.Equals("pypi", StringComparison.Ordinal) ? "pip" : eco;
    }
}
