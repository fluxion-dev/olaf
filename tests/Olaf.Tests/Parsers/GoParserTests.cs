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

    [Fact]
    public void Should_Handle_File_When_CanHandleGoSum()
    {
        var parser = ParserTestHelpers.ResolveParser("go");

        Assert.True(parser.CanHandle("go.sum"));
        Assert.True(parser.CanHandle("/some/dir/go.sum"));
        Assert.True(parser.CanHandle("GO.SUM"));
    }

    [Fact]
    public void Should_ReturnFourDeps_When_DirHasGoModAndGoSum()
    {
        // Pair-count-invariance: go.sum has 2 lines per module (h1: + /go.mod h1:),
        // 8 lines total, but the dir parse must still yield exactly 4 deps (no double-count).
        var parser = ParserTestHelpers.ResolveParser("go");
        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.mod"), tempDir, "go.mod");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.sum"), tempDir, "go.sum");

            var deps = parser.Parse(tempDir);

            Assert.Equal(4, deps.Count);
            Assert.All(deps, d => Assert.Equal("go", d.Ecosystem));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_MarkIndirectAsTransitive_When_PresentInGoSum()
    {
        var parser = ParserTestHelpers.ResolveParser("go");
        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.mod"), tempDir, "go.mod");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.sum"), tempDir, "go.sum");

            var byName = parser.Parse(tempDir).ToDictionary(d => d.Name);

            Assert.True(byName["gopkg.in/yaml.v3"].IsTransitive);
            Assert.False(byName["gopkg.in/yaml.v3"].Direct);
            Assert.True(byName["github.com/spf13/pflag"].IsTransitive);
            Assert.False(byName["github.com/spf13/pflag"].Direct);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_KeepDirectAsDirect_When_PresentInGoSum()
    {
        var parser = ParserTestHelpers.ResolveParser("go");
        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.mod"), tempDir, "go.mod");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.sum"), tempDir, "go.sum");

            var byName = parser.Parse(tempDir).ToDictionary(d => d.Name);

            Assert.False(byName["github.com/spf13/cobra"].IsTransitive);
            Assert.True(byName["github.com/spf13/cobra"].Direct);
            Assert.False(byName["golang.org/x/text"].IsTransitive);
            Assert.True(byName["golang.org/x/text"].Direct);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_FallBackIndirectToDirect_When_AbsentFromGoSum()
    {
        // Partial go.sum: only the direct modules are present, so the
        // // indirect requires fall back to direct (never phantom transitive).
        var parser = ParserTestHelpers.ResolveParser("go");
        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.mod"), tempDir, "go.mod");
            File.WriteAllText(Path.Combine(tempDir, "go.sum"),
                "github.com/spf13/cobra v1.8.0 h1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\n" +
                "github.com/spf13/cobra v1.8.0/go.mod h1:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=\n" +
                "golang.org/x/text v0.14.0 h1:CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC=\n" +
                "golang.org/x/text v0.14.0/go.mod h1:DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD=\n");

            var deps = parser.Parse(tempDir);
            var byName = deps.ToDictionary(d => d.Name);

            Assert.Equal(4, deps.Count);
            Assert.False(byName["gopkg.in/yaml.v3"].IsTransitive);
            Assert.True(byName["gopkg.in/yaml.v3"].Direct);
            Assert.False(byName["github.com/spf13/pflag"].IsTransitive);
            Assert.True(byName["github.com/spf13/pflag"].Direct);
            Assert.False(byName["github.com/spf13/cobra"].IsTransitive);
            Assert.False(byName["golang.org/x/text"].IsTransitive);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_KeepGoModDepsIntact_When_GoSumMalformed()
    {
        // Malformed go.sum must not break the go.mod parse of the same dir:
        // names + versions stay intact (presence set degrades to empty).
        var parser = ParserTestHelpers.ResolveParser("go");
        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.mod"), tempDir, "go.mod");
            File.WriteAllText(Path.Combine(tempDir, "go.sum"),
                "this is not a go.sum\n" +
                "bogus-line-without-enough-fields\n" +
                "github.com/spf13/cobra v1.8.0 missing-hash-prefix\n");

            var deps = parser.Parse(tempDir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal(4, deps.Count);
            Assert.Equal("v1.8.0", byName["github.com/spf13/cobra"]);
            Assert.Equal("v0.14.0", byName["golang.org/x/text"]);
            Assert.Equal("v3.0.1", byName["gopkg.in/yaml.v3"]);
            Assert.Equal("v1.0.5", byName["github.com/spf13/pflag"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_SingleFileGoSumMalformed()
    {
        var parser = ParserTestHelpers.ResolveParser("go");
        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            var bad = Path.Combine(tempDir, "go.sum");
            File.WriteAllText(bad, "this is not a go.sum\nrequire (((\n");

            var deps = parser.Parse(bad);

            Assert.NotNull(deps);
            Assert.Empty(deps);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_ReturnTransitiveDeps_When_GoSumOnlyDir()
    {
        // Lock-like semantics: go.sum-only dir derives deps from go.sum keys,
        // all marked transitive.
        var parser = ParserTestHelpers.ResolveParser("go");
        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.sum"), tempDir, "go.sum");

            var deps = parser.Parse(tempDir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal(4, deps.Count);
            Assert.Equal("v1.8.0", byName["github.com/spf13/cobra"]);
            Assert.Equal("v0.14.0", byName["golang.org/x/text"]);
            Assert.Equal("v3.0.1", byName["gopkg.in/yaml.v3"]);
            Assert.Equal("v1.0.5", byName["github.com/spf13/pflag"]);
            Assert.All(deps, d => Assert.True(d.IsTransitive));
            Assert.All(deps, d => Assert.False(d.Direct));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_ReturnTransitiveDeps_When_SingleFileGoSum()
    {
        var parser = ParserTestHelpers.ResolveParser("go");
        var path = ParserTestHelpers.FixturePath("go", "go.sum");

        var deps = parser.Parse(path);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);

        Assert.Equal(4, deps.Count);
        Assert.Equal("v1.8.0", byName["github.com/spf13/cobra"]);
        Assert.Equal("v0.14.0", byName["golang.org/x/text"]);
        Assert.Equal("v3.0.1", byName["gopkg.in/yaml.v3"]);
        Assert.Equal("v1.0.5", byName["github.com/spf13/pflag"]);
        Assert.All(deps, d => Assert.True(d.IsTransitive));
    }
}
