namespace Olaf.Tests.Parsers;

/// <summary>
/// Go ecosystem: go.mod direct + indirect requires.
/// Fixture has 4 requires: cobra (single-line, direct), x/text (block, direct),
/// yaml.v3 + pflag (block, // indirect).
/// Uses ParserTestHelpers.ResolveParser("go") — safe from #23 collision:
/// parser lookup matches Ecosystem equality (not name-Contains like resolvers).
/// </summary>
public sealed class GoParserTests
{
    [Fact]
    public void Should_ReturnFourDeps_When_GoModHasSingleAndBlockRequires()
    {
        var parser = ParserTestHelpers.ResolveParser("go");
        var path = ParserTestHelpers.FixturePath("go", "go.mod");

        var deps = parser.Parse(path);

        Assert.Equal(4, deps.Count);
        Assert.All(deps, d => Assert.Equal("go", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("v1.8.0", byName["github.com/spf13/cobra"]);
        Assert.Equal("v0.14.0", byName["golang.org/x/text"]);
        Assert.Equal("v3.0.1", byName["gopkg.in/yaml.v3"]);
        Assert.Equal("v1.0.5", byName["github.com/spf13/pflag"]);
    }

    [Fact]
    public void Should_MarkIndirect_When_IndirectCommentPresent()
    {
        var parser = ParserTestHelpers.ResolveParser("go");
        var path = ParserTestHelpers.FixturePath("go", "go.mod");

        var deps = parser.Parse(path);
        var byName = deps.ToDictionary(d => d.Name);

        Assert.False(byName["github.com/spf13/cobra"].IsTransitive);
        Assert.True(byName["github.com/spf13/cobra"].Direct);
        Assert.False(byName["golang.org/x/text"].IsTransitive);
        Assert.True(byName["golang.org/x/text"].Direct);
        Assert.True(byName["gopkg.in/yaml.v3"].IsTransitive);
        Assert.False(byName["gopkg.in/yaml.v3"].Direct);
        Assert.True(byName["github.com/spf13/pflag"].IsTransitive);
        Assert.False(byName["github.com/spf13/pflag"].Direct);

        // Direct == !IsTransitive invariant.
        Assert.All(deps, d => Assert.Equal(!d.IsTransitive, d.Direct));
    }

    [Fact]
    public void Should_Handle_File_When_CanHandleGoMod()
    {
        var parser = ParserTestHelpers.ResolveParser("go");

        Assert.True(parser.CanHandle("go.mod"));
        Assert.True(parser.CanHandle("/some/dir/go.mod"));
    }

    [Fact]
    public void Should_NotHandle_File_When_UnrelatedManifest()
    {
        var parser = ParserTestHelpers.ResolveParser("go");

        Assert.False(parser.CanHandle("package.json"));
        Assert.False(parser.CanHandle("Cargo.toml"));
        Assert.False(parser.CanHandle("requirements.txt"));
        Assert.False(parser.CanHandle("sample.csproj"));
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_GoModMalformed()
    {
        var parser = ParserTestHelpers.ResolveParser("go");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            var bad = Path.Combine(dir, "go.mod");
            File.WriteAllText(bad, "this is not a go.mod\nrequire (((\n");

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
    public void Should_ReturnEmptyWithoutThrow_When_GoModMissing()
    {
        var parser = ParserTestHelpers.ResolveParser("go");
        var missing = Path.Combine(ParserTestHelpers.CreateTempDir(), "go.mod");

        var deps = parser.Parse(missing);

        Assert.NotNull(deps);
        Assert.Empty(deps);
    }

    [Fact]
    public void Should_ParseDirectory_When_GivenDirWithGoMod()
    {
        var parser = ParserTestHelpers.ResolveParser("go");
        var tempDir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("go", "go.mod"), "go.mod");
        try
        {
            var deps = parser.Parse(tempDir);

            Assert.NotEmpty(deps);
            Assert.Contains(deps, d => d.Name == "github.com/spf13/cobra" && d.Version == "v1.8.0");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
