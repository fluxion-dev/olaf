using System.Text.Json;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class CycloneDxFormatter : ILicenseFormatter
{
    public string Format => "cyclonedx-json";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sorted = FormatterSort.ByEcosystemNameVersion(result.Licenses).ToList();
        var bomRefCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var components = new List<Dictionary<string, object?>>(sorted.Count);
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
            object? licenses;
            var properties = new List<Dictionary<string, string?>>();
            if (string.Equals(effective, "Unknown", StringComparison.Ordinal))
            {
                licenses = Array.Empty<object>();
                properties.Add(new Dictionary<string, string?> { ["name"] = "olaf:status", ["value"] = license.Status });
                properties.Add(new Dictionary<string, string?> { ["name"] = "olaf:reason", ["value"] = license.Reason });
                if (!string.IsNullOrEmpty(license.SourceUrl))
                {
                    properties.Add(new Dictionary<string, string?> { ["name"] = "olaf:sourceUrl", ["value"] = license.SourceUrl });
                }
            }
            else if (effective.Any(char.IsWhiteSpace))
            {
                licenses = new[]
                {
                    new Dictionary<string, object?> { ["license"] = new Dictionary<string, string?> { ["name"] = effective } },
                };
            }
            else
            {
                licenses = new[]
                {
                    new Dictionary<string, object?> { ["license"] = new Dictionary<string, string?> { ["id"] = effective } },
                };
            }

            var component = new Dictionary<string, object?>
            {
                ["type"] = "library",
                ["name"] = dep.Name,
                ["version"] = dep.Version,
                ["purl"] = CycloneDxPurl.Build(dep),
                ["bom-ref"] = bomRef,
                ["scope"] = dep.Direct ? "required" : "optional",
                ["licenses"] = licenses,
                ["properties"] = properties,
            };

            // maven/gradle carry the group qualifier separately for consumers that
            // key on it; both JVM ecosystems share the pkg:maven purl shape.
            var ecosystem = (dep.Ecosystem ?? string.Empty).ToLowerInvariant();
            if (ecosystem is "maven" or "gradle")
            {
                if (CycloneDxPurl.TrySplitMavenCoordinates(dep.Name, out var group, out _))
                {
                    component["group"] = group;
                }
            }

            components.Add(component);
        }

        // specVersion pinned to 1.5. v1.4-compat note: every field used here
        // (scope, licenses, properties, serialNumber) already exists in 1.4,
        // so 1.4 validators accept this document as a superset.
        var toolVersion = typeof(FormatterRegistry).Assembly.GetName().Version?.ToString() ?? "0.0.0-dev";
        var metadata = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["tools"] = new[]
            {
                new Dictionary<string, string?> { ["vendor"] = "olaf", ["name"] = "olaf", ["version"] = toolVersion },
            },
            // Constant: the formatter receives only ScanResult, so the input
            // basename is unavailable at this layer.
            ["component"] = new Dictionary<string, string?> { ["type"] = "application", ["name"] = "olaf-scan" },
            ["properties"] = new[]
            {
                new Dictionary<string, string?> { ["name"] = "olaf:total", ["value"] = result.TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new Dictionary<string, string?> { ["name"] = "olaf:resolved", ["value"] = result.ResolvedCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new Dictionary<string, string?> { ["name"] = "olaf:unknown", ["value"] = result.UnknownCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            },
        };
        var envelope = new Dictionary<string, object?>
        {
            ["bomFormat"] = "CycloneDX",
            ["specVersion"] = "1.5",
            ["serialNumber"] = $"urn:uuid:{Guid.NewGuid()}",
            ["version"] = 1,
            ["metadata"] = metadata,
            ["components"] = components,
        };
        return JsonSerializer.Serialize(envelope);
    }
}

internal static class CycloneDxPurl
{
    internal static bool TrySplitMavenCoordinates(string name, out string group, out string coordinates)
    {
        var separator = name.IndexOf(':');
        if (separator >= 0)
        {
            group = name[..separator];
            coordinates = name[..separator] + "/" + name[(separator + 1)..];
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
