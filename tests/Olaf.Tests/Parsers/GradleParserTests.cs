namespace Olaf.Tests.Parsers;

/// <summary>
/// Gradle ecosystem: build.gradle implementation/api/testImplementation
/// "group:name:version" + libs.versions.toml [libraries] (inline table,
/// short form, and module + version.ref).
/// Fixture build.gradle has 4 pinned deps + a commented-out dep and a
/// version-catalog accessor (both must be skipped).
/// Fixture libs.versions.toml has 3 libraries.
/// </summary>
public sealed class GradleParserTests
{
    [Fact]
    public void Should_ReturnFourDeps_When_BuildGradleHasPinnedCoords()
    {
        var parser = ParserTestHelpers.ResolveParser("gradle");
        var path = ParserTestHelpers.FixturePath("gradle", "build.gradle");

        var deps = parser.Parse(path);

        Assert.Equal(4, deps.Count);
        Assert.All(deps, d => Assert.Equal("gradle", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("32.1.2-jre", byName["com.google.guava:guava"]);
        Assert.Equal("3.14.0", byName["org.apache.commons:commons-lang3"]);
        Assert.Equal("4.13.2", byName["junit:junit"]);
        Assert.Equal("2.0.9", byName["org.slf4j:slf4j-simple"]);
    }

    [Fact]
    public void Should_SkipCommentAndAccessor_When_BuildGradleHasNonPinned()
    {
        var parser = ParserTestHelpers.ResolveParser("gradle");
        var path = ParserTestHelpers.FixturePath("gradle", "build.gradle");

        var deps = parser.Parse(path);

        Assert.DoesNotContain(deps, d => d.Name == "org.example:commented-out");
        Assert.DoesNotContain(deps, d => d.Name.Contains("libs.", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_ReturnThreeDeps_When_VersionCatalog()
    {
        var parser = ParserTestHelpers.ResolveParser("gradle");
        var path = ParserTestHelpers.FixturePath("gradle", "libs.versions.toml");

        var deps = parser.Parse(path);

        Assert.Equal(3, deps.Count);
        Assert.All(deps, d => Assert.Equal("gradle", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("32.1.2-jre", byName["com.google.guava:guava"]);
        Assert.Equal("3.14.0", byName["org.apache.commons:commons-lang3"]);
        Assert.Equal("4.13.2", byName["junit:junit"]);
    }

    [Fact]
    public void Should_Handle_File_When_CanHandleGradleManifests()
    {
        var parser = ParserTestHelpers.ResolveParser("gradle");

        Assert.True(parser.CanHandle("build.gradle"));
        Assert.True(parser.CanHandle("/some/dir/build.gradle"));
        Assert.True(parser.CanHandle("build.gradle.kts"));
        Assert.True(parser.CanHandle("/some/dir/build.gradle.kts"));
        Assert.True(parser.CanHandle("libs.versions.toml"));
        Assert.True(parser.CanHandle("/some/dir/libs.versions.toml"));
    }

    [Fact]
    public void Should_NotHandle_File_When_UnrelatedManifest()
    {
        var parser = ParserTestHelpers.ResolveParser("gradle");

        Assert.False(parser.CanHandle("package.json"));
        Assert.False(parser.CanHandle("pom.xml"));
        Assert.False(parser.CanHandle("go.mod"));
        Assert.False(parser.CanHandle("Cargo.toml"));
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_BuildGradleMissing()
    {
        var parser = ParserTestHelpers.ResolveParser("gradle");
        var missing = Path.Combine(ParserTestHelpers.CreateTempDir(), "build.gradle");

        var deps = parser.Parse(missing);

        Assert.NotNull(deps);
        Assert.Empty(deps);
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_CatalogMalformed()
    {
        var parser = ParserTestHelpers.ResolveParser("gradle");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            var bad = Path.Combine(dir, "libs.versions.toml");
            File.WriteAllText(bad, "[libraries]\nguava = { group = ");

            var deps = parser.Parse(bad);

            Assert.NotNull(deps);
            Assert.Empty(deps);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_MergeAll_When_DirectoryHasGradleAndCatalog()
    {
        var parser = ParserTestHelpers.ResolveParser("gradle");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("gradle", "build.gradle"), dir, "build.gradle");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("gradle", "libs.versions.toml"), dir, "libs.versions.toml");

            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("32.1.2-jre", byName["com.google.guava:guava"]);
            Assert.Equal("2.0.9", byName["org.slf4j:slf4j-simple"]);
            Assert.All(deps, d =>
            {
                Assert.False(d.IsTransitive);
                Assert.True(d.Direct);
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ParseKts_When_GivenKotlinDslFile()
    {
        var parser = ParserTestHelpers.ResolveParser("gradle");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            var kts = Path.Combine(dir, "build.gradle.kts");
            File.WriteAllText(kts, "dependencies {\n    implementation(\"com.google.guava:guava:32.1.2-jre\")\n}\n");

            var deps = parser.Parse(kts);

            Assert.Single(deps);
            Assert.Equal("com.google.guava:guava", deps[0].Name);
            Assert.Equal("32.1.2-jre", deps[0].Version);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
