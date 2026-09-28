using System.Globalization;
using System.Xml;
using Olaf.Core;
using Olaf.Tests.Resolvers;

namespace Olaf.Tests.Formatters;

/// <summary>
/// E2E: real npm registry (https://registry.npmjs.org) -> ScanResult -> XmlFormatter.
/// Resolves exactly 3 pinned packages per Fact (express@4.18.2 + lodash@4.17.21
/// resolved MIT, one phantom package Unknown), then formats once. Marked with
/// [Trait("Category", "E2E")] so CI can filter with --filter "Category!=E2E".
/// XML divergences: spdx/reason null render as empty elements (InnerText == "",
/// NOT null/Unknown), summary is /report/summary attributes, no XML namespace.
/// e2e_trait_gate: every Fact below carries [Trait("Category", "E2E")] (4 Facts,
/// 0 Theories) so the Category!=E2E gate excludes this live-network file.
/// </summary>
public sealed class XmlReportE2ETests
{
    private const string PhantomName = "this-package-definitely-does-not-exist-olaf-xyz";

    private static HttpClient CreateRealClient()
    {
        return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static async Task<(ScanResult Scan, string Output)> BuildRealXmlReportAsync(bool phantomFirst = false)
    {
        using var http = CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var express = await resolver.ResolveAsync(new Dependency("npm", "express", "4.18.2", false));
        var lodash = await resolver.ResolveAsync(new Dependency("npm", "lodash", "4.17.21", false));
        var phantom = await resolver.ResolveAsync(new Dependency("npm", PhantomName, "9.9.9", false));

        var licenses = phantomFirst
            ? new List<ResolvedLicense> { phantom, lodash, express }
            : new List<ResolvedLicense> { express, lodash, phantom };
        var scan = new ScanResult(licenses);
        var formatter = FormatterTestHelpers.ResolveFormatter("xml");
        return (scan, formatter.FormatResult(scan));
    }

    private static XmlDocument LoadXmlReport(string output)
    {
        var doc = new XmlDocument();
        doc.LoadXml(output); // throws XmlException if not well-formed XML
        return doc;
    }

    private static XmlNode LicenseByName(XmlNodeList licenses, string name)
    {
        return FormatterTestHelpers.FindByName(
            licenses.Cast<XmlNode>(),
            n => n.SelectSingleNode("name")?.InnerText,
            "XML //license",
            name);
    }

    private static string ChildText(XmlNode license, string child)
    {
        return license.SelectSingleNode(child)?.InnerText ?? string.Empty;
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ProduceEnvelope_When_RealNpmScan()
    {
        var (scan, output) = await BuildRealXmlReportAsync();

        var doc = LoadXmlReport(output); // throws if not well-formed XML
        Assert.Equal("report", doc.DocumentElement!.Name);
        Assert.Equal(3, doc.SelectNodes("//license")!.Count);

        // Summary cross-check vs ScanResult counts.
        var summary = doc.SelectSingleNode("/report/summary");
        Assert.NotNull(summary);
        var total = int.Parse(summary.Attributes!["total"]!.Value, CultureInfo.InvariantCulture);
        var resolved = int.Parse(summary.Attributes!["resolved"]!.Value, CultureInfo.InvariantCulture);
        var unknown = int.Parse(summary.Attributes!["unknown"]!.Value, CultureInfo.InvariantCulture);
        Assert.Equal(3, total);
        Assert.True(unknown >= 1, "Expected at least the phantom package to be Unknown.");
        Assert.Equal(3, resolved + unknown);
        Assert.Equal(scan.TotalCount, total);
        Assert.Equal(scan.ResolvedCount, resolved);
        Assert.Equal(scan.UnknownCount, unknown);
        FormatterTestHelpers.AssertSummaryCounts(
            output,
            total: scan.TotalCount,
            resolved: scan.ResolvedCount,
            unknown: scan.UnknownCount);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ContainEightFieldsPerEntry_When_RealNpmScan()
    {
        var (_, output) = await BuildRealXmlReportAsync();

        var doc = LoadXmlReport(output);
        var licenses = doc.SelectNodes("//license")!;
        Assert.Equal(3, licenses.Count);
        foreach (XmlNode license in licenses)
        {
            foreach (var child in new[] { "ecosystem", "name", "version", "spdx", "licenseText", "sourceUrl", "status", "reason", "direct" })
            {
                Assert.True(license.SelectSingleNode(child) is not null, $"XML //license entry missing <{child}>.");
            }
        }

        FormatterTestHelpers.AssertEightFieldsPresent(output, "xml");
        FormatterTestHelpers.AssertDirectFieldPresent(output, "xml");

        // Hard pins: express/lodash resolve MIT, phantom is Unknown not-found
        // (XML empty spdx element, NOT null/Unknown).
        var express = LicenseByName(licenses, "express");
        Assert.Equal("Resolved", ChildText(express, "status"));
        Assert.Equal("MIT", ChildText(express, "spdx"));
        var lodash = LicenseByName(licenses, "lodash");
        Assert.Equal("Resolved", ChildText(lodash, "status"));
        Assert.Equal("MIT", ChildText(lodash, "spdx"));
        var phantom = LicenseByName(licenses, PhantomName);
        Assert.Equal("Unknown", ChildText(phantom, "status"));
        Assert.Equal(string.Empty, ChildText(phantom, "spdx"));
        Assert.Contains("not-found", ChildText(phantom, "reason"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_SortByEcosystemNameVersion_When_RealNpmScanInputUnsorted()
    {
        // Input order is phantom-first; XML output must still be sorted
        // (ecosystem, name, version): express < lodash < phantom.
        // No status rule; version key is vacuous in this single-ecosystem arm.
        var (_, output) = await BuildRealXmlReportAsync(phantomFirst: true);

        var express = output.IndexOf("express", StringComparison.Ordinal);
        var lodash = output.IndexOf("lodash", StringComparison.Ordinal);
        var phantom = output.IndexOf(PhantomName, StringComparison.Ordinal);
        Assert.True(express >= 0, "XML output missing 'express'.");
        Assert.True(lodash >= 0, "XML output missing 'lodash'.");
        Assert.True(phantom >= 0, $"XML output missing '{PhantomName}'.");
        Assert.True(express < lodash, "Expected 'express' before 'lodash'.");
        Assert.True(lodash < phantom, $"Expected 'lodash' before phantom '{PhantomName}' (phantom last).");
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_MarkMissingPackageUnknown_When_RealNpmScan()
    {
        var (_, output) = await BuildRealXmlReportAsync();

        var doc = LoadXmlReport(output);
        var licenses = doc.SelectNodes("//license")!;
        var phantom = LicenseByName(licenses, PhantomName);
        Assert.Equal("Unknown", ChildText(phantom, "status"));
        // XML nulls serialize as empty elements, so phantom spdx is ""
        // rather than null/Unknown.
        Assert.Equal(string.Empty, ChildText(phantom, "spdx"));
        var reason = ChildText(phantom, "reason");
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("not-found", reason, StringComparison.OrdinalIgnoreCase);

        // Envelope guard: exactly 3 license rows.
        Assert.Equal(3, FormatterTestHelpers.CountOccurrences(output, "<license>"));
    }
}
