using System.Xml;
using System.Xml.Linq;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class CycloneDxXmlFormatter : ILicenseFormatter
{
    public string Format => "cyclonedx-xml";

    internal static readonly XNamespace BomNamespace = "http://cyclonedx.org/schema/bom/1.5";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var ns = BomNamespace;
        var mapped = CycloneDxComponentMapper.Map(result);
        var toolVersion = CycloneDxComponentMapper.ToolVersion;

        var metadata = new XElement(ns + "metadata",
            new XElement(ns + "timestamp", Sanitize(DateTime.UtcNow.ToString("o"))),
            new XElement(ns + "tools",
                new XElement(ns + "tool",
                    new XElement(ns + "vendor", "olaf"),
                    new XElement(ns + "name", "olaf"),
                    new XElement(ns + "version", Sanitize(toolVersion)))),
            new XElement(ns + "component",
                new XAttribute("type", "application"),
                new XElement(ns + "name", "olaf-scan")),
            new XElement(ns + "properties",
                CycloneDxComponentMapper.MetadataProperties(result)
                    .Select(p => new XElement(ns + "property",
                        new XAttribute("name", Sanitize(p.Name)),
                        Sanitize(p.Value)))));

        var components = new XElement(ns + "components",
            mapped.Select(item =>
            {
                // Pinned child order: group?, name, version, scope, licenses?, purl, properties?.
                // scope is an ELEMENT (always emitted); value text is never pre-encoded —
                // XElement/XmlWriter own all escaping (double-escape ban).
                var element = new XElement(ns + "component",
                    new XAttribute("type", "library"),
                    new XAttribute("bom-ref", Sanitize(item.BomRef)));
                if (item.Group is not null)
                {
                    element.Add(new XElement(ns + "group", Sanitize(item.Group)));
                }

                element.Add(new XElement(ns + "name", Sanitize(item.Name)));
                element.Add(new XElement(ns + "version", Sanitize(item.Version)));
                element.Add(new XElement(ns + "scope", Sanitize(item.Scope)));
                if (item.LicenseKind is CycloneDxLicenseKind.Id)
                {
                    element.Add(new XElement(ns + "licenses",
                        new XElement(ns + "license",
                            new XElement(ns + "id", Sanitize(item.LicenseValue)))));
                }
                else if (item.LicenseKind is CycloneDxLicenseKind.Name)
                {
                    element.Add(new XElement(ns + "licenses",
                        new XElement(ns + "license",
                            new XElement(ns + "name", Sanitize(item.LicenseValue)))));
                }

                element.Add(new XElement(ns + "purl", Sanitize(item.Purl)));
                if (item.Properties.Count > 0)
                {
                    element.Add(new XElement(ns + "properties",
                        item.Properties.Select(p => new XElement(ns + "property",
                            new XAttribute("name", Sanitize(p.Name)),
                            Sanitize(p.Value)))));
                }

                return element;
            }));

        // No specVersion attribute: the spec version lives in the xmlns namespace.
        var bom = new XElement(ns + "bom",
            new XAttribute("serialNumber", $"urn:uuid:{Guid.NewGuid()}"),
            new XAttribute("version", 1),
            metadata,
            components);
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), bom);
        return doc.ToString();
    }

    // Invalid XML control chars (e.g. \u0001) are stripped, never thrown:
    // XElement would throw ArgumentException on them, and real-world license
    // metadata must never crash the formatter.
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return string.Concat(value.Where(XmlConvert.IsXmlChar));
    }
}
