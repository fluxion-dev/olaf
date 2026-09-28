namespace Olaf.Core;

/// <summary>
/// Single home for Package-URL construction (issue #70, B4), shared by
/// resolvers (Enrichment.Purl) and formatters (CycloneDxPurl delegates —
/// subsume, not fork: one mapping table). Never throws: null/empty inputs
/// degrade to the generic fallback. Qualifiers are supported in the
/// signature (emitted as "?k=v&amp;..." when non-empty) but all formatters
/// pass null for now.
/// </summary>
public static class PurlBuilder
{
    public static string Build(
        string? ecosystem,
        string? name,
        string? version,
        IReadOnlyDictionary<string, string?>? qualifiers = null)
    {
        var eco = (ecosystem ?? string.Empty).Trim().ToLowerInvariant();
        name ??= string.Empty;
        version ??= string.Empty;
        var qualifierSuffix = BuildQualifierSuffix(qualifiers);

        string Purl(string type, string path) => $"pkg:{type}/{path}@{version}{qualifierSuffix}";

        switch (eco)
        {
            case "npm":
                // Scoped "@scope/name" URL-encodes "@" as "%40" but keeps "/".
                var npmPath = name.StartsWith("@", StringComparison.Ordinal) ? "%40" + name[1..] : name;
                return Purl("npm", npmPath);
            case "pip":
            case "pypi":
                return Purl("pypi", name);
            case "go":
            case "golang":
                return Purl("golang", name);
            case "maven":
            case "gradle":
                // Both JVM ecosystems share the pkg:maven shape: group:artifact
                // coordinates split on the first colon; a missing colon falls
                // back to the bare name (never throw).
                if (MavenCoordinates.TrySplit(name, out var groupPart, out var artifactPart))
                {
                    return Purl("maven", groupPart + "/" + artifactPart);
                }

                return Purl("maven", name);
            case "nuget":
                return Purl("nuget", name);
            case "cargo":
                return Purl("cargo", name);
            case "bundler":
            case "gem":
                return Purl("gem", name);
            case "composer":
                return Purl("composer", name);
            case "swift":
                return Purl("swift", name);
            case "cocoapods":
                return Purl("cocoapods", name);
            case "vcpkg":
                return Purl("vcpkg", name);
            case "conan":
                return Purl("conan", name);
            case "apk":
            case "dpkg":
            case "rpm":
                // Explicit generic: distro packages have no dedicated purl type here.
                return Purl("generic", name);
            default:
                return Purl("generic", name);
        }
    }

    private static string BuildQualifierSuffix(IReadOnlyDictionary<string, string?>? qualifiers)
    {
        if (qualifiers is null || qualifiers.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(qualifiers.Count);
        foreach (var kvp in qualifiers)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value is null)
            {
                continue;
            }

            parts.Add($"{Uri.EscapeDataString(kvp.Key.Trim())}={Uri.EscapeDataString(kvp.Value)}");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }
}
