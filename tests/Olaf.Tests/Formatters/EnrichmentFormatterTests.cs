using System.Text.Json;
using System.Xml;
using Olaf.Core;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #70 B5+B10: SBOM + legacy formatters consume `ResolvedLicense.Enrichment`
/// with omit-null everywhere (unenriched output is byte-stable). Offline unit
/// style — ScanResult constructed directly, no network. Contracts: CDX JSON
/// optional order `group/supplier/hashes/externalReferences` (distribution);
/// CDX XML enrichment slots after `purl`, before `properties`; SPDX supplier
/// `Person: {name}` + `checksums[]` only when hashes split on the first colon
/// (unparseable entries dropped, never emitted); legacy json/yaml enrichment
/// keys trail AFTER `direct`, omitted-when-null. Every XML select goes through
/// an XmlNamespaceManager (`c:` prefix); purl asserted unconditionally.
/// </summary>
public sealed class EnrichmentFormatterTests
{
    private const string BomNamespace = "http://cyclonedx.org/schema/bom/1.5";

    private static readonly string[] ExpectedSpdxEnrichedOrder =
    {
        "SPDXID", "name", "versionInfo", "supplier", "downloadLocation",
        "filesAnalyzed", "licenseConcluded", "licenseDeclared",
        "copyrightText", "externalRefs", "checksums",
    };

    private static ScanResult EnrichedMavenScanResult() => new(new List<ResolvedLicense>
    {
        new(
            new Dependency("maven", "org.example:artifact", "2.0.0", false),
            "MIT",
            "MIT License",
            "https://example.com/artifact",
            "Resolved",
            null,
            new Enrichment(
                "pkg:maven/org.example/artifact@2.0.0",
                new[] { "sha512:abc==" },
                "Example Org",
                "https://example.com/artifact-2.0.0.jar")),
    });

    private static XmlNamespaceManager Ns(XmlDocument doc)
    {
        var manager = new XmlNamespaceManager(doc.NameTable);
        manager.AddNamespace("c", BomNamespace);
        return manager;
    }

