using System.Net;
using System.Text;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class HtmlFormatter : ILicenseFormatter
{
    private readonly bool _groupByLicense;

    // Issue #74: constructor flag (default false) — default path byte-identical (B7).
    // Explicit parameterless overload: reflection-based resolution
    // (Activator.CreateInstance) requires a zero-parameter constructor.
    public HtmlFormatter()
        : this(false)
    {
    }

    public HtmlFormatter(bool groupByLicense)
    {
        _groupByLicense = groupByLicense;
    }

    public string Format => "html";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_groupByLicense)
        {
            return FormatGrouped(result);
        }
        // Issue #70: enrichment (purl/hashes/supplier/download) is consciously
        // omitted here — fixed-column human table; SBOM/structured formats
        // carry enrichment. Issue #72 exception: holders-only Copyright line
        // per package after the table (omit-when-empty).
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

        sb.Append("</tbody></table>");
        foreach (var l in FormatterSort.ByEcosystemNameVersion(result.Licenses))
        {
            if (CopyrightHoldersFormat.Join(l.Enrichment?.CopyrightHolders) is { } copyright)
            {
                sb.Append("<p>Copyright: ")
                    .Append(WebUtility.HtmlEncode(l.Dependency.Name))
                    .Append("@")
                    .Append(WebUtility.HtmlEncode(l.Dependency.Version))
                    .Append(": ")
                    .Append(WebUtility.HtmlEncode(copyright))
                    .Append("</p>");
            }
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    // Issue #74 grouped branch: separate method so the default branch above
    // stays byte-identical (B7). Header "X packages under Y licenses" (B4,
    // Y includes Unknown); bullets never vanish (B3).
    private static string FormatGrouped(ScanResult result)
    {
        var sb = new StringBuilder();
        var groups = LicenseGrouper.Group(result.Licenses);
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Olaf License Report</title></head><body>");
        sb.Append("<p>").Append(result.TotalCount).Append(" packages under ").Append(groups.Count).Append(" licenses</p>");
        sb.Append("<p>Total: ").Append(result.TotalCount)
            .Append(" \u00b7 Resolved: ").Append(result.ResolvedCount)
            .Append(" \u00b7 Unknown: ").Append(result.UnknownCount).Append("</p>");
        foreach (var group in groups)
        {
            sb.Append("<h2>").Append(WebUtility.HtmlEncode(group.Spdx))
                .Append(" (").Append(group.Packages.Count).Append(" packages)</h2>");
            if (!string.IsNullOrWhiteSpace(group.LicenseText))
            {
                sb.Append("<pre>").Append(WebUtility.HtmlEncode(group.LicenseText)).Append("</pre>");
            }

            sb.Append("<ul>");
            foreach (var l in group.Packages)
            {
                var bullet = $"{l.Dependency.Name}@{l.Dependency.Version} ({l.Dependency.Ecosystem})";
                if (LicenseGrouper.IsUnknown(group.Spdx))
                {
                    bullet += $": {l.Reason ?? string.Empty}";
                    bullet = bullet.TrimEnd();
                }

                if (CopyrightHoldersFormat.Join(l.Enrichment?.CopyrightHolders) is { } copyright)
                {
                    bullet += $"; {copyright}";
                }

                sb.Append("<li>").Append(WebUtility.HtmlEncode(bullet)).Append("</li>");
            }

            sb.Append("</ul>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }
}
