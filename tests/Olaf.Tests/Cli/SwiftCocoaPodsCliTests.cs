using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for swift/cocoapods (issue #14). Subprocess e2e via
/// CliTestHelpers; fixtures are local files only (no live network). Phantom
/// Package.swift/Podfile fixtures guarantee Unknown licenses (404 or offline
/// cache-miss), so --strict deterministically exits 1.
/// </summary>
public sealed class SwiftCocoaPodsCliTests
{
    private static string CreateSwiftStrictFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "Package.swift"),
            "// swift-tools-version: 5.9\nimport PackageDescription\n\nlet package = Package(\n    name: \"OlafStrictFixture\",\n    dependencies: [\n        .package(url: \"https://github.com/example/this-package-definitely-does-not-exist-olaf-xyz.git\", from: \"9.9.9\"),\n    ]\n)\n");
        return dir;
    }

    private static string CreateCocoaPodsStrictFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "Podfile"),
            "platform :ios, '16.0'\n\ntarget 'OlafStrictFixture' do\n  pod 'this-pod-definitely-does-not-exist-olaf-xyz', '9.9.9'\nend\n");
        return dir;
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemSwiftFiltersPackageSwift()
    {
        var input = CliTestHelpers.FixturePath("swift", "Package.swift");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "swift");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Alamofire", result.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemCocoaPodsFiltersPodfile()
    {
        var input = CliTestHelpers.FixturePath("cocoapods", "Podfile");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "cocoapods");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Alamofire", result.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    [Fact]
    public void Should_Exit2_When_EcosystemFilterMatchesNothing()
    {
        var input = CliTestHelpers.FixturePath("swift", "Package.swift");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "cocoapods");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Should_Exit2WithSupportedList_When_EcosystemInvalidSpm()
    {
        var input = CliTestHelpers.FixturePath("swift", "Package.swift");

        var result = CliTestHelpers.RunCli("--input", input, "--ecosystem", "spm");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Supported:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("swift", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("cocoapods", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Exit2WithSupportedList_When_EcosystemInvalidConda()
    {
        var input = CliTestHelpers.FixturePath("cocoapods", "Podfile");

        var result = CliTestHelpers.RunCli("--input", input, "--ecosystem", "conda");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Supported:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("swift", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("cocoapods", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Exit1_When_SwiftStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateSwiftStrictFixtureDir();
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
    public void Should_Exit1_When_CocoaPodsStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateCocoaPodsStrictFixtureDir();
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
    public void Should_Exit0_When_SwiftNotStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateSwiftStrictFixtureDir();
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
