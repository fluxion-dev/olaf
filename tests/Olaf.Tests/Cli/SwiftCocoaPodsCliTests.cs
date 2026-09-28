using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for swift/cocoapods (issue #14, generate-only shape for
/// issue #123). Subprocess e2e via CliTestHelpers; each run scans a temp dir
/// holding the committed fixture file (no live network). Phantom
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

    private static string CopyFixtureToTempDir(string ecoDir, string fileName)
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.Copy(CliTestHelpers.FixturePath(ecoDir, fileName), Path.Combine(dir, fileName));
        return dir;
    }

    [Fact]
    public void Should_ExitZero_When_GenerateSwiftFixture()
    {
        var dir = CopyFixtureToTempDir("swift", "Package.swift");
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Alamofire", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_ExitZero_When_GenerateCocoaPodsFixture()
    {
        var dir = CopyFixtureToTempDir("cocoapods", "Podfile");
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Alamofire", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_Exit1_When_SwiftStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateSwiftStrictFixtureDir();
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
    public void Should_Exit1_When_CocoaPodsStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateCocoaPodsStrictFixtureDir();
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
    public void Should_Exit0_When_SwiftNotStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateSwiftStrictFixtureDir();
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
