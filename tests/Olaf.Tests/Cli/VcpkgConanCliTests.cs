using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for vcpkg/conan (issue #15). Subprocess e2e via
/// CliTestHelpers; fixtures are local files only (no live network). Phantom
/// vcpkg.json/conanfile.txt fixtures guarantee Unknown licenses (404 or
/// offline cache-miss), so --strict deterministically exits 1.
/// </summary>
public sealed class VcpkgConanCliTests
{
    private static string CreateVcpkgStrictFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "vcpkg.json"),
            """{"name":"olaf-strict-fixture","version":"1.0.0","dependencies":["this-port-definitely-does-not-exist-olaf-xyz"]}""");
        return dir;
    }

    private static string CreateConanStrictFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "conanfile.txt"),
            "[requires]\nthis-package-definitely-does-not-exist-olaf-xyz/9.9.9\n");
        return dir;
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemVcpkgFiltersVcpkgJson()
    {
        var input = CliTestHelpers.FixturePath("vcpkg", "vcpkg.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "vcpkg");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fmt", result.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemConanFiltersConanfile()
    {
        var input = CliTestHelpers.FixturePath("conan", "conanfile.txt");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "conan");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fmt", result.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    [Fact]
    public void Should_Exit2_When_EcosystemFilterMatchesNothing()
    {
        var input = CliTestHelpers.FixturePath("vcpkg", "vcpkg.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "conan");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Should_Exit1_When_VcpkgStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateVcpkgStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--strict");

            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    [Fact]
    public void Should_Exit1_When_ConanStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateConanStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--strict");

            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    [Fact]
    public void Should_Exit0_When_VcpkgNotStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateVcpkgStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }
}
