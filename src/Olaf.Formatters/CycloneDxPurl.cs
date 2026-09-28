using Olaf.Core;

namespace Olaf.Formatters;

internal static class CycloneDxPurl
{
    internal static string Build(Dependency dep)
    {
        ArgumentNullException.ThrowIfNull(dep);
        // Single mapping table lives in PurlBuilder (issue #70, B4);
        // this stays a thin delegate (subsume, not fork).
        return PurlBuilder.Build(dep.Ecosystem, dep.Name, dep.Version);
    }
}
