using System.Text.Json;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class JsonFormatter : ILicenseFormatter
{
    public string Format => "json";

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
                    ["direct"] = l.Dependency.Direct,
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
        var envelope = new
        {
            summary = new
            {
                total = result.TotalCount,
                resolved = result.ResolvedCount,
                unknown = result.UnknownCount,
            },
            licenses = items,
        };
        return JsonSerializer.Serialize(envelope);
    }
}
