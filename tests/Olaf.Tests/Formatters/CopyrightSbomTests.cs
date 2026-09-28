using System.Text.Json;
using System.Xml;
using Olaf.Core;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #72 R3: SBOM outputs — SPDX <c>copyrightText</c> (joined holders
/// vs literal NOASSERTION) and CDX JSON+XML <c>evidence.copyright</c>
/// (array-of-{text}, omit-when-empty). Offline unit style — ScanResult
/// constructed directly, no network; inline strings only.
/// </summary>
public sealed class CopyrightSbomTests
{
    private const string BomNamespace = "http://cyclonedx.org/schema/bom/1.5";

    private static ScanResult HoldersScanResult() => new(new List<ResolvedLicense>
    {
        new(
            new Dependency("npm", "acme", "1.0.0", false),
            "MIT",
            "MIT License",
            "https://example.com/acme",
            "Resolved",
            null,
            new Enrichment(
                "pkg:npm/acme@1.0.0",
                null,
                "Acme Corp",
                null,
                new[] { "Acme Corp", "Jane Doe" })),
    });

    private static ScanResult UnenrichedScanResult() => new(new List<ResolvedLicense>
    {
        new(new Dependency("npm", "acme", "1.0.0", false), "MIT", null, null, "Resolved", null),
    });

    [Fact]
    public void Should_JoinHoldersOrNoassertion_When_SpdxCopyrightText()
    {
        using var enriched = JsonDocument.Parse(
            FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(HoldersScanResult()));
        var package = enriched.RootElement.GetProperty("packages").EnumerateArray().Single();
        Assert.Equal("Acme Corp; Jane Doe", package.GetProperty("copyrightText").GetString());

        using var plain = JsonDocument.Parse(
            FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(UnenrichedScanResult()));
        var plainPackage = plain.RootElement.GetProperty("packages").EnumerateArray().Single();
        Assert.Equal("NOASSERTION", plainPackage.GetProperty("copyrightText").GetString());
    }

    [Fact]
    public void Should_EmitOrOmitEvidence_When_CycloneDxCopyright()
    {
        using var enriched = JsonDocument.Parse(
            FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(HoldersScanResult()));
        var component = enriched.RootElement.GetProperty("components").EnumerateArray().Single();
        var copyright = component.GetProperty("evidence").GetProperty("copyright").EnumerateArray().ToList();
        Assert.Equal(2, copyright.Count);
        Assert.Equal("Acme Corp", copyright[0].GetProperty("text").GetString());
        Assert.Equal("Jane Doe", copyright[1].GetProperty("text").GetString());

        using var plain = JsonDocument.Parse(
            FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(UnenrichedScanResult()));
        var plainComponent = plain.RootElement.GetProperty("components").EnumerateArray().Single();
        Assert.False(plainComponent.TryGetProperty("evidence", out _));

        var xml = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(HoldersScanResult());
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var manager = new XmlNamespaceManager(doc.NameTable);
        manager.AddNamespace("c", BomNamespace);
        var texts = doc.SelectNodes("/c:bom/c:components/c:component/c:evidence/c:copyright/c:text", manager);
        Assert.NotNull(texts);
        Assert.Equal(2, texts.Count);
        Assert.Equal("Acme Corp", texts[0]!.InnerText);
        Assert.Equal("Jane Doe", texts[1]!.InnerText);

        var plainXml = FormatterTestHelpers.ResolveFormatter("cyclonedx-xml").FormatResult(UnenrichedScanResult());
        var plainDoc = new XmlDocument();
        plainDoc.LoadXml(plainXml);
        var plainManager = new XmlNamespaceManager(plainDoc.NameTable);
        plainManager.AddNamespace("c", BomNamespace);
        Assert.Null(plainDoc.SelectSingleNode("/c:bom/c:components/c:component/c:evidence", plainManager));
    }
}
