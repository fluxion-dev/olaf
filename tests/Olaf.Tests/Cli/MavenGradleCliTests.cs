using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for maven/gradle (issue #12, generate-only shape for issue
/// #123). Subprocess e2e via CliTestHelpers; each run scans a temp dir
/// holding the committed fixture file (no live network). Phantom
/// pom.xml/build.gradle fixtures guarantee Unknown licenses (404 or offline
/// cache-miss), so --strict deterministically exits 1.
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

    private static string CopyFixtureToTempDir(string ecoDir, string fileName)
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.Copy(CliTestHelpers.FixturePath(ecoDir, fileName), Path.Combine(dir, fileName));
        return dir;
    }

    [Fact]
    public void Should_ExitZero_When_GenerateMavenFixture()
    {
        var dir = CopyFixtureToTempDir("maven", "pom.xml");
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("guava", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_ExitZero_When_GenerateGradleFixture()
    {
        var dir = CopyFixtureToTempDir("gradle", "build.gradle");
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("guava", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_ExitZero_When_GenerateGradleCatalogFixture()
    {
        var dir = CopyFixtureToTempDir("gradle", "libs.versions.toml");
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("guava", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_Exit1_When_MavenStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateMavenStrictFixtureDir();
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
    public void Should_Exit1_When_GradleStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateGradleStrictFixtureDir();
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
    public void Should_Exit0_When_MavenNotStrictAndUnknownLicenses()
    {
        var fixtureDir = CreateMavenStrictFixtureDir();
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