    [Fact]
    public void Should_EmitSupplierHashesExternalReferences_When_CycloneDxJsonEnriched()
    {
        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(EnrichedMavenScanResult());

        using var doc = JsonDocument.Parse(output);
        var component = doc.RootElement.GetProperty("components").EnumerateArray().Single();
        Assert.Equal("Example Org", component.GetProperty("supplier").GetProperty("name").GetString());
        var hash = component.GetProperty("hashes").EnumerateArray().Single();
        Assert.Equal("SHA-512", hash.GetProperty("alg").GetString());
        Assert.Equal("abc==", hash.GetProperty("content").GetString());
        var reference = component.GetProperty("externalReferences").EnumerateArray().Single();
        Assert.Equal("distribution", reference.GetProperty("type").GetString());
        Assert.Equal("https://example.com/artifact-2.0.0.jar", reference.GetProperty("url").GetString());

        // Pinned optional order: group, supplier, hashes, externalReferences.
        Assert.Equal(
            new[] { "type", "name", "version", "purl", "bom-ref", "scope", "licenses", "properties", "group", "supplier", "hashes", "externalReferences" },
            component.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Should_EmitEnrichmentAfterPurl_When_CycloneDxXmlEnriched()
    {
        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(EnrichedMavenScanResult());

        var doc = new XmlDocument();
        doc.LoadXml(output);
        var manager = Ns(doc);
        var component = doc.SelectSingleNode("/c:bom/c:components/c:component", manager);
        Assert.NotNull(component);
        Assert.Equal("Example Org", component.SelectSingleNode("c:supplier/c:name", manager)!.InnerText);
        var hash = component.SelectSingleNode("c:hashes/c:hash", manager);
        Assert.NotNull(hash);
        Assert.Equal("SHA-512", hash.Attributes!["alg"]!.Value);
        Assert.Equal("abc==", hash.InnerText);
        var reference = component.SelectSingleNode("c:externalReferences/c:reference", manager);
        Assert.NotNull(reference);
        Assert.Equal("distribution", reference.Attributes!["type"]!.Value);
        Assert.Equal("https://example.com/artifact-2.0.0.jar", reference.SelectSingleNode("c:url", manager)!.InnerText);

        // Pinned child order: enrichment slots after purl, before properties.
        var names = component.ChildNodes.Cast<XmlNode>()
            .Where(n => n.NodeType == XmlNodeType.Element)
            .Select(n => n.LocalName)
            .ToList();
        Assert.Equal(
            new[] { "group", "name", "version", "scope", "licenses", "purl", "supplier", "hashes", "externalReferences" },
            names);
    }

    [Fact]
    public void Should_EmitPersonSupplierDownloadChecksums_When_SpdxEnriched()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "express", "4.18.2", false),
                "MIT",
                "MIT License",
                "https://www.npmjs.com/package/express/v/4.18.2",
                "Resolved",
                null,
                new Enrichment(
                    "pkg:npm/express@4.18.2",
                    new[] { "sha512:abc==", "no-colon-entry", string.Empty },
                    "TJ Holowaychuk",
                    "https://registry.npmjs.org/express/-/express-4.18.2.tgz")),
        });

        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(scan);

        using var doc = JsonDocument.Parse(output);
        var package = doc.RootElement.GetProperty("packages").EnumerateArray().Single();
        Assert.Equal("Person: TJ Holowaychuk", package.GetProperty("supplier").GetString());
        Assert.Equal("https://registry.npmjs.org/express/-/express-4.18.2.tgz", package.GetProperty("downloadLocation").GetString());

        // checksums split "algo:value" on the first colon; unparseable dropped.
        var checksum = package.GetProperty("checksums").EnumerateArray().Single();
        Assert.Equal("SHA512", checksum.GetProperty("algorithm").GetString());
        Assert.Equal("abc==", checksum.GetProperty("checksumValue").GetString());

        Assert.Equal(ExpectedSpdxEnrichedOrder, package.EnumerateObject().Select(p => p.Name).ToArray());

        // Per-id relations + closure (never literal counts).
        var id = package.GetProperty("SPDXID").GetString()!;
        var kinds = doc.RootElement.GetProperty("relationships").EnumerateArray()
            .Where(r => r.GetProperty("relatedSpdxElement").GetString() == id)
            .Select(r => r.GetProperty("relationshipType").GetString())
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "CONTAINS", "DESCRIBES" }, kinds);
    }

    [Fact]
    public void Should_PreserveNoassertionWithoutChecksums_When_SpdxUnenriched()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "express", "4.18.2", false),
                "MIT",
                "MIT License",
                "https://www.npmjs.com/package/express/v/4.18.2",
                "Resolved",
                null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(scan);

        using var doc = JsonDocument.Parse(output);
        var package = doc.RootElement.GetProperty("packages").EnumerateArray().Single();
        Assert.Equal("NOASSERTION", package.GetProperty("supplier").GetString());
        Assert.Equal("NOASSERTION", package.GetProperty("downloadLocation").GetString());
        Assert.False(package.TryGetProperty("checksums", out _));
    }

    [Fact]
    public void Should_OmitNullEnrichmentKeys_When_CycloneDxJsonUnenriched()
    {
        var unenriched = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("npm", "express", "4.18.2", false), "MIT", null, null, "Resolved", null),
        });
        var unparseable = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "express", "4.18.2", false),
                "MIT",
                null,
                null,
                "Resolved",
                null,
                new Enrichment("pkg:npm/express@4.18.2", new[] { "bogus", string.Empty }, "TJ", "https://example.com/express.tgz")),
        });
        var formatter = FormatterTestHelpers.ResolveFormatter("cyclonedx-json");

        using var plain = JsonDocument.Parse(formatter.FormatResult(unenriched));
        var plainComponent = plain.RootElement.GetProperty("components").EnumerateArray().Single();
        Assert.False(plainComponent.TryGetProperty("supplier", out _));
        Assert.False(plainComponent.TryGetProperty("hashes", out _));
        Assert.False(plainComponent.TryGetProperty("externalReferences", out _));

        // Unparseable-only hashes drop the hashes key; other slots still emit.
        using var parsed = JsonDocument.Parse(formatter.FormatResult(unparseable));
        var component = parsed.RootElement.GetProperty("components").EnumerateArray().Single();
        Assert.False(component.TryGetProperty("hashes", out _));
        Assert.Equal("TJ", component.GetProperty("supplier").GetProperty("name").GetString());
        Assert.True(component.TryGetProperty("externalReferences", out _));
    }

    [Fact]
    public void Should_EmitEnrichmentKeysAfterDirect_When_LegacyJsonEnriched()
    {
        var output = FormatterTestHelpers.ResolveFormatter("json").FormatResult(EnrichedMavenScanResult());

        using var doc = JsonDocument.Parse(output);
        var entry = doc.RootElement.GetProperty("licenses").EnumerateArray().Single();
        Assert.Equal(
            new[] { "ecosystem", "name", "version", "spdx", "licenseText", "sourceUrl", "status", "reason", "direct", "purl", "supplier", "downloadUrl", "hashes" },
            entry.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("pkg:maven/org.example/artifact@2.0.0", entry.GetProperty("purl").GetString());
        Assert.Equal("Example Org", entry.GetProperty("supplier").GetString());
        Assert.Equal("https://example.com/artifact-2.0.0.jar", entry.GetProperty("downloadUrl").GetString());
        Assert.Equal(new[] { "sha512:abc==" }, entry.GetProperty("hashes").EnumerateArray().Select(h => h.GetString()).ToArray());
    }

    [Fact]
    public void Should_OmitEnrichmentKeys_When_LegacyJsonUnenriched()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("npm", "express", "4.18.2", false), "MIT", null, null, "Resolved", null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("json").FormatResult(scan);

        using var doc = JsonDocument.Parse(output);
        var entry = doc.RootElement.GetProperty("licenses").EnumerateArray().Single();
        Assert.False(entry.TryGetProperty("purl", out _));
        Assert.False(entry.TryGetProperty("supplier", out _));
        Assert.False(entry.TryGetProperty("downloadUrl", out _));
        Assert.False(entry.TryGetProperty("hashes", out _));
    }

    [Fact]
    public void Should_EmitEnrichmentKeys_When_LegacyYamlEnriched()
    {
        var output = FormatterTestHelpers.ResolveFormatter("yaml").FormatResult(EnrichedMavenScanResult());

        Assert.Contains("purl:", output, StringComparison.Ordinal);
        Assert.Contains("pkg:maven/org.example/artifact@2.0.0", output, StringComparison.Ordinal);
        Assert.Contains("supplier:", output, StringComparison.Ordinal);
        Assert.Contains("Example Org", output, StringComparison.Ordinal);
        Assert.Contains("downloadUrl:", output, StringComparison.Ordinal);
        Assert.Contains("https://example.com/artifact-2.0.0.jar", output, StringComparison.Ordinal);
        Assert.Contains("hashes:", output, StringComparison.Ordinal);
        Assert.Contains("sha512:abc==", output, StringComparison.Ordinal);
    }
}
