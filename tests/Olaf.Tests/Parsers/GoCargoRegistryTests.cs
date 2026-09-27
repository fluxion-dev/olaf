using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Registry coverage for go/cargo (issue #11): ecosystem filters,
/// lock-over-manifest for cargo, nested monorepo discovery.
/// </summary>
public sealed class GoCargoRegistryTests
{
    [Fact]
    public void Should_FilterByEcosystem_When_GoFilterGiven()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.mod"),
                Path.Combine(root, "gosvc"), "go.mod");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package.json"),
                Path.Combine(root, "websvc"), "package.json");

            var registry = new ParserRegistry();

            var goOnly = registry.Scan(root, "go");
            Assert.NotEmpty(goOnly);
            Assert.All(goOnly, d => Assert.Equal("go", d.Ecosystem));
            Assert.Contains(goOnly, d => d.Name == "github.com/spf13/cobra");

            // No cargo manifests under root: filtered scan throws (no manifests).
            var ex = Assert.Throws<InvalidOperationException>(() => registry.Scan(root, "cargo"));
            Assert.Contains("No manifests", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Should_FilterByEcosystem_When_CargoFilterGiven()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cargo", "Cargo.toml"),
                Path.Combine(root, "rustsvc"), "Cargo.toml");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cargo", "Cargo.lock"),
                Path.Combine(root, "rustsvc"), "Cargo.lock");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package.json"),
                Path.Combine(root, "websvc"), "package.json");

            var registry = new ParserRegistry();

            var cargoOnly = registry.Scan(root, "cargo");
            Assert.NotEmpty(cargoOnly);
            Assert.All(cargoOnly, d => Assert.Equal("cargo", d.Ecosystem));
            Assert.Contains(cargoOnly, d => d.Name == "serde");
            Assert.Contains(cargoOnly, d => d.Name == "mio");
            Assert.DoesNotContain(cargoOnly, d => d.Name == "express");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferLock_When_CargoManifestAndLockCoexistViaRegistry()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cargo", "Cargo.toml"), dir, "Cargo.toml");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cargo", "Cargo.lock"), dir, "Cargo.lock");

            var deps = new ParserRegistry().Scan(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("0.8.11", byName["mio"]);
            Assert.DoesNotContain("anyhow", byName.Keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_FindAllEcosystems_When_NestedGoAndCargoDirs()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.mod"),
                Path.Combine(root, "gosvc"), "go.mod");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cargo", "Cargo.toml"),
                Path.Combine(root, "rustsvc"), "Cargo.toml");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cargo", "Cargo.lock"),
                Path.Combine(root, "rustsvc"), "Cargo.lock");

            var deps = new ParserRegistry().Scan(root);

            Assert.Contains(deps, d => d.Ecosystem == "go" && d.Name == "github.com/spf13/cobra");
            Assert.Contains(deps, d => d.Ecosystem == "cargo" && d.Name == "serde");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Should_ScanGoPairOnce_When_DirHasGoModAndGoSum()
    {
        // Registry grouping: go.mod + go.sum in one dir still yields one
        // parser call → 4 deps, no double-count.
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.mod"), dir, "go.mod");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("go", "go.sum"), dir, "go.sum");

            var deps = new ParserRegistry().Scan(dir);

            Assert.Equal(4, deps.Count);
            Assert.All(deps, d => Assert.Equal("go", d.Ecosystem));
            Assert.Contains(deps, d => d.Name == "github.com/spf13/cobra" && !d.IsTransitive);
            Assert.Contains(deps, d => d.Name == "gopkg.in/yaml.v3" && d.IsTransitive);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ScanGoSumFile_When_SingleGoSumFileGiven()
    {
        var deps = new ParserRegistry().Scan(ParserTestHelpers.FixturePath("go", "go.sum"));

        Assert.Equal(4, deps.Count);
        Assert.All(deps, d => Assert.Equal("go", d.Ecosystem));
        Assert.All(deps, d => Assert.True(d.IsTransitive));
        Assert.Contains(deps, d => d.Name == "github.com/spf13/cobra" && d.Version == "v1.8.0");
    }
}
