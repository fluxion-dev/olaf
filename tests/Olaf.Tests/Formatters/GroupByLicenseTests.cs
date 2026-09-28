using Olaf.Core;
using Olaf.Formatters;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #74: --group-by-license grouped branches of Txt/Markdown/Html
/// formatters. Offline unit style — ScanResults constructed inline (50-MIT
/// built programmatically, no fixture files, no network).
/// Implementer pins: header "{X} packages under {Y} licenses"; group header
/// "## {SPDX} ({n} packages)" (txt/md; h2 html); txt "License: {text}"; md
/// Cell() paragraph; html pre; bullets "- name@version (ecosystem)[; holders]";
/// Unknown bullets add ": {reason}". Key = EffectiveSpdx; groups Ordinal asc
/// with Unknown LAST; in-group ecosystem/name/version; first-sorted text wins.
/// </summary>
public sealed class GroupByLicenseTests
{
    private static ScanResult FiftyMitScanResult() => new(Enumerable.Range(0, 50).Select(i => new ResolvedLicense(
        new Dependency("npm", $"pkg-{i:00}", "1.0.0", false),
        "MIT",
        "MIT License text",
        null,
        "Resolved",
        null)).ToList());

    [Fact]
    public void Should_GroupFiftyMitIntoSingleBlock_When_TxtGrouped()
    {
        var output = new TxtFormatter(groupByLicense: true).FormatResult(FiftyMitScanResult());

        Assert.Contains("50 packages under 1 licenses", output, StringComparison.Ordinal);
        Assert.Contains("## MIT (50 packages)", output, StringComparison.Ordinal);
        Assert.Contains("License: MIT License text", output, StringComparison.Ordinal);
        Assert.Contains("- pkg-00@1.0.0 (npm)", output, StringComparison.Ordinal);
        Assert.Contains("- pkg-49@1.0.0 (npm)", output, StringComparison.Ordinal);
        Assert.Equal(50, FormatterTestHelpers.CountOccurrences(output, "- pkg-"));
        Assert.Equal(1, FormatterTestHelpers.CountOccurrences(output, "## MIT (50 packages)"));
    }

