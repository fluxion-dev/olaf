using System.Text.Json;
using System.Xml;
using Olaf.Core;
using Olaf.Formatters;
using Olaf.Tests.Cli;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #68: CycloneDX XML export (`cyclonedx-xml`; `cyclonedx` stays JSON).
/// Offline unit style — ScanResult constructed directly, no network
/// (mirrors CycloneDxFormatterTests). The document carries a default xmlns
/// (`http://cyclonedx.org/schema/bom/1.5`), so every select goes through an
/// XmlNamespaceManager (`c:` prefix) — bare SelectNodes would find nothing.
/// Pinned shape: bom[@serialNumber urn:uuid, @version 1, NO specVersion attr]
/// children metadata,components; metadata children timestamp,tools,component,
/// properties; component attrs type=library/bom-ref, children order group?,
/// name, version, scope (ELEMENT, always), licenses?, purl (UNCONDITIONAL —
/// never assert conditional-purl), properties?. Escaping is XElement-owned
/// (double-escape ban); invalid XML control chars are sanitized, never throw.
/// Test map: F1 envelope, F2 metadata order+content, F3 empty, F4 license-id,
/// F5 license-name, F6 unknown-props, F7 adversarial round-trip, F8
/// control-char sanitize, F9 sorted/scope/purl/group, P1 JSON shape snapshot
/// (mapper-extraction guard), P2 JSON↔XML parity, R1 registry+help, R2 CLI
/// exit codes, M1 duplicate bom-ref.
/// CLI notes: no hardcoded component totals asserted (counts depend on live
/// resolver output for the npm fixture); only exit codes + envelope keys,
/// so no probe-derived count citation is required (Wave 5b rule).
/// </summary>
public sealed class CycloneDxXmlFormatterTests
{
    private const string BomNamespace = "http://cyclonedx.org/schema/bom/1.5";

    private static ScanResult MixedXmlScanResult() => new(new List<ResolvedLicense>
    {
        // Deliberately unsorted: pip first, then transitive npm, then direct npm.
        new(
            new Dependency("pip", "requests", "2.31.0", false),
            "MIT",
            "MIT License",
            "https://example.com/requests/LICENSE",
            "Resolved",
            null),
        new(
            new Dependency("npm", "shadow-dep", "1.0.0", true),
            null,
            null,
            null,
            "Unknown",
            "not-found: no license for 'shadow-dep 1.0.0'."),
        new(
            new Dependency("npm", "express", "4.18.2", false),
            "MIT",
            "MIT License\nPermission is hereby granted, free of charge,",
            "https://example.com/express/LICENSE",
            "Resolved",
            null),
    });

    private static XmlDocument LoadXml(string output)
    {
        var doc = new XmlDocument();
        doc.LoadXml(output); // throws if not well-formed XML
        return doc;
    }

    private static XmlNamespaceManager Ns(XmlDocument doc)
    {
        var manager = new XmlNamespaceManager(doc.NameTable);
        manager.AddNamespace("c", BomNamespace);
        return manager;
    }

    private static List<string> ChildElementNames(XmlNode node)
    {
        return node.ChildNodes.Cast<XmlNode>()
            .Where(n => n.NodeType == XmlNodeType.Element)
            .Select(n => n.LocalName)
            .ToList();
    }

    private static XmlNode RequireSingle(XmlDocument doc, XmlNamespaceManager manager, string xpath)
    {
        var node = doc.SelectSingleNode(xpath, manager);
        Assert.NotNull(node);
        return node!;
    }

    private static List<XmlNode> ComponentNodes(XmlDocument doc, XmlNamespaceManager manager)
    {
        var nodes = doc.SelectNodes("/c:bom/c:components/c:component", manager);
        Assert.NotNull(nodes);
        return nodes!.Cast<XmlNode>().ToList();
    }

