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
            .Select(l => new
            {
                ecosystem = l.Dependency.Ecosystem,
                name = l.Dependency.Name,
                version = l.Dependency.Version,
                spdx = l.SpdxId,
                licenseText = l.LicenseText,
                sourceUrl = l.SourceUrl,
                status = l.Status,
                reason = l.Reason,
                direct = l.Dependency.Direct,
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
