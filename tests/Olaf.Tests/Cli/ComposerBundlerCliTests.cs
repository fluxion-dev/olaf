using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for composer/bundler (issue #13, generate-only shape for
/// issue #123). Subprocess e2e via CliTestHelpers; each run scans a temp dir
/// holding the committed fixture file (no live network). Phantom
/// composer.json/Gemfile fixtures guarantee Unknown licenses (404 or offline
/// cache-miss), so --strict deterministically exits 1.
/// </summary>
public sealed class ComposerBundlerCliTests
{
    private static string CreateComposerStrictFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "composer.json"),
            """{"name":"example/olaf-strict-fixture","require":{"example/this-package-definitely-does-not-exist-olaf-xyz":"9.9.9"}}""");
        return dir;
    }

    private static string CreateBundlerStrictFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "Gemfile"),
            "source \"https://rubygems.org\"\n\ngem \"this-gem-definitely-does-not-exist-olaf-xyz\", \"9.9.9\"\n");
        return dir;
    }

    private static string CopyFixtureToTempDir(string ecoDir, string fileName)
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.Copy(CliTestHelpers.FixturePath(ecoDir, fileName), Path.Combine(dir, fileName));
        return dir;
    }

    [Fact]
    public void Should_ExitZero_When_GenerateComposerFixture()
    {
        var dir = CopyFixtureToTempDir("composer", "composer.json");
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("monolog", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_ExitZero_When_GenerateBundlerFixture()
    {
        var dir = CopyFixtureToTempDir("bundler", "Gemfile");
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("rails", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_Exit1_When_ComposerStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateComposerStrictFixtureDir();
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
    public void Should_Exit1_When_BundlerStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateBundlerStrictFixtureDir();
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
    public void Should_Exit0_When_ComposerNotStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateComposerStrictFixtureDir();
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
