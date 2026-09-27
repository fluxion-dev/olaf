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
        var name = dep.Name ?? string.Empty;
        var version = dep.Version ?? string.Empty;
        var ecosystem = (dep.Ecosystem ?? string.Empty).ToLowerInvariant();
        switch (ecosystem)
        {
            case "pip":
            case "pypi":
                return $"pkg:pypi/{name}@{version}";
            case "go":
                return $"pkg:golang/{name}@{version}";
            case "npm":
                // Scoped "@scope/name" URL-encodes "@" as "%40" but keeps "/".
                var path = name.StartsWith("@", StringComparison.Ordinal) ? "%40" + name[1..] : name;
                return $"pkg:npm/{path}@{version}";
            case "maven":
            case "gradle":
                // Gradle reuses the maven purl shape: both are JVM "group:artifact"
                // coordinates split on the first colon; a missing colon falls back
                // to the bare name (never throw).
                if (TrySplitMavenCoordinates(name, out _, out var coordinates))
                {
                    return $"pkg:maven/{coordinates}@{version}";
                }

                return $"pkg:maven/{name}@{version}";
            default:
                return $"pkg:generic/{name}@{version}";
        }
    }
}
