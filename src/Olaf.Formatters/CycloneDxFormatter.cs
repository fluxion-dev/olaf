using System.Text.Json;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class CycloneDxFormatter : ILicenseFormatter
{
    public string Format => "cyclonedx-json";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var mapped = CycloneDxComponentMapper.Map(result);
        var components = new List<Dictionary<string, object?>>(mapped.Count);
        foreach (var item in mapped)
        {
            object? licenses = item.LicenseKind switch
            {
                CycloneDxLicenseKind.None => Array.Empty<object>(),
                CycloneDxLicenseKind.Name => new[]
                {
                    new Dictionary<string, object?> { ["license"] = new Dictionary<string, string?> { ["name"] = item.LicenseValue } },
                },
                _ => new[]
                {
                    new Dictionary<string, object?> { ["license"] = new Dictionary<string, string?> { ["id"] = item.LicenseValue } },
                },
            };
            var properties = item.Properties
                .Select(p => new Dictionary<string, string?> { ["name"] = p.Name, ["value"] = p.Value })
                .ToList();

            var component = new Dictionary<string, object?>
            {
                ["type"] = "library",
                ["name"] = item.Name,
                ["version"] = item.Version,
                ["purl"] = item.Purl,
                ["bom-ref"] = item.BomRef,
                ["scope"] = item.Scope,
                ["licenses"] = licenses,
                ["properties"] = properties,
            };

            if (item.Group is not null)
            {
                component["group"] = item.Group;
            }

            components.Add(component);
        }

        // specVersion pinned to 1.5. v1.4-compat note: every field used here
        // (scope, licenses, properties, serialNumber) already exists in 1.4,
        // so 1.4 validators accept this document as a superset.
        var toolVersion = CycloneDxComponentMapper.ToolVersion;
        var metadataProperties = CycloneDxComponentMapper.MetadataProperties(result)
            .Select(p => new Dictionary<string, string?> { ["name"] = p.Name, ["value"] = p.Value })
            .ToArray();
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
            ["properties"] = metadataProperties,
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
