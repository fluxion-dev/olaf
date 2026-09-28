using Olaf.Core;
using Olaf.Tests.Resolvers;

namespace Olaf.Tests.Formatters;

/// <summary>
/// E2E: real npm registry (https://registry.npmjs.org) -> ScanResult -> MarkdownFormatter.
/// Resolves exactly 3 pinned packages per Fact (express@4.18.2 + lodash@4.17.21
/// resolved MIT, one phantom package Unknown), then formats once. Marked with
/// [Trait("Category", "E2E")] so CI can filter with --filter "Category!=E2E".
/// Markdown divergences: SpdxId ?? Unknown fallback (phantom SPDX renders Unknown,
/// not null), table plus per-package ## sections with - bullets.
/// </summary>
public sealed class MarkdownReportE2ETests
{
    private const string PhantomName = "this-package-definitely-does-not-exist-olaf-xyz";

    private static HttpClient CreateRealClient()
    {
        return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static async Task<(ScanResult Scan, string Output)> BuildRealMarkdownReportAsync(bool phantomFirst = false)
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
        var formatter = FormatterTestHelpers.ResolveFormatter("md");
        return (scan, formatter.FormatResult(scan));
    }

    private static string GetTableRow(string output, string name)
    {
        var cell = $"| {name} |";
        return FormatterTestHelpers.FindByName(
            output.Split('\n'),
            line =>
            {
                var row = line.Trim();
                return row.StartsWith("|", StringComparison.Ordinal) && row.Contains(cell, StringComparison.Ordinal)
                    ? name
                    : null;
            },
            "Markdown table",
            name).Trim();
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ProduceEnvelope_When_RealNpmScan()
    {
        var (scan, output) = await BuildRealMarkdownReportAsync();

        Assert.Contains("# Third-Party Attribution", output, StringComparison.Ordinal);

        // Summary line cross-check vs ScanResult counts.
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

        // Table header + separator, then 3 data rows and 3 ## sections.
        Assert.Contains("| Ecosystem | Name | Version | SPDX | License | Source | Status | Reason | Direct |", output, StringComparison.Ordinal);
        Assert.Contains("| --- | --- | --- | --- | --- | --- | --- | --- | --- |", output, StringComparison.Ordinal);
        var pipeLines = output.Split('\n').Count(line => line.TrimStart().StartsWith("| ", StringComparison.Ordinal));
        Assert.Equal(5, pipeLines);
        Assert.Equal(3, FormatterTestHelpers.CountOccurrences(output, "## "));
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ContainEightFieldsPerEntry_When_RealNpmScan()
    {
        var (scan, output) = await BuildRealMarkdownReportAsync();

        // Per-row: 9 cells means 10 pipe delimiters per data row.
        foreach (var name in new[] { "express", "lodash", PhantomName })
        {
            var row = GetTableRow(output, name);
            Assert.Equal(10, FormatterTestHelpers.CountOccurrences(row, "|"));
        }

        // Per-package sections with - bullets carrying all 9 fields.
        Assert.Contains("## express@4.18.2 (npm)", output, StringComparison.Ordinal);
        Assert.Contains("## lodash@4.17.21 (npm)", output, StringComparison.Ordinal);
        Assert.Contains($"## {PhantomName}@9.9.9 (npm)", output, StringComparison.Ordinal);
        foreach (var bullet in new[] { "- Ecosystem:", "- Name:", "- Version:", "- SPDX:", "- License:", "- Source:", "- Status:", "- Reason:", "- Direct:" })
        {
            Assert.Contains(bullet, output, StringComparison.Ordinal);
        }

        FormatterTestHelpers.AssertEightFieldsPresent(output, "md");

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

        // Hard pins: express/lodash resolve MIT, phantom is Unknown not-found
        // (SPDX Unknown fallback, not null).
        var express = GetTableRow(output, "express");
        Assert.Contains("Resolved", express, StringComparison.Ordinal);
        Assert.Contains("MIT", express, StringComparison.Ordinal);
        var lodash = GetTableRow(output, "lodash");
        Assert.Contains("Resolved", lodash, StringComparison.Ordinal);
        Assert.Contains("MIT", lodash, StringComparison.Ordinal);
        var phantom = GetTableRow(output, PhantomName);
        Assert.Contains("Unknown", phantom, StringComparison.Ordinal);
        Assert.Contains("| Unknown |", phantom, StringComparison.Ordinal);
        Assert.Contains("not-found", phantom, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_SortByEcosystemNameVersion_When_RealNpmScanInputUnsorted()
    {
        // Input order is phantom-first; Markdown output must still be sorted
        // (ecosystem, name, version): express < lodash < phantom.
        var (_, output) = await BuildRealMarkdownReportAsync(phantomFirst: true);

        var express = output.IndexOf("express", StringComparison.Ordinal);
        var lodash = output.IndexOf("lodash", StringComparison.Ordinal);
        var phantom = output.IndexOf(PhantomName, StringComparison.Ordinal);
        Assert.True(express >= 0, "Markdown output missing 'express'.");
        Assert.True(lodash >= 0, "Markdown output missing 'lodash'.");
        Assert.True(phantom >= 0, $"Markdown output missing '{PhantomName}'.");
        Assert.True(express < lodash, "Expected 'express' before 'lodash'.");
        Assert.True(lodash < phantom, $"Expected 'lodash' before phantom '{PhantomName}' (phantom last).");
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_MarkMissingPackageUnknown_When_RealNpmScan()
    {
        var (_, output) = await BuildRealMarkdownReportAsync();

        var phantom = GetTableRow(output, PhantomName);
        Assert.Contains("Unknown", phantom, StringComparison.Ordinal);
        // Markdown renders SpdxId ?? Unknown, so phantom SPDX falls back to
        // Unknown rather than null/empty.
        Assert.Contains("| Unknown |", phantom, StringComparison.Ordinal);
        Assert.Contains("not-found", phantom, StringComparison.OrdinalIgnoreCase);

        // Phantom sorts last, so its ## section runs to the end of output.
        var sectionStart = output.IndexOf($"## {PhantomName}@", StringComparison.Ordinal);
        Assert.True(sectionStart >= 0, $"Markdown output missing section for '{PhantomName}'.");
        var section = output.Substring(sectionStart);
        Assert.Contains("Unknown", section, StringComparison.Ordinal);
        Assert.Contains("- SPDX: Unknown", section, StringComparison.Ordinal);
        Assert.Contains("not-found", section, StringComparison.OrdinalIgnoreCase);
    }
}
