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
    string? Group);

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
            // (Program.cs FormatDependency); residual collisions get -2, -3, ... suffixes.
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
                if (CycloneDxPurl.TrySplitMavenCoordinates(dep.Name, out var groupPart, out _))
                {
                    group = groupPart;
                }
            }

            components.Add(new CycloneDxComponent(
                dep.Name,
                dep.Version,
                CycloneDxPurl.Build(dep),
                bomRef,
                dep.Direct ? "required" : "optional",
                licenseKind,
                licenseValue,
                properties,
                group));
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
}
