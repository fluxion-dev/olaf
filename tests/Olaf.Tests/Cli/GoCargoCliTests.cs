using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for go/cargo (issue #11, generate-only shape for issue
/// #123). Subprocess e2e via CliTestHelpers; each run scans a temp dir
/// holding the committed fixture file (no live network). Phantom
/// go.mod/Cargo.toml fixtures guarantee Unknown licenses (404 or offline
/// cache-miss), so --strict deterministically exits 1.
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

    private static string CopyFixtureToTempDir(string ecoDir, string fileName)
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.Copy(CliTestHelpers.FixturePath(ecoDir, fileName), Path.Combine(dir, fileName));
        return dir;
    }

    [Fact]
    public void Should_ExitZero_When_GenerateGoFixture()
    {
        var dir = CopyFixtureToTempDir("go", "go.mod");
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("cobra", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_ExitZero_When_GenerateCargoFixture()
    {
        var dir = CopyFixtureToTempDir("cargo", "Cargo.toml");
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("serde", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_Exit1_When_GoStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateGoStrictFixtureDir();
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
    public void Should_Exit1_When_CargoStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateCargoStrictFixtureDir();
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
    public void Should_Exit0_When_GoNotStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateGoStrictFixtureDir();
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
