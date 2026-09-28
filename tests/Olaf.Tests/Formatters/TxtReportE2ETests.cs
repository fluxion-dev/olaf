using Olaf.Core;
using Olaf.Tests.Resolvers;

namespace Olaf.Tests.Formatters;

/// <summary>
/// E2E: real npm registry (https://registry.npmjs.org) -> ScanResult -> TxtFormatter.
/// Resolves exactly 3 pinned packages per Fact (express@4.18.2 + lodash@4.17.21
/// resolved MIT, one phantom package Unknown), then formats once. Marked with
/// [Trait("Category", "E2E")] so CI can filter with --filter "Category!=E2E".
/// TXT divergences: plain name@version (eco) direct= blocks plus Total:/Resolved:/
/// Unknown: header; AssertSummaryCounts/AssertEightFieldsPresent MUST NOT be
/// called on txt (no txt branch — would FAIL); hand-rolled Contains + FindByName
/// + CountOccurrences only. Inputs built new Dependency(..., isTransitive: false)
/// so Direct is true and entries render direct=true (Dependency.Direct negation).
/// e2e_trait_gate: every Fact below carries [Trait("Category", "E2E")] (4 Facts,
/// 0 Theories) so the Category!=E2E gate excludes this live-network file.
/// </summary>
public sealed class TxtReportE2ETests
{
    private const string PhantomName = "this-package-definitely-does-not-exist-olaf-xyz";

    private static HttpClient CreateRealClient()
    {
        return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static async Task<(ScanResult Scan, string Output)> BuildRealTxtReportAsync(bool phantomFirst = false)
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
        var formatter = FormatterTestHelpers.ResolveFormatter("txt");
        return (scan, formatter.FormatResult(scan));
    }

