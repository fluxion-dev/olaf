using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Extended JS locks (issue #16): pnpm-lock.yaml, yarn.lock, bun.lock/bun.lockb.
/// Ecosystem stays "npm"; lock entries are transitive; lock beats manifest.
/// </summary>
public sealed class NpmExtendedLockTests
{
    [Fact]
    public void Should_ReturnTransitiveDeps_When_PnpmLockHasPackages()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");
        var path = ParserTestHelpers.FixturePath("npm", "pnpm-lock.yaml");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.Equal("npm", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("4.18.2", byName["express"]);
        Assert.Equal("4.3.4", byName["debug"]);
        Assert.Equal("1.2.3", byName["@scope/tool"]);
        Assert.All(deps, d => Assert.Equal(!d.IsTransitive, d.Direct));
        Assert.All(deps, d => Assert.True(d.IsTransitive));
    }

    [Fact]
    public void Should_ReturnTransitiveDeps_When_YarnLockHasSpecBlocks()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");
        var path = ParserTestHelpers.FixturePath("npm", "yarn.lock");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        // Multi-spec block dedups to one entry; quoted scoped spec keeps scope.
        Assert.Equal("4.18.2", byName["express"]);
        Assert.Equal("4.3.4", byName["debug"]);
        Assert.Equal("1.2.3", byName["@scope/tool"]);
        Assert.All(deps, d => Assert.True(d.IsTransitive));
    }

    [Fact]
    public void Should_ReturnTransitiveDeps_When_BunLockHasPackages()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");
        var path = ParserTestHelpers.FixturePath("npm", "bun.lock");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("4.18.2", byName["express"]);
        Assert.Equal("4.3.4", byName["debug"]);
        Assert.All(deps, d => Assert.True(d.IsTransitive));
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_BunLockbIsBinary()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");
        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            var lockb = Path.Combine(tempDir, "bun.lockb");
            File.WriteAllBytes(lockb, new byte[] { 0x62, 0x75, 0x6E, 0x00, 0x01, 0x02, 0xFF });

            var deps = parser.Parse(lockb);

            Assert.NotNull(deps);
            Assert.Empty(deps);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_Handle_File_When_CanHandleExtendedLocks()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");

        Assert.True(parser.CanHandle("pnpm-lock.yaml"));
        Assert.True(parser.CanHandle("yarn.lock"));
        Assert.True(parser.CanHandle("bun.lock"));
        Assert.True(parser.CanHandle("bun.lockb"));
    }

    [Fact]
    public void Should_PreferLock_When_PnpmLockAndManifestCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package.json"), dir, "package.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "pnpm-lock.yaml"), dir, "pnpm-lock.yaml");

            var deps = ParserTestHelpers.ResolveParser("npm").Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Pinned lock version wins over the "^4.18.2" manifest range;
            // manifest-only deps (react, lodash, vitest) are absent.
            Assert.Equal("4.18.2", byName["express"]);
            Assert.DoesNotContain("react", byName.Keys);
            Assert.DoesNotContain("vitest", byName.Keys);
            Assert.All(deps, d => Assert.True(d.IsTransitive));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferLock_When_YarnLockAndManifestCoexistViaRegistry()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package.json"), dir, "package.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "yarn.lock"), dir, "yarn.lock");

            var deps = new ParserRegistry().Scan(dir);
            var npm = deps.Where(d => d.Ecosystem == "npm").ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("4.18.2", npm["express"]);
            Assert.DoesNotContain("react", npm.Keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
