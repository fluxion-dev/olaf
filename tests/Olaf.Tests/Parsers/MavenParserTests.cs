namespace Olaf.Tests.Parsers;

/// <summary>
/// Maven ecosystem: pom.xml direct &lt;dependencies&gt; as group:artifact + version.
/// Fixture pom.xml has 3 direct deps (commons-lang3 pinned, guava via ${property},
/// junit test-scoped) plus a &lt;dependencyManagement&gt; entry that must be excluded.
/// </summary>
public sealed class MavenParserTests
{
    [Fact]
    public void Should_ReturnThreeDeps_When_PomHasDirectDependencies()
    {
        var parser = ParserTestHelpers.ResolveParser("maven");
        var path = ParserTestHelpers.FixturePath("maven", "pom.xml");

        var deps = parser.Parse(path);

        Assert.Equal(3, deps.Count);
        Assert.All(deps, d => Assert.Equal("maven", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("3.14.0", byName["org.apache.commons:commons-lang3"]);
        Assert.Equal("32.1.2-jre", byName["com.google.guava:guava"]);
        Assert.Equal("4.13.2", byName["junit:junit"]);
    }

    [Fact]
    public void Should_ExcludeManaged_When_DependencyManagementPresent()
    {
        var parser = ParserTestHelpers.ResolveParser("maven");
        var path = ParserTestHelpers.FixturePath("maven", "pom.xml");

        var deps = parser.Parse(path);

        Assert.DoesNotContain(deps, d => d.Name == "org.example:managed-only");
    }

    [Fact]
    public void Should_MarkDirect_When_AllDepsDirect()
    {
        var parser = ParserTestHelpers.ResolveParser("maven");
        var path = ParserTestHelpers.FixturePath("maven", "pom.xml");

        var deps = parser.Parse(path);

        Assert.All(deps, d =>
        {
            Assert.False(d.IsTransitive);
            Assert.True(d.Direct);
        });
        Assert.All(deps, d => Assert.Equal(!d.IsTransitive, d.Direct));
    }

    [Fact]
    public void Should_Handle_File_When_CanHandlePom()
    {
        var parser = ParserTestHelpers.ResolveParser("maven");

        Assert.True(parser.CanHandle("pom.xml"));
        Assert.True(parser.CanHandle("/some/dir/pom.xml"));
    }

    [Fact]
    public void Should_NotHandle_File_When_UnrelatedManifest()
    {
        var parser = ParserTestHelpers.ResolveParser("maven");

        Assert.False(parser.CanHandle("package.json"));
        Assert.False(parser.CanHandle("build.gradle"));
        Assert.False(parser.CanHandle("go.mod"));
        Assert.False(parser.CanHandle("Cargo.toml"));
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_PomMalformed()
    {
        var parser = ParserTestHelpers.ResolveParser("maven");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            var bad = Path.Combine(dir, "pom.xml");
            File.WriteAllText(bad, "<project><dependencies><oops");

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
    public void Should_ReturnEmptyWithoutThrow_When_PomMissing()
    {
        var parser = ParserTestHelpers.ResolveParser("maven");
        var missing = Path.Combine(ParserTestHelpers.CreateTempDir(), "pom.xml");

        var deps = parser.Parse(missing);

        Assert.NotNull(deps);
        Assert.Empty(deps);
    }

    [Fact]
    public void Should_ParseDirectory_When_GivenDirWithPom()
    {
        var parser = ParserTestHelpers.ResolveParser("maven");
        var tempDir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("maven", "pom.xml"), "pom.xml");
        try
        {
            var deps = parser.Parse(tempDir);

            Assert.NotEmpty(deps);
            Assert.Contains(deps, d => d.Name == "com.google.guava:guava" && d.Version == "32.1.2-jre");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
