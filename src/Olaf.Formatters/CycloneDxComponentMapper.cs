using System.Globalization;
using Olaf.Core;

namespace Olaf.Formatters;

public enum CycloneDxLicenseKind
{
    None,
    Id,
    Name,
}

public sealed record CycloneDxProperty(string Name, string? Value);

public sealed record CycloneDxComponent(
    string Name,
    string Version,
    string Purl,
    string BomRef,
    string Scope,
    CycloneDxLicenseKind LicenseKind,
    string? LicenseValue,
    IReadOnlyList<CycloneDxProperty> Properties,
    string? Group,
    // Issue #70 enrichment slots (trailing, defaulted): omit-null downstream.
    // Issue #72 adds CopyrightHolders (5th trailing slot, same rule).
    string? Supplier = null,
    string[]? Hashes = null,
    string? DownloadUrl = null,
    string[]? CopyrightHolders = null);

/// <summary>
/// Neutral DTO mapper shared by the CycloneDX JSON and XML formatters.
/// Owns sort (FormatterSort.ByEcosystemNameVersion), bom-ref dedup (-2/-3
/// suffixes), purl building (CycloneDxPurl), the license rule
/// (LicenseDisplay.EffectiveSpdx: single-token SpdxId => id, else name,
/// "Unknown" => no license + olaf:* properties), and the metadata counts
/// (ScanResult.Total/Resolved/Unknown). Formatters stay thin: JSON maps
/// DTO->dict, XML maps DTO->XElement.
/// </summary>
public static class CycloneDxComponentMapper
{
    public static IReadOnlyList<CycloneDxComponent> Map(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sorted = FormatterSort.ByEcosystemNameVersion(result.Licenses).ToList();
        var bomRefCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var components = new List<CycloneDxComponent>(sorted.Count);
        foreach (var license in sorted)
        {
            var dep = license.Dependency;

            // bom-ref mirrors the CLI human-readable key "{ecosystem}:{name}@{version}"
            // (same shape as the ScanRunner --strict offender key); residual collisions get -2, -3, ... suffixes.
            var baseRef = $"{dep.Ecosystem}:{dep.Name}@{dep.Version}";
            string bomRef;
            if (bomRefCounts.TryGetValue(baseRef, out var seen))
            {
                seen += 1;
                bomRefCounts[baseRef] = seen;
                bomRef = $"{baseRef}-{seen}";
            }
            else
            {
                bomRefCounts[baseRef] = 1;
                bomRef = baseRef;
            }

            // License rule trusts LicenseDisplay.EffectiveSpdx (normalization already
            // happened upstream at resolve time); single-token SpdxId => id, else name.
            var effective = LicenseDisplay.EffectiveSpdx(license);
            CycloneDxLicenseKind licenseKind;
            string? licenseValue;
            List<CycloneDxProperty> properties = new();
            if (string.Equals(effective, "Unknown", StringComparison.Ordinal))
            {
                licenseKind = CycloneDxLicenseKind.None;
                licenseValue = null;
                properties.Add(new CycloneDxProperty("olaf:status", license.Status));
                properties.Add(new CycloneDxProperty("olaf:reason", license.Reason));
                if (!string.IsNullOrEmpty(license.SourceUrl))
                {
                    properties.Add(new CycloneDxProperty("olaf:sourceUrl", license.SourceUrl));
                }
            }
            else if (effective.Any(char.IsWhiteSpace))
            {
                licenseKind = CycloneDxLicenseKind.Name;
                licenseValue = effective;
            }
            else
            {
                licenseKind = CycloneDxLicenseKind.Id;
                licenseValue = effective;
            }

            // maven/gradle carry the group qualifier separately for consumers that
            // key on it; both JVM ecosystems share the pkg:maven purl shape.
            string? group = null;
            var ecosystem = (dep.Ecosystem ?? string.Empty).ToLowerInvariant();
            if (ecosystem is "maven" or "gradle")
            {
                if (MavenCoordinates.TrySplit(dep.Name, out var groupPart, out _))
                {
                    group = groupPart;
                }
            }

            components.Add(new CycloneDxComponent(
                dep.Name,
                dep.Version,
                license.Enrichment?.Purl ?? CycloneDxPurl.Build(dep),
                bomRef,
                dep.Direct ? "required" : "optional",
                licenseKind,
                licenseValue,
                properties,
                group,
                license.Enrichment?.Supplier,
                license.Enrichment?.Hashes,
                license.Enrichment?.DownloadUrl,
                license.Enrichment?.CopyrightHolders));
        }

        return components;
    }

    public static IReadOnlyList<CycloneDxProperty> MetadataProperties(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new[]
        {
            new CycloneDxProperty("olaf:total", result.TotalCount.ToString(CultureInfo.InvariantCulture)),
            new CycloneDxProperty("olaf:resolved", result.ResolvedCount.ToString(CultureInfo.InvariantCulture)),
            new CycloneDxProperty("olaf:unknown", result.UnknownCount.ToString(CultureInfo.InvariantCulture)),
        };
    }

    public static string ToolVersion =>
        typeof(CycloneDxComponentMapper).Assembly.GetName().Version?.ToString() ?? "0.0.0-dev";

    /// <summary>
    /// Issue #70: splits an "algo:value" enrichment hash on the first colon.
    /// Entries without a colon (or with blank halves) are unparseable and
    /// dropped by every SBOM formatter (never emitted, never throw).
    /// </summary>
    public static bool TrySplitHash(string? entry, out string algorithm, out string value)
    {
        algorithm = string.Empty;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var sep = entry.IndexOf(':');
        if (sep <= 0 || sep >= entry.Length - 1)
        {
            return false;
        }

        algorithm = entry.Substring(0, sep).Trim();
        value = entry.Substring(sep + 1).Trim();
        return algorithm.Length > 0 && value.Length > 0;
    }

    /// <summary>
    /// CycloneDX hash-alg names ("SHA-512"); unknown algos pass through
    /// uppercased (spec-tolerant, never dropped when parseable).
    /// </summary>
    public static string ToCycloneDxAlg(string algorithm)
    {
        return algorithm.Trim().ToLowerInvariant() switch
        {
            "md5" => "MD5",
            "sha1" => "SHA-1",
            "sha224" or "sha-224" => "SHA-224",
            "sha256" or "sha-256" => "SHA-256",
            "sha384" or "sha-384" => "SHA-384",
            "sha512" or "sha-512" => "SHA-512",
            "sha3-256" => "SHA3-256",
            "sha3-512" => "SHA3-512",
            _ => algorithm.Trim().ToUpperInvariant(),
        };
    }

    /// <summary>
    /// SPDX checksum algorithm names ("SHA256"); unknown algos pass through
    /// uppercased without hyphens (never dropped when parseable).
    /// </summary>
    public static string ToSpdxAlg(string algorithm)
    {
        return algorithm.Trim().ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal) switch
        {
            "md5" => "MD5",
            "sha1" => "SHA1",
            "sha224" => "SHA224",
            "sha256" => "SHA256",
            "sha384" => "SHA384",
            "sha512" => "SHA512",
            "sha3256" => "SHA3-256",
            "sha3512" => "SHA3-512",
            _ => algorithm.Trim().ToUpperInvariant().Replace("-", string.Empty, StringComparison.Ordinal),
        };
    }
}
