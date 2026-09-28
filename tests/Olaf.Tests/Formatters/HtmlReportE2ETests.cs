using System.Net;
using Olaf.Core;
using Olaf.Tests.Resolvers;
using Xunit.Sdk;

namespace Olaf.Tests.Formatters;

/// <summary>
/// E2E: real npm registry (https://registry.npmjs.org) -> ScanResult -> HtmlFormatter.
/// Resolves exactly 3 pinned packages per Fact (express@4.18.2 + lodash@4.17.21
/// resolved MIT, one phantom package Unknown), then formats once. Marked with
/// [Trait("Category", "E2E")] so CI can filter with --filter "Category!=E2E".
/// HTML divergences: SpdxId ?? Status (phantom SPDX renders Unknown, not null),
/// nulls render as empty cells, 8-column table with summary paragraph.
/// </summary>
public sealed class HtmlReportE2ETests
{
    private const string PhantomName = "this-package-definitely-does-not-exist-olaf-xyz";

    private static HttpClient CreateRealClient()
    {
        return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static async Task<(ScanResult Scan, string Output)> BuildRealHtmlReportAsync(bool phantomFirst = false)
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
        var formatter = FormatterTestHelpers.ResolveFormatter("html");
        return (scan, formatter.FormatResult(scan));
    }

    private static string GetRow(string output, string name)
    {
        var cell = $"<td>{WebUtility.HtmlEncode(name)}</td>";
        var haystack = FormatterTestHelpers.FindByName(
            new[] { output },
            h => h.Contains(cell, StringComparison.Ordinal) ? name : null,
            "HTML table",
            name);
        var nameIndex = haystack.IndexOf(cell, StringComparison.Ordinal);
        var rowStart = haystack.LastIndexOf("<tr>", nameIndex, StringComparison.Ordinal);
        var rowEnd = haystack.IndexOf("</tr>", nameIndex, StringComparison.Ordinal);
        if (rowStart < 0 || rowEnd < 0)
        {
            throw new XunitException($"HTML table row for '{name}' is malformed.");
        }

        return haystack.Substring(rowStart, rowEnd - rowStart);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ProduceEnvelope_When_RealNpmScan()
    {
        var (scan, output) = await BuildRealHtmlReportAsync();

        Assert.Contains("<!DOCTYPE html>", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<html", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<head", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<meta", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<table", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<thead", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<tbody", output, StringComparison.OrdinalIgnoreCase);

        // Summary paragraph cross-check vs ScanResult counts.
        Assert.Contains("Total: 3", output, StringComparison.Ordinal);
        Assert.Contains($"Resolved: {scan.ResolvedCount}", output, StringComparison.Ordinal);
        Assert.Contains($"Unknown: {scan.UnknownCount}", output, StringComparison.Ordinal);
        Assert.Equal(3, scan.TotalCount);
        Assert.Equal(3, scan.ResolvedCount + scan.UnknownCount);
        FormatterTestHelpers.AssertSummaryCounts(
            output,
            total: scan.TotalCount,
            resolved: scan.ResolvedCount,
            unknown: scan.UnknownCount);

        // Table rows: 3 body rows x 9 columns = 27 td cells.
        var tbodyStart = output.IndexOf("<tbody>", StringComparison.Ordinal);
        var tbodyEnd = output.IndexOf("</tbody>", StringComparison.Ordinal);
        Assert.True(tbodyStart >= 0, "HTML output missing '<tbody>'.");
        Assert.True(tbodyEnd > tbodyStart, "HTML output missing '</tbody>'.");
        var tbody = output.Substring(tbodyStart, tbodyEnd - tbodyStart);
        Assert.Equal(3, FormatterTestHelpers.CountOccurrences(tbody, "<tr>"));
        Assert.Equal(27, FormatterTestHelpers.CountOccurrences(tbody, "<td>"));
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ContainEightFieldsPerEntry_When_RealNpmScan()
    {
        var (scan, output) = await BuildRealHtmlReportAsync();

        foreach (var header in new[] { "<th>Ecosystem</th>", "<th>Name</th>", "<th>Version</th>", "<th>SPDX</th>", "<th>License</th>", "<th>Source</th>", "<th>Status</th>", "<th>Reason</th>", "<th>Direct</th>" })
        {
            Assert.Contains(header, output, StringComparison.Ordinal);
        }

        FormatterTestHelpers.AssertEightFieldsPresent(output, "html");

        // Tolerant summary: total pinned at 3, at least one unknown (phantom),
        // resolved fills the rest — then cross-check formatter vs ScanResult.
        Assert.Contains("Total: 3", output, StringComparison.Ordinal);
        Assert.True(scan.UnknownCount >= 1, "Expected at least the phantom package to be Unknown.");
        Assert.Equal(3, scan.ResolvedCount + scan.UnknownCount);
        FormatterTestHelpers.AssertSummaryCounts(
            output,
            total: scan.TotalCount,
            resolved: scan.ResolvedCount,
            unknown: scan.UnknownCount);

        // Hard pins: express/lodash resolve MIT, phantom is Unknown not-found.
        var express = GetRow(output, "express");
        Assert.Contains("Resolved", express, StringComparison.Ordinal);
        Assert.Contains("MIT", express, StringComparison.Ordinal);
        var lodash = GetRow(output, "lodash");
        Assert.Contains("Resolved", lodash, StringComparison.Ordinal);
        Assert.Contains("MIT", lodash, StringComparison.Ordinal);
        var phantom = GetRow(output, PhantomName);
        Assert.Contains("Unknown", phantom, StringComparison.Ordinal);
        Assert.Contains("not-found", phantom, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_SortByEcosystemNameVersion_When_RealNpmScanInputUnsorted()
    {
        // Input order is phantom-first; HTML output must still be sorted
        // (ecosystem, name, version): express < lodash < phantom.
        var (_, output) = await BuildRealHtmlReportAsync(phantomFirst: true);

        var express = output.IndexOf("express", StringComparison.Ordinal);
        var lodash = output.IndexOf("lodash", StringComparison.Ordinal);
        var phantom = output.IndexOf(PhantomName, StringComparison.Ordinal);
        Assert.True(express >= 0, "HTML output missing 'express'.");
        Assert.True(lodash >= 0, "HTML output missing 'lodash'.");
        Assert.True(phantom >= 0, $"HTML output missing '{PhantomName}'.");
        Assert.True(express < lodash, "Expected 'express' before 'lodash'.");
        Assert.True(lodash < phantom, $"Expected 'lodash' before phantom '{PhantomName}' (phantom last).");
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_MarkMissingPackageUnknown_When_RealNpmScan()
    {
        var (_, output) = await BuildRealHtmlReportAsync();

        var phantom = GetRow(output, PhantomName);
        Assert.Contains("Unknown", phantom, StringComparison.Ordinal);
        // HTML renders SpdxId ?? Status, so phantom SPDX falls back to
        // Unknown rather than null/empty.
        Assert.Contains("<td>Unknown</td>", phantom, StringComparison.Ordinal);
        Assert.Contains("not-found", phantom, StringComparison.OrdinalIgnoreCase);
    }
}