    private static string GetEntryBlock(string output, string name)
    {
        var title = FormatterTestHelpers.FindByName(
            output.Split('\n'),
            line => line.Contains($"{name}@", StringComparison.Ordinal) && line.Contains("(npm)", StringComparison.Ordinal)
                ? name
                : null,
            "TXT entries",
            name).Trim();
        var start = output.IndexOf(title, StringComparison.Ordinal);
        Assert.True(start >= 0, $"TXT output missing entry block for '{name}'.");
        // Block runs to the next "name@" title line or the end of output.
        var end = output.Length;
        foreach (var other in new[] { "express", "lodash", PhantomName })
        {
            if (other == name)
            {
                continue;
            }

            var candidate = output.IndexOf($"{other}@", start + 1, StringComparison.Ordinal);
            if (candidate >= 0 && candidate < end)
            {
                end = candidate;
            }
        }

        return output.Substring(start, end - start);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ProduceEnvelope_When_RealNpmScan()
    {
        var (scan, output) = await BuildRealTxtReportAsync();

        Assert.Contains("Third-Party Attribution", output, StringComparison.Ordinal);

        // Hand-rolled summary (AssertSummaryCounts MUST NOT be called on txt —
        // no txt branch) cross-checked vs ScanResult counts.
        Assert.Contains("Total: 3", output, StringComparison.Ordinal);
        Assert.Contains($"Resolved: {scan.ResolvedCount}", output, StringComparison.Ordinal);
        Assert.Contains($"Unknown: {scan.UnknownCount}", output, StringComparison.Ordinal);
        Assert.Equal(3, scan.TotalCount);
        Assert.Equal(3, scan.ResolvedCount + scan.UnknownCount);
        Assert.True(scan.UnknownCount >= 1, "Expected at least the phantom package to be Unknown.");

        // Inputs are direct (isTransitive: false) so all 3 entries render direct=true.
        Assert.Equal(3, FormatterTestHelpers.CountOccurrences(output, "direct=true"));
        FormatterTestHelpers.AssertDirectFieldPresent(output, "txt");

        // Default ungrouped branch only: grouped header/section markers must not appear.
        Assert.DoesNotContain("packages under", output, StringComparison.Ordinal);
        Assert.DoesNotContain("## ", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ContainFieldLinesPerEntry_When_RealNpmScan()
    {
        var (_, output) = await BuildRealTxtReportAsync();

        // Entry titles: name@version (npm) with direct= markers.
        Assert.Contains("express@4.18.2 (npm)", output, StringComparison.Ordinal);
        Assert.Contains("lodash@4.17.21 (npm)", output, StringComparison.Ordinal);
        Assert.Contains($"{PhantomName}@9.9.9 (npm)", output, StringComparison.Ordinal);
        FormatterTestHelpers.AssertDirectFieldPresent(output, "txt");

        // SPDX:/Status: present on every block; Source:/Reason:/Copyright: are
        // omit-when-empty, so present lines must carry a non-empty value.
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            foreach (var prefix in new[] { "SPDX:", "Source:", "Status:", "Reason:", "Copyright:" })
            {
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                {
                    Assert.True(
                        trimmed.Length > prefix.Length && !string.IsNullOrWhiteSpace(trimmed.Substring(prefix.Length)),
                        $"Expected non-empty value on line '{trimmed}'.");
                }
            }
        }

        // Resolved pins: express/lodash MIT with no Reason line (empty reason omitted).
        var express = GetEntryBlock(output, "express");
        Assert.Contains("SPDX: MIT", express, StringComparison.Ordinal);
        Assert.Contains("Status: Resolved", express, StringComparison.Ordinal);
        Assert.DoesNotContain("Reason:", express, StringComparison.Ordinal);
        var lodash = GetEntryBlock(output, "lodash");
        Assert.Contains("SPDX: MIT", lodash, StringComparison.Ordinal);
        Assert.Contains("Status: Resolved", lodash, StringComparison.Ordinal);
        Assert.DoesNotContain("Reason:", lodash, StringComparison.Ordinal);

        // Phantom: SPDX Unknown fallback (EffectiveSpdx, NOT null/empty) + not-found reason.
        var phantom = GetEntryBlock(output, PhantomName);
        Assert.Contains("SPDX: Unknown", phantom, StringComparison.Ordinal);
        Assert.Contains("Status: Unknown", phantom, StringComparison.Ordinal);
        Assert.Contains("Reason:", phantom, StringComparison.Ordinal);
        Assert.Contains("not-found", phantom, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_SortByEcosystemNameVersion_When_RealNpmScanInputUnsorted()
    {
        // Input order is phantom-first; TXT output must still be sorted
        // (ecosystem, name, version): express < lodash < phantom.
        var (_, output) = await BuildRealTxtReportAsync(phantomFirst: true);

        var express = output.IndexOf("express", StringComparison.Ordinal);
        var lodash = output.IndexOf("lodash", StringComparison.Ordinal);
        var phantom = output.IndexOf(PhantomName, StringComparison.Ordinal);
        Assert.True(express >= 0, "TXT output missing 'express'.");
        Assert.True(lodash >= 0, "TXT output missing 'lodash'.");
        Assert.True(phantom >= 0, $"TXT output missing '{PhantomName}'.");
        Assert.True(express < lodash, "Expected 'express' before 'lodash'.");
        Assert.True(lodash < phantom, $"Expected 'lodash' before phantom '{PhantomName}' (phantom last).");
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_MarkMissingPackageUnknown_When_RealNpmScan()
    {
        var (_, output) = await BuildRealTxtReportAsync();

        var phantom = GetEntryBlock(output, PhantomName);
        Assert.Contains("Status: Unknown", phantom, StringComparison.Ordinal);
        Assert.Contains("SPDX: Unknown", phantom, StringComparison.Ordinal);
        Assert.Contains("Reason:", phantom, StringComparison.Ordinal);
        Assert.Contains("not-found", phantom, StringComparison.OrdinalIgnoreCase);

        // Exactly 3 entry blocks (one per package).
        Assert.Equal(3, FormatterTestHelpers.CountOccurrences(output, " (npm) direct="));
    }
}
