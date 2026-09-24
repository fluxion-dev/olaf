using Olaf.Core;

namespace Olaf.Formatters;

internal static class FormatterSort
{
    internal static IOrderedEnumerable<ResolvedLicense> ByEcosystemNameVersion(IEnumerable<ResolvedLicense> licenses)
    {
        ArgumentNullException.ThrowIfNull(licenses);
        return licenses
            .OrderBy(l => l.Dependency.Ecosystem, StringComparer.Ordinal)
            .ThenBy(l => l.Dependency.Name, StringComparer.Ordinal)
            .ThenBy(l => l.Dependency.Version, StringComparer.Ordinal);
    }
}
