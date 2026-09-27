using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// Issue #66 margin/edge coverage for the transitive filter (subprocess e2e,
/// offline fixtures only — no SPDX pins, only totals, markers, exit codes):
/// empty-result branch, all-direct no-op, txt/yaml direct markers through
/// the CLI, and cross-run consistency between the three filter modes.
/// </summary>
public sealed class TransitiveFilterMarginCliTests
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

    private static int TotalOf(CliResult result)
    {
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32();
    }

    [Fact]
    public void Should_ReportZeroTotal_When_DirectOnlyOnAllTransitiveInput()
    {
        // package-lock.json entries are all transitive → --direct-only yields
        // an empty (but successful) report, not an error.
        var input = CliTestHelpers.FixturePath("npm", "package-lock.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--direct-only");

        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal(0, doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("licenses").GetArrayLength());
    }

    [Fact]
    public void Should_ReportAll_When_DirectOnlyOnAllDirectInput()
    {
        // package.json entries are all direct → --direct-only is a no-op.
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--direct-only");

        Assert.Equal(4, TotalOf(result));
    }

    [Fact]
    public void Should_ContainOnlyDirectTokens_When_DirectOnlyWithTxtFormat()
    {
        var root = CreateMixedFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", root, "--format", "txt", "--direct-only");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("direct=true", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("direct=false", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(root);
        }
    }

    [Fact]
    public void Should_ContainDirectMarkers_When_IncludeTransitiveWithYamlFormat()
    {
        var root = CreateMixedFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", root, "--format", "yaml", "--include-transitive");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("direct:", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(root);
        }
    }

    [Fact]
    public void Should_ReconcileTotals_When_ComparingFilterModes()
    {
        var root = CreateMixedFixtureDir();
        try
        {
            var @default = CliTestHelpers.RunCli("--input", root, "--format", "json");
            var directOnly = CliTestHelpers.RunCli("--input", root, "--format", "json", "--direct-only");
            var includeTransitive = CliTestHelpers.RunCli("--input", root, "--format", "json", "--include-transitive");

            Assert.Equal(6, TotalOf(@default));
            Assert.Equal(4, TotalOf(directOnly));
            Assert.Equal(6, TotalOf(includeTransitive));

            // The direct-only set is exactly the direct subset of the default set.
            static HashSet<string> DirectKeys(string stdout)
            {
                using var doc = JsonDocument.Parse(stdout);
                return doc.RootElement.GetProperty("licenses").EnumerateArray()
                    .Where(e => e.GetProperty("direct").GetBoolean())
                    .Select(e => $"{e.GetProperty("name").GetString()}@{e.GetProperty("version").GetString()}")
                    .ToHashSet(StringComparer.Ordinal);
            }

            static HashSet<string> AllKeys(string stdout)
            {
                using var doc = JsonDocument.Parse(stdout);
                return doc.RootElement.GetProperty("licenses").EnumerateArray()
                    .Select(e => $"{e.GetProperty("name").GetString()}@{e.GetProperty("version").GetString()}")
                    .ToHashSet(StringComparer.Ordinal);
            }

            Assert.Equal(DirectKeys(@default.Stdout), AllKeys(directOnly.Stdout));
            Assert.Equal(AllKeys(@default.Stdout), AllKeys(includeTransitive.Stdout));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(root);
        }
    }
}
