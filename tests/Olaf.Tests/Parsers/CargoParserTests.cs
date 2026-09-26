namespace Olaf.Tests.Parsers;

/// <summary>
/// Cargo ecosystem: Cargo.toml direct deps + Cargo.lock transitive closure.
/// Fixture Cargo.toml has 3 direct (serde, tokio w/ inline table, anyhow w/ comment).
/// Fixture Cargo.lock has 3 non-root (serde, tokio, mio) with root package excluded
/// via sibling Cargo.toml [package] name lookup.
/// Uses ParserTestHelpers.ResolveParser("cargo") — safe from #23 collision:
/// parser lookup matches Ecosystem equality (not name-Contains like resolvers).
/// </summary>
public sealed class CargoParserTests
{
    [Fact]
    public void Should_ReturnDirectDeps_When_CargoToml()
    {
        var parser = ParserTestHelpers.ResolveParser("cargo");
        var path = ParserTestHelpers.FixturePath("cargo", "Cargo.toml");

        var deps = parser.Parse(path);

        Assert.Equal(3, deps.Count);
        Assert.All(deps, d => Assert.Equal("cargo", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("1.0.197", byName["serde"]);
        Assert.Equal("1.36.0", byName["tokio"]);
        Assert.Equal("1.0.81", byName["anyhow"]);
        Assert.All(deps, d =>
        {
            Assert.False(d.IsTransitive);
            Assert.True(d.Direct);
        });
    }

    [Fact]
    public void Should_ReturnTransitiveRootExcluded_When_CargoLock()
    {
        var parser = ParserTestHelpers.ResolveParser("cargo");
        var path = ParserTestHelpers.FixturePath("cargo", "Cargo.lock");

        var deps = parser.Parse(path);

        Assert.Equal(3, deps.Count);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("1.0.197", byName["serde"]);
        Assert.Equal("1.36.0", byName["tokio"]);
        Assert.Equal("0.8.11", byName["mio"]);
        Assert.DoesNotContain("olaf-cargo-fixture", byName.Keys);
        // Lockfile entries represent the transitive closure: Direct == !IsTransitive.
        Assert.All(deps, d =>
        {
            Assert.True(d.IsTransitive);
            Assert.False(d.Direct);
        });
    }

    [Fact]
    public void Should_IncludeRoot_When_LockHasNoSiblingManifest()
    {
        var parser = ParserTestHelpers.ResolveParser("cargo");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            // Copy lock alone: no sibling Cargo.toml, so no root exclusion.
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cargo", "Cargo.lock"), dir, "Cargo.lock");

            var deps = parser.Parse(Path.Combine(dir, "Cargo.lock"));
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal(4, deps.Count);
            Assert.Contains("olaf-cargo-fixture", byName.Keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferLock_When_DirectoryHasBothManifestAndLock()
    {
        var parser = ParserTestHelpers.ResolveParser("cargo");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cargo", "Cargo.toml"), dir, "Cargo.toml");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cargo", "Cargo.lock"), dir, "Cargo.lock");

            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Lock set only: mio is lock-exclusive; anyhow is manifest-only.
            Assert.Equal("1.0.197", byName["serde"]);
            Assert.Equal("1.36.0", byName["tokio"]);
            Assert.Equal("0.8.11", byName["mio"]);
            Assert.DoesNotContain("anyhow", byName.Keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_Handle_File_When_CanHandleCargoManifests()
    {
        var parser = ParserTestHelpers.ResolveParser("cargo");

        Assert.True(parser.CanHandle("Cargo.toml"));
        Assert.True(parser.CanHandle("/some/dir/Cargo.toml"));
        Assert.True(parser.CanHandle("Cargo.lock"));
        Assert.True(parser.CanHandle("/some/dir/Cargo.lock"));
    }

    [Fact]
    public void Should_NotHandle_File_When_UnrelatedManifest()
    {
        var parser = ParserTestHelpers.ResolveParser("cargo");

        Assert.False(parser.CanHandle("package.json"));
        Assert.False(parser.CanHandle("go.mod"));
        Assert.False(parser.CanHandle("requirements.txt"));
        Assert.False(parser.CanHandle("sample.csproj"));
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_CargoTomlMalformed()
    {
        var parser = ParserTestHelpers.ResolveParser("cargo");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            var bad = Path.Combine(dir, "Cargo.toml");
            File.WriteAllText(bad, "[[[ not toml {{{ \n[dependencies\nserde ====");

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
    public void Should_ReturnEmptyWithoutThrow_When_MissingFile()
    {
        var parser = ParserTestHelpers.ResolveParser("cargo");
        var missing = Path.Combine(ParserTestHelpers.CreateTempDir(), "Cargo.toml");

        var deps = parser.Parse(missing);

        Assert.NotNull(deps);
        Assert.Empty(deps);
    }

    [Fact]
    public void Should_ParseDirectory_When_GivenDirWithCargoTomlOnly()
    {
        var parser = ParserTestHelpers.ResolveParser("cargo");
        var tempDir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("cargo", "Cargo.toml"), "Cargo.toml");
        try
        {
            var deps = parser.Parse(tempDir);

            Assert.NotEmpty(deps);
            Assert.Contains(deps, d => d.Name == "serde" && d.Version == "1.0.197");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
