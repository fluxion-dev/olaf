using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for maven/gradle (issue #12). Subprocess e2e via CliTestHelpers;
/// fixtures are local files only (no live network). Phantom pom.xml/build.gradle
/// fixtures guarantee Unknown licenses (404 or offline cache-miss), so
/// --strict deterministically exits 1.
/// </summary>
public sealed class MavenGradleCliTests
{
    private static string CreateMavenStrictFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "pom.xml"),
            """
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <groupId>org.example</groupId>
              <artifactId>olaf-strict-fixture</artifactId>
              <version>1.0.0</version>
              <dependencies>
                <dependency>
                  <groupId>org.example</groupId>
                  <artifactId>this-artifact-definitely-does-not-exist-olaf-xyz</artifactId>
                  <version>9.9.9</version>
                </dependency>
              </dependencies>
            </project>
            """);
        return dir;
    }

    private static string CreateGradleStrictFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "build.gradle"),
            "dependencies {\n    implementation 'org.example:this-artifact-definitely-does-not-exist-olaf-xyz:9.9.9'\n}\n");
        return dir;
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemMavenFiltersPom()
    {
        var input = CliTestHelpers.FixturePath("maven", "pom.xml");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "maven");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("guava", result.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemGradleFiltersBuildGradle()
    {
        var input = CliTestHelpers.FixturePath("gradle", "build.gradle");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "gradle");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("guava", result.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemGradleFiltersCatalog()
    {
        var input = CliTestHelpers.FixturePath("gradle", "libs.versions.toml");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "gradle");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("guava", result.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    [Fact]
    public void Should_Exit2_When_EcosystemFilterMatchesNothing()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "maven");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Should_Exit1_When_MavenStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateMavenStrictFixtureDir();
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
    public void Should_Exit1_When_GradleStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateGradleStrictFixtureDir();
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
    public void Should_Exit0_When_MavenNotStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateMavenStrictFixtureDir();
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
