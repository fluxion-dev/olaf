using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for composer/bundler (issue #13). Subprocess e2e via
/// CliTestHelpers; fixtures are local files only (no live network). Phantom
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

    [Fact]
    public void Should_ExitZero_When_EcosystemComposerFiltersComposerJson()
    {
        var input = CliTestHelpers.FixturePath("composer", "composer.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "composer");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("monolog", result.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemBundlerFiltersGemfile()
    {
        var input = CliTestHelpers.FixturePath("bundler", "Gemfile");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "bundler");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("rails", result.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    [Fact]
    public void Should_Exit2WithSupportedList_When_EcosystemInvalid()
    {
        var input = CliTestHelpers.FixturePath("composer", "composer.json");

        var result = CliTestHelpers.RunCli("--input", input, "--ecosystem", "vcpkg");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Supported:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("composer", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("bundler", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Exit2_When_EcosystemFilterMatchesNothing()
    {
        var input = CliTestHelpers.FixturePath("composer", "composer.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "bundler");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Should_Exit1_When_ComposerStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateComposerStrictFixtureDir();
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
    public void Should_Exit1_When_BundlerStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateBundlerStrictFixtureDir();
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
    public void Should_Exit0_When_ComposerNotStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateComposerStrictFixtureDir();
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