    [Fact]
    public void Should_GroupFiftyMitIntoSingleBlock_When_MdGrouped()
    {
        var output = new MarkdownFormatter(groupByLicense: true).FormatResult(FiftyMitScanResult());

        Assert.Contains("# Third-Party Attribution", output, StringComparison.Ordinal);
        Assert.Contains("50 packages under 1 licenses", output, StringComparison.Ordinal);
        Assert.Contains("## MIT (50 packages)", output, StringComparison.Ordinal);
        Assert.Contains("MIT License text", output, StringComparison.Ordinal);
        Assert.Contains("- pkg-00@1.0.0 (npm)", output, StringComparison.Ordinal);
        Assert.Contains("- pkg-49@1.0.0 (npm)", output, StringComparison.Ordinal);
        Assert.Equal(50, FormatterTestHelpers.CountOccurrences(output, "- pkg-"));
        Assert.Equal(1, FormatterTestHelpers.CountOccurrences(output, "## MIT (50 packages)"));
        Assert.DoesNotContain("| npm |", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_GroupFiftyMitIntoSingleBlock_When_HtmlGrouped()
    {
        var output = new HtmlFormatter(groupByLicense: true).FormatResult(FiftyMitScanResult());

        Assert.Contains("<p>50 packages under 1 licenses</p>", output, StringComparison.Ordinal);
        Assert.Contains("<h2>MIT (50 packages)</h2>", output, StringComparison.Ordinal);
        Assert.Contains("<pre>MIT License text</pre>", output, StringComparison.Ordinal);
        Assert.Contains("<li>pkg-00@1.0.0 (npm)</li>", output, StringComparison.Ordinal);
        Assert.Contains("<li>pkg-49@1.0.0 (npm)</li>", output, StringComparison.Ordinal);
        Assert.Equal(50, FormatterTestHelpers.CountOccurrences(output, "<li>"));
        Assert.DoesNotContain("<table", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Should_EmitLicenseTextOnce_When_FiftyPackagesShareLicense()
    {
        var result = FiftyMitScanResult();

        Assert.Equal(1, FormatterTestHelpers.CountOccurrences(new TxtFormatter(groupByLicense: true).FormatResult(result), "MIT License text"));
        Assert.Equal(1, FormatterTestHelpers.CountOccurrences(new MarkdownFormatter(groupByLicense: true).FormatResult(result), "MIT License text"));
        Assert.Equal(1, FormatterTestHelpers.CountOccurrences(new HtmlFormatter(groupByLicense: true).FormatResult(result), "MIT License text"));
    }

    [Fact]
    public void Should_AppendHoldersOrFallback_When_GroupedBullets()
    {
        var result = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "held-pkg", "1.0.0", false),
                "MIT",
                "MIT License text",
                null,
                "Resolved",
                null,
                new Enrichment(null, null, null, null, ["Acme Corp", "Jane Doe"])),
            new(
                new Dependency("npm", "bare-pkg", "2.0.0", false),
                "MIT",
                "MIT License text",
                null,
                "Resolved",
                null),
        });

        var txt = new TxtFormatter(groupByLicense: true).FormatResult(result);

        Assert.Contains("- held-pkg@1.0.0 (npm); Acme Corp; Jane Doe", txt, StringComparison.Ordinal);
        var bareBullet = "- bare-pkg@2.0.0 (npm)";
        var bareIndex = txt.IndexOf(bareBullet, StringComparison.Ordinal);
        Assert.True(bareIndex >= 0, "grouped txt missing bare-pkg bullet.");
        Assert.Equal(bareBullet, txt.Substring(bareIndex, bareBullet.Length));

        var md = new MarkdownFormatter(groupByLicense: true).FormatResult(result);

        Assert.Contains("- held-pkg@1.0.0 (npm); Acme Corp; Jane Doe", md, StringComparison.Ordinal);
        Assert.Contains("- bare-pkg@2.0.0 (npm)", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ProduceHeaderOnly_When_GroupedResultEmpty()
    {
        var txt = new TxtFormatter(groupByLicense: true).FormatResult(ScanResult.Empty);

        Assert.Contains("0 packages under 0 licenses", txt, StringComparison.Ordinal);
        Assert.DoesNotContain("##", txt, StringComparison.Ordinal);
        Assert.DoesNotContain("- ", txt, StringComparison.Ordinal);

        var md = new MarkdownFormatter(groupByLicense: true).FormatResult(ScanResult.Empty);

        Assert.Contains("0 packages under 0 licenses", md, StringComparison.Ordinal);
        Assert.DoesNotContain("##", md, StringComparison.Ordinal);

        var html = new HtmlFormatter(groupByLicense: true).FormatResult(ScanResult.Empty);

        Assert.Contains("0 packages under 0 licenses", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h2>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<li>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_OrderGroupsOrdinalAsc_WithUnknownLast()
    {
        var result = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "mit-pkg", "1.0.0", false),
                "MIT",
                null,
                null,
                "Resolved",
                null),
            new(
                new Dependency("npm", "mystery-pkg", "1.0.0", false),
                null,
                null,
                null,
                "Unknown",
                "not-found: no license for 'mystery-pkg 1.0.0'."),
            new(
                new Dependency("npm", "gpl-pkg", "1.0.0", false),
                "GPL-3.0-only",
                null,
                null,
                "Resolved",
                null),
            new(
                new Dependency("npm", "apache-pkg", "1.0.0", false),
                "Apache-2.0",
                null,
                null,
                "Resolved",
                null),
        });

        var output = new TxtFormatter(groupByLicense: true).FormatResult(result);

        Assert.Contains("4 packages under 4 licenses", output, StringComparison.Ordinal);
        var apache = output.IndexOf("## Apache-2.0 (1 packages)", StringComparison.Ordinal);
        var gpl = output.IndexOf("## GPL-3.0-only (1 packages)", StringComparison.Ordinal);
        var mit = output.IndexOf("## MIT (1 packages)", StringComparison.Ordinal);
        var unknown = output.IndexOf("## Unknown (1 packages)", StringComparison.Ordinal);
        Assert.True(apache >= 0, "missing Apache-2.0 group.");
        Assert.True(gpl >= 0, "missing GPL-3.0-only group.");
        Assert.True(mit >= 0, "missing MIT group.");
        Assert.True(unknown >= 0, "missing Unknown group.");
        Assert.True(apache < gpl && gpl < mit && mit < unknown, "expected Ordinal-asc groups with Unknown last.");
    }

