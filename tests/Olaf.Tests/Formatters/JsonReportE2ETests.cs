using System.Text.Json;
using Olaf.Core;
using Olaf.Tests.Resolvers;

namespace Olaf.Tests.Formatters;

/// <summary>
/// E2E: real npm registry (https://registry.npmjs.org) -> ScanResult -> JsonFormatter.
/// Resolves exactly 3 pinned packages per Fact (express@4.18.2 + lodash@4.17.21
/// resolved MIT, one phantom package Unknown), then formats once. Marked with
/// [Trait("Category", "E2E")] so CI can filter with --filter "Category!=E2E".
/// </summary>
public sealed class JsonReportE2ETests
{
    private const string PhantomName = "this-package-definitely-does-not-exist-olaf-xyz";

    private static HttpClient CreateRealClient()
    {
        return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static async Task<(ScanResult Scan, string Output)> BuildRealJsonReportAsync(bool phantomFirst = false)
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
        var formatter = FormatterTestHelpers.ResolveFormatter("json");
        return (scan, formatter.FormatResult(scan));
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ProduceEnvelope_When_RealNpmScan()
    {
        var (scan, output) = await BuildRealJsonReportAsync();

        using var doc = JsonDocument.Parse(output); // throws if not valid JSON
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("total").GetInt32());
        var licenses = doc.RootElement.GetProperty("licenses");
        Assert.Equal(JsonValueKind.Array, licenses.ValueKind);
        Assert.Equal(3, licenses.GetArrayLength());

        // Summary cross-check vs ScanResult counts.
        var resolved = summary.GetProperty("resolved").GetInt32();
        var unknown = summary.GetProperty("unknown").GetInt32();
        Assert.Equal(3, resolved + unknown);
        Assert.Equal(scan.TotalCount, summary.GetProperty("total").GetInt32());
        Assert.Equal(scan.ResolvedCount, resolved);
        Assert.Equal(scan.UnknownCount, unknown);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ContainEightFieldsPerEntry_When_RealNpmScan()
    {
        var (scan, output) = await BuildRealJsonReportAsync();

        using var doc = JsonDocument.Parse(output);
        var licenses = doc.RootElement.GetProperty("licenses");
        Assert.Equal(3, licenses.GetArrayLength());
        foreach (var entry in licenses.EnumerateArray())
        {
            foreach (var key in new[] { "ecosystem", "name", "version", "spdx", "licenseText", "sourceUrl", "status", "reason" })
            {
                Assert.True(entry.TryGetProperty(key, out _), $"JSON licenses[] entry missing key '{key}'.");
            }
        }

        FormatterTestHelpers.AssertEightFieldsPresent(output, "json");

        // Tolerant summary: total pinned at 3, at least one unknown (phantom),
        // resolved fills the rest — then cross-check formatter vs ScanResult.
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("total").GetInt32());
        Assert.True(summary.GetProperty("unknown").GetInt32() >= 1, "Expected at least the phantom package to be Unknown.");
        Assert.Equal(
            summary.GetProperty("total").GetInt32(),
            summary.GetProperty("resolved").GetInt32() + summary.GetProperty("unknown").GetInt32());
        FormatterTestHelpers.AssertSummaryCounts(
            output,
            total: scan.TotalCount,
            resolved: scan.ResolvedCount,
            unknown: scan.UnknownCount);

        // Hard pins: express/lodash resolve MIT, phantom is Unknown not-found.
        var express = FormatterTestHelpers.FindByName(licenses.EnumerateArray(), e => e.GetProperty("name").GetString(), "JSON licenses[]", "express");
        Assert.Equal("Resolved", express.GetProperty("status").GetString());
        Assert.Equal("MIT", express.GetProperty("spdx").GetString());
        var lodash = FormatterTestHelpers.FindByName(licenses.EnumerateArray(), e => e.GetProperty("name").GetString(), "JSON licenses[]", "lodash");
        Assert.Equal("Resolved", lodash.GetProperty("status").GetString());
        Assert.Equal("MIT", lodash.GetProperty("spdx").GetString());
        var phantom = FormatterTestHelpers.FindByName(licenses.EnumerateArray(), e => e.GetProperty("name").GetString(), "JSON licenses[]", PhantomName);
        Assert.Equal("Unknown", phantom.GetProperty("status").GetString());
        Assert.Contains("not-found", phantom.GetProperty("reason").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_SortByEcosystemNameVersion_When_RealNpmScanInputUnsorted()
    {
        // Input order is phantom-first; JSON output must still be sorted
        // (ecosystem, name, version): express < lodash < phantom.
        var (_, output) = await BuildRealJsonReportAsync(phantomFirst: true);

        var express = output.IndexOf("express", StringComparison.Ordinal);
        var lodash = output.IndexOf("lodash", StringComparison.Ordinal);
        var phantom = output.IndexOf(PhantomName, StringComparison.Ordinal);
        Assert.True(express >= 0, "JSON output missing 'express'.");
        Assert.True(lodash >= 0, "JSON output missing 'lodash'.");
        Assert.True(phantom >= 0, $"JSON output missing '{PhantomName}'.");
        Assert.True(express < lodash, "Expected 'express' before 'lodash'.");
        Assert.True(lodash < phantom, $"Expected 'lodash' before phantom '{PhantomName}' (phantom last).");
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_MarkMissingPackageUnknown_When_RealNpmScan()
    {
        var (_, output) = await BuildRealJsonReportAsync();

        using var doc = JsonDocument.Parse(output);
        var licenses = doc.RootElement.GetProperty("licenses");
        var phantom = FormatterTestHelpers.FindByName(licenses.EnumerateArray(), e => e.GetProperty("name").GetString(), "JSON licenses[]", PhantomName);
        Assert.Equal("Unknown", phantom.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, phantom.GetProperty("spdx").ValueKind);
        var reason = phantom.GetProperty("reason").GetString();
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("not-found", reason, StringComparison.OrdinalIgnoreCase);
    }
}
