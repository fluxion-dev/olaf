using System.Xml.Linq;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class XmlFormatter : ILicenseFormatter
{
    public string Format => "xml";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var root = new XElement("licenses",
            result.Licenses.Select(l => new XElement("license",
                new XElement("ecosystem", l.Dependency.Ecosystem),
                new XElement("name", l.Dependency.Name),
                new XElement("version", l.Dependency.Version),
                new XElement("spdx", l.SpdxId ?? string.Empty),
                new XElement("licenseText", l.LicenseText ?? string.Empty),
                new XElement("sourceUrl", l.SourceUrl ?? string.Empty),
                new XElement("status", l.Status),
                new XElement("reason", l.Reason ?? string.Empty))));
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        return doc.ToString();
    }
}
