using Olaf.Core;
using YamlDotNet.Serialization;

namespace Olaf.Formatters;

public sealed class YamlFormatter : ILicenseFormatter
{
    public string Format => "yaml";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var items = FormatterSort.ByEcosystemNameVersion(result.Licenses)
            .Select(l =>
            {
                // Issue #70 enrichment keys trail AFTER direct and are
                // omitted-when-null (no golden churn on unenriched).
                var entry = new Dictionary<string, object?>
                {
                    ["ecosystem"] = l.Dependency.Ecosystem,
                    ["name"] = l.Dependency.Name,
                    ["version"] = l.Dependency.Version,
                    ["spdx"] = l.SpdxId,
                    ["licenseText"] = l.LicenseText,
                    ["sourceUrl"] = l.SourceUrl,
                    ["status"] = l.Status,
                    ["reason"] = l.Reason,
                    ["direct"] = l.Dependency.Direct ? "true" : "false",
                };
                if (l.Enrichment?.Purl is not null)
                {
                    entry["purl"] = l.Enrichment.Purl;
                }

                if (l.Enrichment?.Supplier is not null)
                {
                    entry["supplier"] = l.Enrichment.Supplier;
                }

                if (l.Enrichment?.DownloadUrl is not null)
                {
                    entry["downloadUrl"] = l.Enrichment.DownloadUrl;
                }

                if (l.Enrichment?.Hashes is { Length: > 0 })
                {
                    entry["hashes"] = l.Enrichment.Hashes;
                }

                return entry;
            }).ToList();
        var doc = new Dictionary<string, object?>
        {
            ["summary"] = new Dictionary<string, object?>
            {
                ["total"] = result.TotalCount,
                ["resolved"] = result.ResolvedCount,
                ["unknown"] = result.UnknownCount,
            },
            ["licenses"] = items,
        };
        var serializer = new SerializerBuilder().Build();
        return serializer.Serialize(doc);
    }
}
