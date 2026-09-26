using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Registry coverage for maven/gradle (issue #12): ecosystem filters
/// and nested monorepo discovery alongside existing ecosystems.
/// </summary>
public sealed class MavenGradleRegistryTests
{
    [Fact]
    public void Should_FilterByEcosystem_When_MavenFilterGiven()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("maven", "pom.xml"),
                Path.Combine(root, "javasvc"), "pom.xml");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package.json"),
                Path.Combine(root, "websvc"), "package.json");

            var registry = new ParserRegistry();

            var mavenOnly = registry.Scan(root, "maven");
            Assert.NotEmpty(mavenOnly);
            Assert.All(mavenOnly, d => Assert.Equal("maven", d.Ecosystem));
            Assert.Contains(mavenOnly, d => d.Name == "com.google.guava:guava");

            // No gradle manifests under root: filtered scan throws (no manifests).
            var ex = Assert.Throws<InvalidOperationException>(() => registry.Scan(root, "gradle"));
            Assert.Contains("No manifests", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Should_FilterByEcosystem_When_GradleFilterGiven()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("gradle", "build.gradle"),
                Path.Combine(root, "javasvc"), "build.gradle");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package.json"),
                Path.Combine(root, "websvc"), "package.json");

            var registry = new ParserRegistry();

            var gradleOnly = registry.Scan(root, "gradle");
            Assert.NotEmpty(gradleOnly);
            Assert.All(gradleOnly, d => Assert.Equal("gradle", d.Ecosystem));
            Assert.Contains(gradleOnly, d => d.Name == "com.google.guava:guava");
            Assert.DoesNotContain(gradleOnly, d => d.Name == "express");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Should_FindAllEcosystems_When_NestedMavenAndGradleDirs()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("maven", "pom.xml"),
                Path.Combine(root, "javasvc"), "pom.xml");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("gradle", "build.gradle"),
                Path.Combine(root, "kotlinsvc"), "build.gradle");

            var deps = new ParserRegistry().Scan(root);

            Assert.Contains(deps, d => d.Ecosystem == "maven" && d.Name == "com.google.guava:guava");
            Assert.Contains(deps, d => d.Ecosystem == "gradle" && d.Name == "junit:junit");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
