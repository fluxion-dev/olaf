using Olaf.Core;

namespace Olaf.Formatters;

internal static class CycloneDxPurl
{
    internal static bool TrySplitMavenCoordinates(string name, out string group, out string coordinates)
    {
        if (MavenCoordinates.TrySplit(name, out var groupPart, out var artifactPart))
        {
            group = groupPart!;
            coordinates = groupPart + "/" + artifactPart;
            return true;
        }

        group = string.Empty;
        coordinates = name;
        return false;
    }

    internal static string Build(Dependency dep)
    {
        ArgumentNullException.ThrowIfNull(dep);
        // Single mapping table lives in PurlBuilder (issue #70, B4);
        // this stays a thin delegate (subsume, not fork).
        return PurlBuilder.Build(dep.Ecosystem, dep.Name, dep.Version);
    }
}
