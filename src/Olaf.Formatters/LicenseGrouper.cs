using Olaf.Core;

namespace Olaf.Formatters;

/// <summary>
/// Issue #74: shared grouping projection for --group-by-license.
/// Key = LicenseDisplay.EffectiveSpdx (B2); groups ordered Ordinal asc
/// with Unknown LAST by explicit rule (B1); in-group order is
/// FormatterSort.ByEcosystemNameVersion; first-sorted LicenseText wins (B3).
/// Count-desc secondary is vacuous: keys are distinct by construction.
/// </summary>
public sealed record LicenseGroup(string Spdx, IReadOnlyList<ResolvedLicense> Packages, string? LicenseText);

public static class LicenseGrouper
{
    /// <summary>
    /// Single canonical Unknown-group check (Ordinal) shared by the grouped
    /// branches of the txt/md/html formatters — never re-spell the literal.
    /// </summary>
    public static bool IsUnknown(string spdx) => spdx.Equals("Unknown", StringComparison.Ordinal);

    public static IReadOnlyList<LicenseGroup> Group(IEnumerable<ResolvedLicense> licenses)
    {
        ArgumentNullException.ThrowIfNull(licenses);
        var sorted = FormatterSort.ByEcosystemNameVersion(licenses).ToList();
        return sorted
            .GroupBy(LicenseDisplay.EffectiveSpdx, StringComparer.Ordinal)
            .OrderBy(g => IsUnknown(g.Key) ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new LicenseGroup(g.Key, g.ToList(), g.First().LicenseText))
            .ToList();
    }
}