    [Fact]
    public void Should_OrderPackagesWithinGroup_ByEcosystemNameVersion()
    {
        var result = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("nuget", "zebra", "1.0.0", false),
                "MIT",
                null,
                null,
                "Resolved",
                null),
            new(
                new Dependency("npm", "zebra", "1.0.0", false),
                "MIT",
                null,
                null,
                "Resolved",
                null),
            new(
                new Dependency("npm", "apple", "2.0.0", false),
                "MIT",
                null,
                null,
                "Resolved",
                null),
        });

        var output = new TxtFormatter(groupByLicense: true).FormatResult(result);

        Assert.Contains("## MIT (3 packages)", output, StringComparison.Ordinal);
        var appleNpm = output.IndexOf("- apple@2.0.0 (npm)", StringComparison.Ordinal);
        var zebraNpm = output.IndexOf("- zebra@1.0.0 (npm)", StringComparison.Ordinal);
        var zebraNuget = output.IndexOf("- zebra@1.0.0 (nuget)", StringComparison.Ordinal);
        Assert.True(appleNpm >= 0, "missing npm/apple bullet.");
        Assert.True(zebraNpm >= 0, "missing npm/zebra bullet.");
        Assert.True(zebraNuget >= 0, "missing nuget/zebra bullet.");
        Assert.True(appleNpm < zebraNpm && zebraNpm < zebraNuget, "expected in-group ecosystem/name/version order.");
    }

    [Fact]
    public void Should_PreservePerPackageReasons_When_UnknownGrouped()
    {
        var result = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "aaa", "1.0.0", false),
                null,
                null,
                null,
                "Unknown",
                "reason-alpha: no license file."),
            new(
                new Dependency("npm", "zzz", "2.0.0", false),
                null,
                null,
                null,
                "Unknown",
                "reason-beta: fetch failed."),
        });

        var output = new TxtFormatter(groupByLicense: true).FormatResult(result);

        Assert.Contains("## Unknown (2 packages)", output, StringComparison.Ordinal);
        Assert.Contains("- aaa@1.0.0 (npm): reason-alpha: no license file.", output, StringComparison.Ordinal);
        Assert.Contains("- zzz@2.0.0 (npm): reason-beta: fetch failed.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_KeyByEffectiveSpdx_When_StatusMix()
    {
        var result = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "alpha-pkg", "1.0.0", false),
                null,
                null,
                null,
                "Resolved",
                null),
            new(
                new Dependency("npm", "beta-pkg", "1.0.0", false),
                "MIT",
                "MIT License text",
                null,
                "Resolved",
                null),
            new(
                new Dependency("npm", "gamma-pkg", "1.0.0", false),
                "MIT",
                "MIT License text",
                null,
                "Unknown",
                "stale-cache: should not surface in the MIT group."),
        });

        var output = new TxtFormatter(groupByLicense: true).FormatResult(result);

        Assert.Contains("3 packages under 2 licenses", output, StringComparison.Ordinal);
        var mitIndex = output.IndexOf("## MIT (2 packages)", StringComparison.Ordinal);
        var unknownIndex = output.IndexOf("## Unknown (1 packages)", StringComparison.Ordinal);
        Assert.True(mitIndex >= 0, "missing MIT group.");
        Assert.True(unknownIndex >= 0, "missing Unknown group.");
        Assert.True(unknownIndex > mitIndex, "expected Unknown group after MIT group.");
        var mitBlock = output.Substring(mitIndex, unknownIndex - mitIndex);
        Assert.Contains("- beta-pkg@1.0.0 (npm)", mitBlock, StringComparison.Ordinal);
        Assert.Contains("- gamma-pkg@1.0.0 (npm)", mitBlock, StringComparison.Ordinal);
        var unknownBlock = output.Substring(unknownIndex);
        Assert.Contains("- alpha-pkg@1.0.0 (npm)", unknownBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-cache", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_MatchDefaultOutput_When_GroupFlagFalse()
    {
        var sample = FormatterTestHelpers.SampleScanResult();

        Assert.Equal(
            FormatterTestHelpers.ResolveFormatter("txt").FormatResult(sample),
            new TxtFormatter(groupByLicense: false).FormatResult(sample));
        Assert.Equal(
            new TxtFormatter().FormatResult(sample),
            new TxtFormatter(groupByLicense: false).FormatResult(sample));
        Assert.Equal(
            FormatterTestHelpers.ResolveFormatter("md").FormatResult(sample),
            new MarkdownFormatter(groupByLicense: false).FormatResult(sample));
        Assert.Equal(
            new MarkdownFormatter().FormatResult(sample),
            new MarkdownFormatter(groupByLicense: false).FormatResult(sample));
        Assert.Equal(
            FormatterTestHelpers.ResolveFormatter("html").FormatResult(sample),
            new HtmlFormatter(groupByLicense: false).FormatResult(sample));
        Assert.Equal(
            new HtmlFormatter().FormatResult(sample),
            new HtmlFormatter(groupByLicense: false).FormatResult(sample));
        Assert.NotEqual(
            new TxtFormatter().FormatResult(sample),
            new TxtFormatter(groupByLicense: true).FormatResult(sample));
    }

    [Fact]
    public void Should_OmitGroupingMarkers_When_DefaultFormatter()
    {
        var sample = FormatterTestHelpers.SampleScanResult();

        var txt = FormatterTestHelpers.ResolveFormatter("txt").FormatResult(sample);

        Assert.DoesNotContain("packages under", txt, StringComparison.Ordinal);
        Assert.DoesNotContain("##", txt, StringComparison.Ordinal);
        Assert.Contains("express@4.18.2 (npm)", txt, StringComparison.Ordinal);

        var md = FormatterTestHelpers.ResolveFormatter("md").FormatResult(sample);

        Assert.DoesNotContain("packages under", md, StringComparison.Ordinal);
        Assert.DoesNotContain("packages)", md, StringComparison.Ordinal);
        Assert.Contains("## express@4.18.2 (npm)", md, StringComparison.Ordinal);

        var html = FormatterTestHelpers.ResolveFormatter("html").FormatResult(sample);

        Assert.DoesNotContain("packages under", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h2>", html, StringComparison.Ordinal);
        Assert.Contains("express", html, StringComparison.Ordinal);
    }
}
