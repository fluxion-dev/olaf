using static Olaf.Tests.Cli.CliTestHelpers;

namespace Olaf.Tests.Cli;

/// <summary>
/// Issue #74: --group-by-license CLI surface (subprocess e2e, offline only —
/// --help needs no input; the SBOM-ignore case uses a temp phantom
/// package.json with a never-resolvable dependency, so the effective SPDX is
/// always Unknown via offline cache-miss — no committed fixtures, no network).
/// </summary>
public sealed class GroupByLicenseCliTests
{
    [Fact]
    public void Should_ListGroupByLicenseFlag_When_HelpRequested()
    {
        var result = RunCli("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--group-by-license", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_IgnoreFlagWithVerboseNote_When_SbomFormat()
    {
        var fixtureDir = CreateStrictFixtureDir();
        try
        {
            var result = RunCli("--input", fixtureDir, "--format", "spdx-json", "--group-by-license", "--verbose");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("--group-by-license ignored for SBOM format 'spdx-json'.", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("packages under", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(fixtureDir);
        }
    }
}
