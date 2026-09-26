using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for go/cargo (issue #11). Subprocess e2e via CliTestHelpers;
/// fixtures are local files only (no live network). Phantom go.mod/Cargo.toml
/// fixtures guarantee Unknown licenses (404 or offline cache-miss), so
/// --strict deterministically exits 1.
/// </summary>
public sealed class GoCargoCliTests
{
    private static string CreateGoStrictFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "go.mod"),
            "module example.com/olaf-strict-fixture\n\ngo 1.21\n\nrequire example.com/this-module-definitely-does-not-exist-olaf-xyz v9.9.9\n");
        return dir;
    }

    private static string CreateCargoStrictFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "Cargo.toml"),
            "[package]\nname = \"olaf-strict-fixture\"\nversion = \"1.0.0\"\nedition = \"2021\"\n\n[dependencies]\nthis-crate-definitely-does-not-exist-olaf-xyz = \"9.9.9\"\n");
        return dir;
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemGoFiltersGoMod()
    {
        var input = CliTestHelpers.FixturePath("go", "go.mod");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "go");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("cobra", result.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemCargoFiltersCargoToml()
    {
        var input = CliTestHelpers.FixturePath("cargo", "Cargo.toml");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "cargo");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("serde", result.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    [Fact]
    public void Should_Exit2_When_EcosystemFilterMatchesNothing()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "go");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Should_Exit2WithSupportedList_When_EcosystemInvalid()
    {
        var input = CliTestHelpers.FixturePath("go", "go.mod");

        var result = CliTestHelpers.RunCli("--input", input, "--ecosystem", "vcpkg");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Supported:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("go", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("cargo", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Exit1_When_GoStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateGoStrictFixtureDir();
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
    public void Should_Exit1_When_CargoStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateCargoStrictFixtureDir();
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
    public void Should_Exit0_When_GoNotStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateGoStrictFixtureDir();
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
