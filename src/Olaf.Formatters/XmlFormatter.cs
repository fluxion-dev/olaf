using System.Xml.Linq;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class XmlFormatter : ILicenseFormatter
{
    public string Format => "xml";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        // Issue #70: enrichment (purl/hashes/supplier/download) is consciously
        // omitted here — this is a fixed-shape human report, not a structured
        // format; SBOM (cyclonedx/spdx) and structured (json/yaml) formats
        // carry enrichment.
        var sorted = FormatterSort.ByEcosystemNameVersion(result.Licenses);
        var root = new XElement("report",
            new XElement("summary",
                new XAttribute("total", result.TotalCount),
                new XAttribute("resolved", result.ResolvedCount),
                new XAttribute("unknown", result.UnknownCount)),
            new XElement("licenses",
                sorted.Select(l => new XElement("license",
                    new XElement("ecosystem", l.Dependency.Ecosystem),
                    new XElement("name", l.Dependency.Name),
                    new XElement("version", l.Dependency.Version),
                    new XElement("spdx", l.SpdxId ?? string.Empty),
                    new XElement("licenseText", l.LicenseText ?? string.Empty),
                    new XElement("sourceUrl", l.SourceUrl ?? string.Empty),
                    new XElement("status", l.Status),
                    new XElement("reason", l.Reason ?? string.Empty),
                    new XElement("direct", l.Dependency.Direct ? "true" : "false")))));
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        return doc.ToString();
    }
}
