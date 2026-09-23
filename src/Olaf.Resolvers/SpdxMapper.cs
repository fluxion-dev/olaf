namespace Olaf.Resolvers;

internal static class SpdxMapper
{
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

        if (trimmed.Contains(" OR ", StringComparison.Ordinal)
            || trimmed.Contains(" AND ", StringComparison.Ordinal)
            || trimmed.Contains(" WITH ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var lower = trimmed.ToLowerInvariant();
        return lower switch
        {
            "mit" or "mit license" or "the mit license" => "MIT",
            "apache 2.0" or "apache-2.0" or "apache license 2.0" or "apache license, version 2.0"
                or "apache software license" or "apache license" or "apache" => "Apache-2.0",
            "isc" or "isc license" => "ISC",
            "bsd-2-clause" or "bsd 2-clause" or "bsd simplified" => "BSD-2-Clause",
            "bsd-3-clause" or "bsd 3-clause" or "bsd new" or "bsd revised" => "BSD-3-Clause",
            "gpl-2.0-only" or "gplv2" or "gpl-2.0" => "GPL-2.0-only",
            "gpl-3.0-only" or "gplv3" or "gpl-3.0" => "GPL-3.0-only",
            "lgpl-2.1-only" or "lgpl-2.1" => "LGPL-2.1-only",
            "lgpl-3.0-only" or "lgpl-3.0" => "LGPL-3.0-only",
            "mpl-2.0" or "mozilla public license 2.0" => "MPL-2.0",
            "cddl-1.0" => "CDDL-1.0",
            "epl-1.0" => "EPL-1.0",
            "epl-2.0" => "EPL-2.0",
            "unlicense" or "the unlicense" => "Unlicense",
            "cc0-1.0" or "cc0" => "CC0-1.0",
            "artistic-2.0" => "Artistic-2.0",
            _ => trimmed,
        };
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

        if (lower.Contains("gpl"))
        {
            return "GPL-3.0-only";
        }

        return null;
    }
}
