using Olaf.Core;
using Olaf.Tests.Resolvers;
using YamlDotNet.Serialization;

namespace Olaf.Tests.Formatters;

/// <summary>
/// E2E: real npm registry (https://registry.npmjs.org) -> ScanResult -> YamlFormatter.
/// Resolves exactly 3 pinned packages per Fact (express@4.18.2 + lodash@4.17.21
/// resolved MIT, one phantom package Unknown), then formats once. Marked with
/// [Trait("Category", "E2E")] so CI can filter with --filter "Category!=E2E".
/// </summary>
public sealed class YamlReportE2ETests
{
    private const string PhantomName = "this-package-definitely-does-not-exist-olaf-xyz";

    private sealed class YamlSummaryDto
    {
        [YamlMember(Alias = "total")]
        public int Total { get; set; }

        [YamlMember(Alias = "resolved")]
        public int Resolved { get; set; }

        [YamlMember(Alias = "unknown")]
        public int Unknown { get; set; }
    }

    private sealed class YamlReportDto
    {
        [YamlMember(Alias = "summary")]
        public YamlSummaryDto Summary { get; set; } = new();

        [YamlMember(Alias = "licenses")]
        public List<Dictionary<string, string?>> Licenses { get; set; } = new();
    }

    private static HttpClient CreateRealClient()
    {
        return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static async Task<(ScanResult Scan, string Output)> BuildRealYamlReportAsync(bool phantomFirst = false)
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
        var formatter = FormatterTestHelpers.ResolveFormatter("yaml");
        return (scan, formatter.FormatResult(scan));
    }

    private static YamlReportDto ParseYamlReport(string output)
    {
        var deserializer = new DeserializerBuilder().Build();
        var report = deserializer.Deserialize<YamlReportDto>(output); // throws if not valid YAML
        Assert.NotNull(report);
        Assert.NotNull(report.Summary);
        Assert.NotNull(report.Licenses);
        return report;
    }

    private static Dictionary<string, string?> FindByName(YamlReportDto report, string name)
    {
        foreach (var entry in report.Licenses)
        {
            if (entry.TryGetValue("name", out var entryName) && entryName == name)
            {
                return entry;
            }
        }

        throw new Xunit.Sdk.XunitException($"YAML licenses[] missing entry '{name}'.");
    }

    private static string? Get(Dictionary<string, string?> entry, string key)
    {
        entry.TryGetValue(key, out var value);
        return value;
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ProduceEnvelope_When_RealNpmScan()
    {
        var (scan, output) = await BuildRealYamlReportAsync();

        var report = ParseYamlReport(output); // throws if not valid YAML
        Assert.Equal(3, report.Summary.Total);
        Assert.Equal(3, report.Licenses.Count);

        // Summary cross-check vs ScanResult counts.
        Assert.Equal(3, report.Summary.Resolved + report.Summary.Unknown);
        Assert.Equal(scan.TotalCount, report.Summary.Total);
        Assert.Equal(scan.ResolvedCount, report.Summary.Resolved);
        Assert.Equal(scan.UnknownCount, report.Summary.Unknown);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ContainEightFieldsPerEntry_When_RealNpmScan()
    {
        var (scan, output) = await BuildRealYamlReportAsync();

        var report = ParseYamlReport(output);
        Assert.Equal(3, report.Licenses.Count);
        foreach (var entry in report.Licenses)
        {
            Assert.Equal(9, entry.Count);
            foreach (var key in new[] { "ecosystem", "name", "version", "spdx", "licenseText", "sourceUrl", "status", "reason", "direct" })
            {
                Assert.True(entry.ContainsKey(key), $"YAML licenses[] entry missing key '{key}'.");
            }
        }

        FormatterTestHelpers.AssertEightFieldsPresent(output, "yaml");

        // Tolerant summary: total pinned at 3, at least one unknown (phantom),
        // resolved fills the rest — then cross-check formatter vs ScanResult.
        Assert.Equal(3, report.Summary.Total);
        Assert.True(report.Summary.Unknown >= 1, "Expected at least the phantom package to be Unknown.");
        Assert.Equal(
            report.Summary.Total,
            report.Summary.Resolved + report.Summary.Unknown);
        FormatterTestHelpers.AssertSummaryCounts(
            output,
            total: scan.TotalCount,
            resolved: scan.ResolvedCount,
            unknown: scan.UnknownCount);

        // Hard pins: express/lodash resolve MIT, phantom is Unknown not-found.
        var express = FindByName(report, "express");
        Assert.Equal("Resolved", Get(express, "status"));
        Assert.Equal("MIT", Get(express, "spdx"));
        var lodash = FindByName(report, "lodash");
        Assert.Equal("Resolved", Get(lodash, "status"));
        Assert.Equal("MIT", Get(lodash, "spdx"));
        var phantom = FindByName(report, PhantomName);
        Assert.Equal("Unknown", Get(phantom, "status"));
        Assert.Contains("not-found", Get(phantom, "reason") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_SortByEcosystemNameVersion_When_RealNpmScanInputUnsorted()
    {
        // Input order is phantom-first; YAML output must still be sorted
        // (ecosystem, name, version): express < lodash < phantom.
        var (_, output) = await BuildRealYamlReportAsync(phantomFirst: true);

        ParseYamlReport(output); // throws if not valid YAML
        var express = output.IndexOf("express", StringComparison.Ordinal);
        var lodash = output.IndexOf("lodash", StringComparison.Ordinal);
        var phantom = output.IndexOf(PhantomName, StringComparison.Ordinal);
        Assert.True(express >= 0, "YAML output missing 'express'.");
        Assert.True(lodash >= 0, "YAML output missing 'lodash'.");
        Assert.True(phantom >= 0, $"YAML output missing '{PhantomName}'.");
        Assert.True(express < lodash, "Expected 'express' before 'lodash'.");
        Assert.True(lodash < phantom, $"Expected 'lodash' before phantom '{PhantomName}' (phantom last).");
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_MarkMissingPackageUnknown_When_RealNpmScan()
    {
        var (_, output) = await BuildRealYamlReportAsync();

        var report = ParseYamlReport(output);
        var phantom = FindByName(report, PhantomName);
        Assert.Equal("Unknown", Get(phantom, "status"));
        // YAML nulls serialize as empty/null — assert tolerant, not exact null.
        var spdx = Get(phantom, "spdx");
        Assert.True(
            string.IsNullOrEmpty(spdx) || string.Equals(spdx, "null", StringComparison.OrdinalIgnoreCase),
            $"Expected phantom spdx to be null/empty, got '{spdx}'.");
        var reason = Get(phantom, "reason");
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("not-found", reason, StringComparison.OrdinalIgnoreCase);
    }
}
