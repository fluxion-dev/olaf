using Olaf.Core;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Red tests for npm ecosystem: package.json direct deps + package-lock transitive.
/// Expects NpmParser : IEcosystemParser with Ecosystem == "npm".
/// </summary>
public sealed class NpmParserTests
{
    [Fact]
    public void Should_ReturnDirectDeps_When_PackageJsonHasDependenciesAndDevDependencies()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");
        var path = ParserTestHelpers.FixturePath("npm", "package.json");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.Equal("npm", d.Ecosystem));
        // Direct manifests are not transitive.
        Assert.All(deps, d =>
        {
            Assert.False(d.IsTransitive);
            Assert.True(d.Direct);
        });

        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("^4.18.2", byName["express"]);
        Assert.Equal("~4.17.21", byName["lodash"]);
        Assert.Equal(">=18.2.0", byName["react"]);
        Assert.Equal("^1.0.0", byName["vitest"]);
    }

    [Fact]
    public void Should_ReturnTransitiveDeps_When_PackageLockHasPackages()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");
        var path = ParserTestHelpers.FixturePath("npm", "package-lock.json");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("4.18.2", byName["express"]);
        Assert.Equal("4.3.4", byName["debug"]);
        // Lockfile entries represent the transitive closure: Direct == !IsTransitive invariant.
        Assert.All(deps, d => Assert.Equal(!d.IsTransitive, d.Direct));
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_PackageJsonMalformed()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");
        var path = ParserTestHelpers.FixturePath("npm", "malformed-package.json");

        // Must warn + return empty, not crash.
        var deps = parser.Parse(path);

        Assert.NotNull(deps);
        Assert.Empty(deps);
    }

    [Fact]
    public void Should_UseWildcard_When_PackageJsonDepMissingVersion()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");
        var path = ParserTestHelpers.FixturePath("npm", "package-missing-version.json");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.Equal("*", d.Version));
    }

    [Fact]
    public void Should_Handle_File_When_CanHandlePackageJson()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");

        Assert.True(parser.CanHandle("package.json"));
        Assert.True(parser.CanHandle("/some/dir/package.json"));
    }

    [Fact]
    public void Should_Handle_File_When_CanHandlePackageLock()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");

        Assert.True(parser.CanHandle("package-lock.json"));
    }

    [Fact]
    public void Should_NotHandle_File_When_UnrelatedManifest()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");

        Assert.False(parser.CanHandle("sample.csproj"));
        Assert.False(parser.CanHandle("requirements.txt"));
    }

    [Fact]
    public void Should_ParseDirectory_When_GivenDirWithPackageJson()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");
        var tempDir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("npm", "package.json"), "package.json");
        try
        {
            var deps = parser.Parse(tempDir);

            Assert.NotEmpty(deps);
            Assert.Contains(deps, d => d.Name == "express" && d.Version == "^4.18.2");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_ReturnScopedDeps_When_PackageLockHasScopedPackages()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");
        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package-lock-scoped.json"),
                tempDir, "package-lock.json");
            var deps = parser.Parse(tempDir);

            Assert.NotEmpty(deps);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);
            Assert.Equal("7.10.0", byName["@babel/code-frame"]);
            Assert.Equal("20.5.0", byName["@types/node"]);
            Assert.Equal("4.3.4", byName["debug"]);
            Assert.All(deps.Where(d => d.Name.StartsWith('@')), d => Assert.True(d.IsTransitive));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_SkipInvalidPaths_When_PackageLockHasNonScopedSlashEntries()
    {
        var parser = ParserTestHelpers.ResolveParser("npm");
        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package-lock-invalid-scope.json"),
                tempDir, "package-lock.json");
            var deps = parser.Parse(tempDir);

            Assert.NotEmpty(deps);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);
            Assert.Equal("2.0.0", byName["baz"]);
            Assert.DoesNotContain("foo/bar", byName.Keys);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
