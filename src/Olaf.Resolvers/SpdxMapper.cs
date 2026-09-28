using System.Text.RegularExpressions;

namespace Olaf.Resolvers;

internal static class SpdxMapper
{
    private static readonly Regex LicenseRefRegex = new(@"^LicenseRef-[A-Za-z0-9][A-Za-z0-9.\-+]*$", RegexOptions.Compiled);

    // Bare family names (any case, optional single-digit version) carry no
    // mapping — null, never an invented -only pin. Spelled to exclude the
    // pinned forms below (gplv2, gpl-2.0, ...): no 'v', no dotted version.
    private static readonly Regex BareFamilyRegex = new(@"^(gpl|lgpl|agpl)(-\d|\d)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim().Trim('"', '\'').Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Equals("NOASSERTION", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("NONE", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Length > 120)
        {
            return null;
        }

        if (trimmed.Contains(" OR ", StringComparison.Ordinal)
            || trimmed.Contains(" AND ", StringComparison.Ordinal)
            || trimmed.Contains(" WITH ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        // Issue #77 (D4): LicenseRef passthrough — valid refs return as-is,
        // bare "LicenseRef-" (or malformed refs) return null.
        if (trimmed.StartsWith("LicenseRef-", StringComparison.Ordinal))
        {
            return LicenseRefRegex.IsMatch(trimmed) ? trimmed : null;
        }

        // Issue #77 (D4): bare gpl|lgpl|agpl (any case, optional version)
        // stay null — the -only pins below are untouched.
        if (BareFamilyRegex.IsMatch(trimmed))
        {
            return null;
        }

        var lower = trimmed.ToLowerInvariant();
        return lower switch
        {
            "mit" or "mit license" or "the mit license" => "MIT",
            "apache 2.0" or "apache-2.0" or "apache license 2.0" or "apache license, version 2.0"
                or "apache software license" or "apache license" or "apache" => "Apache-2.0",
            "apache 1.1" or "apache-1.1" or "apache license 1.1" or "apache license, version 1.1"
                or "apache software license 1.1" => "Apache-1.1",
            "isc" or "isc license" => "ISC",
            "bsd-2-clause" or "bsd 2-clause" or "bsd simplified" => "BSD-2-Clause",
            "bsd-3-clause" or "bsd 3-clause" or "bsd new" or "bsd revised" => "BSD-3-Clause",
            "bsd-4-clause" or "bsd 4-clause" or "bsd original" => "BSD-4-Clause",
            "gpl-1.0-only" or "gplv1" or "gpl-1.0" => "GPL-1.0-only",
            "gpl-2.0-only" or "gplv2" or "gpl-2.0" => "GPL-2.0-only",
            "gpl-3.0-only" or "gplv3" or "gpl-3.0" => "GPL-3.0-only",
            "gpl-2.0-or-later" => "GPL-2.0-or-later",
            "gpl-3.0-or-later" => "GPL-3.0-or-later",
            "agpl-1.0-only" or "agplv1" or "agpl-1.0" => "AGPL-1.0-only",
            "agpl-3.0-only" or "agplv3" or "agpl-3.0" => "AGPL-3.0-only",
            "agpl-3.0-or-later" => "AGPL-3.0-or-later",
            "lgpl-2.0-only" or "lgplv2" or "lgpl-2.0" => "LGPL-2.0-only",
            "lgpl-2.1-only" or "lgplv2.1" or "lgpl-2.1" => "LGPL-2.1-only",
            "lgpl-2.1-or-later" => "LGPL-2.1-or-later",
            "lgpl-3.0-only" or "lgplv3" or "lgpl-3.0" => "LGPL-3.0-only",
            "lgpl-3.0-or-later" => "LGPL-3.0-or-later",
            "mpl-1.0" or "mozilla public license 1.0" => "MPL-1.0",
            "mpl-1.1" or "mozilla public license 1.1" => "MPL-1.1",
            "mpl-2.0" or "mozilla public license 2.0" => "MPL-2.0",
            "cddl-1.0" => "CDDL-1.0",
            "epl-1.0" or "eclipse public license 1.0" or "eclipse public license, version 1.0"
                or "eclipse public license v1.0" or "eclipse public license - v 1.0" => "EPL-1.0",
            "epl-2.0" or "eclipse public license 2.0" or "eclipse public license, version 2.0"
                or "eclipse public license v2.0" => "EPL-2.0",
            "unlicense" or "the unlicense" => "Unlicense",
            "cc0-1.0" or "cc0" => "CC0-1.0",
            "artistic-2.0" => "Artistic-2.0",
            "aal" or "attribution assurance license" => "AAL",
            _ => trimmed,
        };
    }

    public static string? FromLicenseText(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var lower = content.ToLowerInvariant();
        if (lower.Contains("apache license") && (lower.Contains("version 2.0") || lower.Contains("v2.0")))
        {
            return "Apache-2.0";
        }

        if (lower.Contains("permission to use, copy, modify, and/or distribute"))
        {
            return "ISC";
        }

        if (lower.Contains("redistribution and use in source and binary forms"))
        {
            if (lower.Contains("neither the name of"))
            {
                return "BSD-3-Clause";
            }

            return "BSD-2-Clause";
        }

        if (lower.Contains("permission is hereby granted")
            && (lower.Contains("mit")
                || lower.Contains("massachusetts institute of technology")
                || lower.Contains("without warranty of any kind")))
        {
            return "MIT";
        }

        return null;
    }

    public static string? FromLicenseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var lower = url.ToLowerInvariant();
        if (lower.Contains("apache-2.0") || lower.Contains("apache_2.0")
            || (lower.Contains("apache") && lower.Contains("2.0"))
            || lower.Contains("apachelicense"))
        {
            return "Apache-2.0";
        }

        if (lower.Contains("opensource.org/licenses/mit") || lower.Contains("/mit")
            || lower.Contains("mit-license") || lower.Contains("mitlicense"))
        {
            return "MIT";
        }

        if (lower.Contains("gnu.org/licenses/agpl") || lower.Contains("agpl"))
        {
            return "AGPL-3.0-only";
        }

        if (lower.Contains("gpl-3") || lower.Contains("gnu.org/licenses/gpl"))
        {
            return "GPL-3.0-only";
        }

        if (lower.Contains("bsd-3") || lower.Contains("bsd_3"))
        {
            return "BSD-3-Clause";
        }

        if (lower.Contains("bsd-2") || lower.Contains("bsd_2"))
        {
            return "BSD-2-Clause";
        }

        if (lower.Contains("mpl-2") || lower.Contains("mozilla.org/mpl"))
        {
            return "MPL-2.0";
        }

        if (lower.Contains("isclicense") || lower.Contains("/isc"))
        {
            return "ISC";
        }

        if (lower.Contains("unlicense"))
        {
            return "Unlicense";
        }

        if (lower.Contains("opensource.org/licenses/aal") || lower.Contains("aal"))
        {
            return "AAL";
        }

        if (lower.Contains("bsd-original") || lower.Contains("bsd-4"))
        {
            return "BSD-4-Clause";
        }

        return null;
    }

    public static string? FromClassifier(string? classifier)
    {
        if (string.IsNullOrWhiteSpace(classifier))
        {
            return null;
        }

        var lower = classifier.ToLowerInvariant();
        if (lower.Contains("mit license"))
        {
            return "MIT";
        }

        if (lower.Contains("apache software license"))
        {
            return "Apache-2.0";
        }

        if (lower.Contains("bsd 4") || lower.Contains("bsd-4") || lower.Contains("bsd original") || lower.Contains("original bsd"))
        {
            return "BSD-4-Clause";
        }

        if (lower.Contains("bsd license") || lower.Contains("bsd-3") || lower.Contains("bsd 3"))
        {
            return "BSD-3-Clause";
        }

        if (lower.Contains("isc license"))
        {
            return "ISC";
        }

        if (lower.Contains("mozilla public license") || lower.Contains("mpl"))
        {
            return "MPL-2.0";
        }

        if (lower.Contains("affero") || lower.Contains("agpl"))
        {
            return "AGPL-3.0-only";
        }

        if (lower.Contains("gpl"))
        {
            return "GPL-3.0-only";
        }

        if (lower.Contains("attribution assurance") || lower.Contains("aal"))
        {
            return "AAL";
        }

        return null;
    }
}
