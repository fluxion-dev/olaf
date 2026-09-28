using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for vcpkg/conan (issue #15, generate-only shape for issue
/// #123). Subprocess e2e via CliTestHelpers; each run scans a temp dir
/// holding the committed fixture file (no live network). Phantom
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

    private static string CopyFixtureToTempDir(string ecoDir, string fileName)
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.Copy(CliTestHelpers.FixturePath(ecoDir, fileName), Path.Combine(dir, fileName));
        return dir;
    }

    [Fact]
    public void Should_ExitZero_When_GenerateVcpkgFixture()
    {
        var dir = CopyFixtureToTempDir("vcpkg", "vcpkg.json");
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("fmt", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_ExitZero_When_GenerateConanFixture()
    {
        var dir = CopyFixtureToTempDir("conan", "conanfile.txt");
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("fmt", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_Exit1_When_VcpkgStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateVcpkgStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", fixtureDir, "--format", "json", "--strict", "--offline");

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
            var result = CliTestHelpers.RunCli("generate", fixtureDir, "--format", "json", "--strict", "--offline");

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
            var result = CliTestHelpers.RunCli("generate", fixtureDir, "--format", "json", "--offline");

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