    private static string? ComponentProperty(XmlNode component, XmlNamespaceManager manager, string propertyName)
    {
        var properties = component.SelectNodes("c:properties/c:property", manager);
        if (properties is null)
        {
            return null;
        }

        foreach (XmlNode property in properties)
        {
            if (property.Attributes!["name"]!.Value == propertyName)
            {
                return property.InnerText;
            }
        }

        return null;
    }

    // F1: envelope (serial regex + freshness, never golden).
    [Fact]
    public void Should_PinEnvelope_When_MixedScanResult()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml");
        var output = formatter.FormatResult(MixedXmlScanResult());

        var doc = LoadXml(output);
        var root = doc.DocumentElement;
        Assert.NotNull(root);
        Assert.Equal("bom", root.LocalName);
        Assert.Equal(BomNamespace, root.NamespaceURI);
        Assert.Null(root.Attributes["specVersion"]);
        Assert.Equal("1", root.Attributes["version"]!.Value);
        Assert.Matches(@"^urn:uuid:[0-9a-fA-F-]{36}$", root.Attributes["serialNumber"]!.Value);

        // Bom children order: metadata then components.
        Assert.Equal(new[] { "metadata", "components" }, ChildElementNames(root));

        // Fresh serial per call + ISO-8601 timestamp shape (never golden-matched).
        var second = LoadXml(formatter.FormatResult(MixedXmlScanResult()));
        var secondSerial = second.DocumentElement!.Attributes!["serialNumber"]!.Value;
        Assert.Matches(@"^urn:uuid:[0-9a-fA-F-]{36}$", secondSerial);
        Assert.NotEqual(root.Attributes["serialNumber"]!.Value, secondSerial);
        FormatterTestHelpers.AssertIso8601(second.SelectSingleNode("/c:bom/c:metadata/c:timestamp", Ns(second))!.InnerText);
    }

    // F2: metadata order + content.
    [Fact]
    public void Should_PinMetadataOrderAndContent_When_MixedScanResult()
    {
        var scan = MixedXmlScanResult();
        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(scan);

        var doc = LoadXml(output);
        var manager = Ns(doc);
        var metadata = RequireSingle(doc, manager, "/c:bom/c:metadata");

        // Metadata children order: timestamp, tools, component, properties.
        Assert.Equal(new[] { "timestamp", "tools", "component", "properties" }, ChildElementNames(metadata));

        FormatterTestHelpers.AssertIso8601(metadata.SelectSingleNode("c:timestamp", manager)!.InnerText);
        var tool = RequireSingle(doc, manager, "/c:bom/c:metadata/c:tools/c:tool");
        Assert.Equal("olaf", tool.SelectSingleNode("c:vendor", manager)!.InnerText);
        Assert.Equal("olaf", tool.SelectSingleNode("c:name", manager)!.InnerText);
        Assert.False(string.IsNullOrWhiteSpace(tool.SelectSingleNode("c:version", manager)!.InnerText));

        var appComponent = RequireSingle(doc, manager, "/c:bom/c:metadata/c:component");
        Assert.Equal("application", appComponent.Attributes!["type"]!.Value);
        Assert.Equal("olaf-scan", appComponent.SelectSingleNode("c:name", manager)!.InnerText);

        Assert.Equal(scan.TotalCount.ToString(), ComponentProperty(metadata, manager, "olaf:total"));
        Assert.Equal(scan.ResolvedCount.ToString(), ComponentProperty(metadata, manager, "olaf:resolved"));
        Assert.Equal(scan.UnknownCount.ToString(), ComponentProperty(metadata, manager, "olaf:unknown"));
        Assert.Equal(3, metadata.SelectNodes("c:properties/c:property", manager)!.Count);
    }

    // F3: empty.
    [Fact]
    public void Should_EmitEmptyComponents_When_ScanResultEmpty()
    {
        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(ScanResult.Empty);

        var doc = LoadXml(output);
        var manager = Ns(doc);
        Assert.Equal(BomNamespace, doc.DocumentElement!.NamespaceURI);
        var components = RequireSingle(doc, manager, "/c:bom/c:components");
        Assert.Empty(ComponentNodes(doc, manager));
        var metadata = RequireSingle(doc, manager, "/c:bom/c:metadata");
        Assert.Equal("0", ComponentProperty(metadata, manager, "olaf:total"));
        Assert.Equal("0", ComponentProperty(metadata, manager, "olaf:resolved"));
        Assert.Equal("0", ComponentProperty(metadata, manager, "olaf:unknown"));
        Assert.NotNull(components);
    }

    // F4: license-id.
    [Fact]
    public void Should_EmitLicenseId_When_SingleTokenSpdx()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "express", "4.18.2", false),
                "MIT",
                "MIT License",
                "https://example.com/express/LICENSE",
                "Resolved",
                null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(scan);

        var doc = LoadXml(output);
        var manager = Ns(doc);
        var component = ComponentNodes(doc, manager).Single();
        var id = component.SelectSingleNode("c:licenses/c:license/c:id", manager);
        Assert.NotNull(id);
        Assert.Equal("MIT", id!.InnerText);
        Assert.Null(component.SelectSingleNode("c:licenses/c:license/c:name", manager));
    }

    // F5: license-name.
    [Fact]
    public void Should_EmitLicenseName_When_MultiWordSpdx()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "custom-pkg", "1.0.0", false),
                "My Custom License",
                "Full custom text",
                null,
                "Resolved",
                null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(scan);

        var doc = LoadXml(output);
        var manager = Ns(doc);
        var component = ComponentNodes(doc, manager).Single();
        var name = component.SelectSingleNode("c:licenses/c:license/c:name", manager);
        Assert.NotNull(name);
        Assert.Equal("My Custom License", name!.InnerText);
        Assert.Null(component.SelectSingleNode("c:licenses/c:license/c:id", manager));
    }

    // F6: unknown-props.
    [Fact]
    public void Should_EmitStatusProperties_When_LicenseUnknown()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "mystery-pkg", "1.0.0", false),
                null,
                null,
                "https://example.com/mystery",
                "Unknown",
                "not-found: no license for 'mystery-pkg 1.0.0'."),
            new(
                new Dependency("npm", "no-source-pkg", "2.0.0", true),
                null,
                null,
                null,
                "Unknown",
                "not-found."),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(scan);

        var doc = LoadXml(output);
        var manager = Ns(doc);
        var components = ComponentNodes(doc, manager);
        var mystery = FormatterTestHelpers.FindByName(components, c => c.SelectSingleNode("c:name", manager)?.InnerText, "CycloneDX XML <components>", "mystery-pkg");
        Assert.Null(mystery.SelectSingleNode("c:licenses", manager));
        Assert.Equal("Unknown", ComponentProperty(mystery, manager, "olaf:status"));
        Assert.Equal("not-found: no license for 'mystery-pkg 1.0.0'.", ComponentProperty(mystery, manager, "olaf:reason"));
        Assert.Equal("https://example.com/mystery", ComponentProperty(mystery, manager, "olaf:sourceUrl"));

        // sourceUrl property present only when non-empty.
        var noSource = FormatterTestHelpers.FindByName(components, c => c.SelectSingleNode("c:name", manager)?.InnerText, "CycloneDX XML <components>", "no-source-pkg");
        Assert.Null(noSource.SelectSingleNode("c:licenses", manager));
        Assert.Equal("Unknown", ComponentProperty(noSource, manager, "olaf:status"));
        Assert.Null(ComponentProperty(noSource, manager, "olaf:sourceUrl"));
    }

    // F7: escaping adversarial round-trip.
    [Fact]
    public void Should_RoundTripSpecialChars_When_FieldsContainMarkup()
    {
        const string evilName = "evil<script>&\"'pkg";
        const string evilVersion = "1.0 <beta>&\"1\"";
        const string evilReason = "needs \"review\" & <fix> 'now'";
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", evilName, evilVersion, false),
                "MIT",
                "text with <b>markup</b> & \"quotes\"",
                "https://example.com/?a=1&b=2",
                "Resolved",
                evilReason),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(scan);

        Assert.DoesNotContain("<script>", output, StringComparison.Ordinal);
        var doc = LoadXml(output); // throws if escaping broke well-formedness
        var manager = Ns(doc);
        var component = ComponentNodes(doc, manager).Single();
        Assert.Equal(evilName, component.SelectSingleNode("c:name", manager)!.InnerText);
        Assert.Equal(evilVersion, component.SelectSingleNode("c:version", manager)!.InnerText);
        Assert.Equal($"pkg:npm/{evilName}@{evilVersion}", component.SelectSingleNode("c:purl", manager)!.InnerText);
        Assert.Equal($"npm:{evilName}@{evilVersion}", component.Attributes!["bom-ref"]!.Value);
        Assert.Equal("MIT", component.SelectSingleNode("c:licenses/c:license/c:id", manager)!.InnerText);
    }

    // F8: control-char sanitize.
    [Fact]
    public void Should_StripControlChars_When_FieldsContainInvalidXmlChars()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "ctrlpkg", "1.0.0", false),
                "MIT",
                null,
                null,
                "Resolved",
                null),
        });

        // Must never throw: invalid XML chars are stripped, rest intact.
        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(scan);

        Assert.DoesNotContain("", output, StringComparison.Ordinal);
        var doc = LoadXml(output);
        var manager = Ns(doc);
        var component = ComponentNodes(doc, manager).Single();
        Assert.Equal("ctrlpkg", component.SelectSingleNode("c:name", manager)!.InnerText);
    }

    // F9: sorted/scope/purl/group.
    [Fact]
    public void Should_PinOrderScopePurlGroup_When_MixedEcosystems()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            // Deliberately unsorted input: npm, conan, maven, pip.
            new(new Dependency("npm", "@scope/name", "1.0.0", true), "MIT", null, null, "Resolved", null),
            new(new Dependency("conan", "fmt", "10.2.1", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("maven", "org.example:artifact", "2.0.0", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("pip", "requests", "2.31.0", false), "MIT", null, null, "Resolved", null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(scan);

        var doc = LoadXml(output);
        var manager = Ns(doc);
        var components = ComponentNodes(doc, manager);
        Assert.Equal(4, components.Count);

        // Sorted by (ecosystem, name, version): conan < maven < npm < pip.
        var names = components
            .Select(c => c.SelectSingleNode("c:name", manager)!.InnerText)
            .ToArray();
        Assert.Equal(new[] { "fmt", "org.example:artifact", "@scope/name", "requests" }, names);

        // Scope element always present: direct -> required, transitive -> optional.
        var scopes = components
            .Select(c => c.SelectSingleNode("c:scope", manager)!.InnerText)
            .ToArray();
        Assert.Equal(new[] { "required", "required", "optional", "required" }, scopes);

        // Purl emitted unconditionally on every component (issue #70 B4:
        // conan has a dedicated purl type, never generic).
        var purls = components
            .Select(c => c.SelectSingleNode("c:purl", manager)!.InnerText)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "pkg:conan/fmt@10.2.1",
                "pkg:maven/org.example/artifact@2.0.0",
                "pkg:npm/%40scope/name@1.0.0",
                "pkg:pypi/requests@2.31.0",
            },
            purls);

        // Group only on maven/gradle group:artifact coordinates.
        var maven = FormatterTestHelpers.FindByName(components, c => c.SelectSingleNode("c:name", manager)?.InnerText, "CycloneDX XML <components>", "org.example:artifact");
        Assert.Equal("org.example", maven.SelectSingleNode("c:group", manager)!.InnerText);
        Assert.Null(FormatterTestHelpers.FindByName(components, c => c.SelectSingleNode("c:name", manager)?.InnerText, "CycloneDX XML <components>", "fmt").SelectSingleNode("c:group", manager));
        Assert.Null(FormatterTestHelpers.FindByName(components, c => c.SelectSingleNode("c:name", manager)?.InnerText, "CycloneDX XML <components>", "@scope/name").SelectSingleNode("c:group", manager));

        // Component children order: name, version, scope, licenses, purl (no group/properties here except maven).
        var plain = FormatterTestHelpers.FindByName(components, c => c.SelectSingleNode("c:name", manager)?.InnerText, "CycloneDX XML <components>", "fmt");
        Assert.Equal(new[] { "name", "version", "scope", "licenses", "purl" }, ChildElementNames(plain));
        Assert.Equal(
            new[] { "group", "name", "version", "scope", "licenses", "purl" },
            ChildElementNames(maven));

        // Every component pins the library type attribute.
        foreach (var component in components)
        {
            Assert.Equal("library", component.Attributes!["type"]!.Value);
            Assert.False(string.IsNullOrWhiteSpace(component.Attributes!["bom-ref"]!.Value));
        }
    }

    // P1: JSON shape snapshot (mapper-extraction guard).
    [Fact]
    public void Should_PinJsonEnvelopeShape_When_MixedScanResult()
    {
        // The CycloneDxComponentMapper extraction must leave JSON output
        // unchanged; value coverage lives in CycloneDxFormatterTests — here
        // the envelope/component key shape is pinned so a JSON regression
        // fails in this file too.
        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(MixedXmlScanResult());

        using var parsed = JsonDocument.Parse(output);
        var root = parsed.RootElement;
        Assert.Equal(
            new[] { "bomFormat", "components", "metadata", "serialNumber", "specVersion", "version" },
            root.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("CycloneDX", root.GetProperty("bomFormat").GetString());
        Assert.Equal("1.5", root.GetProperty("specVersion").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Matches(@"^urn:uuid:[0-9a-fA-F-]{36}$", root.GetProperty("serialNumber").GetString() ?? string.Empty);

        var metadataKeys = root.GetProperty("metadata").EnumerateObject()
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "component", "properties", "timestamp", "tools" }, metadataKeys);

        var components = root.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(3, components.Count);
        foreach (var component in components)
        {
            var keys = component.EnumerateObject()
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(
                new[] { "bom-ref", "licenses", "name", "properties", "purl", "scope", "type", "version" },
                keys);
        }
    }

    // P2: JSON<->XML field parity.
    [Fact]
    public void Should_MatchJsonFields_When_SameScanResult()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("npm", "express", "4.18.2", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("npm", "custom-pkg", "1.0.0", false), "My Custom License", null, null, "Resolved", null),
            new(new Dependency("npm", "mystery-pkg", "1.0.0", true), null, null, null, "Unknown", "not-found."),
            new(new Dependency("maven", "org.example:artifact", "2.0.0", false), "MIT", null, null, "Resolved", null),
        });

        using var parsed = JsonDocument.Parse(
            FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(scan));
        var jsonComponents = parsed.RootElement.GetProperty("components").EnumerateArray().ToList();

        var doc = LoadXml(FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(scan));
        var manager = Ns(doc);
        var xmlComponents = ComponentNodes(doc, manager);

        Assert.Equal(jsonComponents.Count, xmlComponents.Count);
        for (var i = 0; i < jsonComponents.Count; i++)
        {
            var json = jsonComponents[i];
            var xml = xmlComponents[i];
            Assert.Equal(json.GetProperty("name").GetString(), xml.SelectSingleNode("c:name", manager)!.InnerText);
            Assert.Equal(json.GetProperty("version").GetString(), xml.SelectSingleNode("c:version", manager)!.InnerText);
            Assert.Equal(json.GetProperty("purl").GetString(), xml.SelectSingleNode("c:purl", manager)!.InnerText);
            Assert.Equal(json.GetProperty("bom-ref").GetString(), xml.Attributes!["bom-ref"]!.Value);
            Assert.Equal(json.GetProperty("scope").GetString(), xml.SelectSingleNode("c:scope", manager)!.InnerText);

            var jsonLicenses = json.GetProperty("licenses").EnumerateArray().ToList();
            var xmlLicenses = xml.SelectSingleNode("c:licenses", manager);
            if (jsonLicenses.Count == 0)
            {
                Assert.Null(xmlLicenses);
            }
            else
            {
                Assert.NotNull(xmlLicenses);
                var jsonLicense = jsonLicenses.Single().GetProperty("license");
                if (jsonLicense.TryGetProperty("id", out var id))
                {
                    Assert.Equal(id.GetString(), xml.SelectSingleNode("c:licenses/c:license/c:id", manager)!.InnerText);
                }
                else
                {
                    Assert.Equal(
                        jsonLicense.GetProperty("name").GetString(),
                        xml.SelectSingleNode("c:licenses/c:license/c:name", manager)!.InnerText);
                }
            }
        }
    }

    // R1: registry + help.
    [Fact]
    public void Should_ResolveViaRegistry_When_FormatCycloneDxXml()
    {
        var xml = FormatterTestHelpers.GetFormatterViaRegistry("cyclonedx-xml");

        Assert.NotNull(xml);
        Assert.Equal("cyclonedx-xml", xml.Format, StringComparer.OrdinalIgnoreCase);

        // toml still throws; the message names the new format.
        var thrown = Assert.Throws<ArgumentException>(() => new FormatterRegistry().GetFormatter("toml"));
        Assert.Contains("cyclonedx-xml", thrown.Message, StringComparison.Ordinal);

        // generate --help lists the new format (automated assert).
        var help = CliTestHelpers.RunCli("generate", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("cyclonedx-xml", help.Stdout + help.Stderr, StringComparison.Ordinal);
    }

    // R2: CLI exit codes.
    [Fact]
    public void Should_ExitZeroWithBom_When_FormatCycloneDxXml()
    {
        var dir = CliTestHelpers.CreateTempDir();
        try
        {
            File.Copy(CliTestHelpers.FixturePath("npm", "package.json"), Path.Combine(dir, "package.json"));

            var result = CliTestHelpers.RunCli("generate", dir, "--format", "cyclonedx-xml");

            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
            var doc = LoadXml(result.Stdout);
            Assert.Equal("bom", doc.DocumentElement!.LocalName);
            Assert.Equal(BomNamespace, doc.DocumentElement.NamespaceURI);
            Assert.Matches(@"^urn:uuid:[0-9a-fA-F-]{36}$", doc.DocumentElement.Attributes!["serialNumber"]!.Value);
            Assert.NotNull(doc.SelectSingleNode("/c:bom/c:components", Ns(doc)));

            // Bad-format exit-2 contract preserved.
            var badFormat = CliTestHelpers.RunCli("generate", dir, "--format", "toml");
            Assert.Equal(2, badFormat.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    // M1: duplicate bom-ref margin.
    [Fact]
    public void Should_SuffixBomRef_When_DuplicateDependencies()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("npm", "dup", "1.0.0", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("npm", "dup", "1.0.0", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("npm", "dup", "1.0.0", false), "MIT", null, null, "Resolved", null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(scan);

        var doc = LoadXml(output);
        var refs = ComponentNodes(doc, Ns(doc))
            .Select(c => c.Attributes!["bom-ref"]!.Value)
            .ToList();
        Assert.Equal(3, refs.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            new[] { "npm:dup@1.0.0", "npm:dup@1.0.0-2", "npm:dup@1.0.0-3" },
            refs.OrderBy(r => r, StringComparer.Ordinal).ToArray());
    }

}
