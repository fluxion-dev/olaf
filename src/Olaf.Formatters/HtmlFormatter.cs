using System.Net;
using System.Text;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class HtmlFormatter : ILicenseFormatter
{
    public string Format => "html";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        // Issue #70: enrichment (purl/hashes/supplier/download) is consciously
        // omitted here — fixed-column human table; SBOM/structured formats
        // carry enrichment.
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Olaf License Report</title></head><body>");
        sb.Append("<p>Total: ").Append(result.TotalCount)
            .Append(" \u00b7 Resolved: ").Append(result.ResolvedCount)
            .Append(" \u00b7 Unknown: ").Append(result.UnknownCount).Append("</p>");
        sb.Append("<table><thead><tr><th>Ecosystem</th><th>Name</th><th>Version</th><th>SPDX</th><th>License</th><th>Source</th><th>Status</th><th>Reason</th><th>Direct</th></tr></thead><tbody>");
        foreach (var l in FormatterSort.ByEcosystemNameVersion(result.Licenses))
        {
            sb.Append("<tr><td>")
                .Append(WebUtility.HtmlEncode(l.Dependency.Ecosystem))
                .Append("</td><td>")
                .Append(WebUtility.HtmlEncode(l.Dependency.Name))
                .Append("</td><td>")
                .Append(WebUtility.HtmlEncode(l.Dependency.Version))
                .Append("</td><td>")
                .Append(WebUtility.HtmlEncode(LicenseDisplay.EffectiveSpdx(l)))
                .Append("</td><td>")
                .Append(WebUtility.HtmlEncode(l.LicenseText ?? string.Empty))
                .Append("</td><td>")
                .Append(WebUtility.HtmlEncode(l.SourceUrl ?? string.Empty))
                .Append("</td><td>")
                .Append(WebUtility.HtmlEncode(l.Status))
                .Append("</td><td>")
                .Append(WebUtility.HtmlEncode(l.Reason ?? string.Empty))
                .Append("</td><td>")
                .Append(l.Dependency.Direct ? "true" : "false")
                .Append("</td></tr>");
        }

        sb.Append("</tbody></table></body></html>");
        return sb.ToString();
    }
}
