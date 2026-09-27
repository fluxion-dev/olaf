using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// Issue #66 transitive-filter CLI matrix (subprocess e2e, mirrors
/// CliTests.cs style). Mixed fixture dir: svc-a/package.json (4 direct deps)
/// + svc-b/package-lock.json (2 transitive deps) → default total 6,
/// --direct-only total 4. No SPDX pins and no resolved/unknown splits (those
/// depend on network reachability); only totals, direct flags, names, and
/// exit codes — deterministic offline.
/// Exit-code contract: 0 success, 2 conflicting flags.
/// </summary>
public sealed class TransitiveFilterCliTests
{
    private static string CreateMixedFixtureDir()
    {
        var root = CliTestHelpers.CreateTempDir();
        var svcA = Path.Combine(root, "svc-a");
        var svcB = Path.Combine(root, "svc-b");
        Directory.CreateDirectory(svcA);
        Directory.CreateDirectory(svcB);
        File.Copy(CliTestHelpers.FixturePath("npm", "package.json"), Path.Combine(svcA, "package.json"));
        File.Copy(CliTestHelpers.FixturePath("npm", "package-lock.json"), Path.Combine(svcB, "package-lock.json"));
        return root;
    }

    private static JsonDocument ParseJsonStdout(CliResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        return JsonDocument.Parse(result.Stdout); // stdout must be JSON
    }

    [Fact]
    public void Should_ReportAll_When_NoFilterFlag()
    {
        var root = CreateMixedFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", root, "--format", "json");

            using var doc = ParseJsonStdout(result);
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(6, summary.GetProperty("total").GetInt32());
            var licenses = doc.RootElement.GetProperty("licenses");
            Assert.Equal(6, licenses.GetArrayLength());
            Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("debug", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(root);
        }
    }

    [Fact]
    public void Should_ReportDirectOnlySubset_When_DirectOnlyFlag()
    {
        var root = CreateMixedFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", root, "--format", "json", "--direct-only");

            using var doc = ParseJsonStdout(result);
            // Summary recomputes from the FILTERED set.
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(4, summary.GetProperty("total").GetInt32());
            Assert.Equal(
                summary.GetProperty("total").GetInt32(),
                summary.GetProperty("resolved").GetInt32() + summary.GetProperty("unknown").GetInt32());
            var licenses = doc.RootElement.GetProperty("licenses").EnumerateArray().ToList();
            Assert.Equal(4, licenses.Count);
            foreach (var entry in licenses)
            {
                Assert.True(entry.GetProperty("direct").GetBoolean(), "Expected every --direct-only entry to be direct.");
            }

            Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("debug", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(root);
        }
    }

    [Fact]
    public void Should_Exit2_When_BothFilterFlags()
    {
        var root = CreateMixedFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", root, "--format", "json", "--direct-only", "--include-transitive");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("Conflicting flags", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(root);
        }
    }

    [Fact]
    public void Should_ReportAll_When_IncludeTransitiveExplicit()
    {
        var root = CreateMixedFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", root, "--format", "json", "--include-transitive");

            using var doc = ParseJsonStdout(result);
            Assert.Equal(6, doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
            Assert.Equal(6, doc.RootElement.GetProperty("licenses").GetArrayLength());
            Assert.Contains("debug", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(root);
        }
    }

    [Fact]
    public void Should_Help_ListBothFilterFlags()
    {
        var result = CliTestHelpers.RunCli("--help");

        Assert.Equal(0, result.ExitCode);
        var combined = result.Stdout + result.Stderr;
        Assert.Contains("--direct-only", combined, StringComparison.Ordinal);
        Assert.Contains("--include-transitive", combined, StringComparison.Ordinal);
    }
}
