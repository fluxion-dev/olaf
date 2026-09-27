using Olaf.Core;

namespace Olaf.Formatters;

public static class LicenseDisplay
{
    public static string EffectiveSpdx(ResolvedLicense license)
    {
        ArgumentNullException.ThrowIfNull(license);
        if (!string.IsNullOrWhiteSpace(license.SpdxId))
        {
            return license.SpdxId.Trim();
        }

        return "Unknown";
    }
}
