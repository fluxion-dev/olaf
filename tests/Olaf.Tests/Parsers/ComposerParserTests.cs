namespace Olaf.Tests.Parsers;

/// <summary>
/// Composer ecosystem: composer.json direct deps + composer.lock transitive.
/// </summary>
public sealed class ComposerParserTests
{
    [Fact]
    public void Should_ReturnDirectDeps_When_ComposerJsonHasRequireSections()
    {
        var parser = ParserTestHelpers.ResolveParser("composer");
        var path = ParserTestHelpers.FixturePath("composer", "composer.json");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.Equal("composer", d.Ecosystem));
        Assert.All(deps, d => Assert.False(d.IsTransitive));

        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("^3.5", byName["monolog/monolog"]);
        Assert.Equal("~6.3", byName["symfony/console"]);
        Assert.Equal("^10.5", byName["phpunit/phpunit"]);
        // Platform requirements are not packages.
        Assert.DoesNotContain("php", byName.Keys);
    }

    [Fact]
    public void Should_ReturnTransitiveDeps_When_ComposerLockHasPackages()
    {
        var parser = ParserTestHelpers.ResolveParser("composer");
        var path = ParserTestHelpers.FixturePath("composer", "composer.lock");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("3.5.0", byName["monolog/monolog"]);
        Assert.Equal("6.3.4", byName["symfony/console"]);
        Assert.Equal("3.0.0", byName["psr/log"]);
        Assert.Equal("10.5.5", byName["phpunit/phpunit"]);
        Assert.All(deps, d => Assert.Equal(!d.IsTransitive, d.Direct));
    }

    [Fact]
    public void Should_PreferLock_When_DirectoryHasManifestAndLock()
    {
        var parser = ParserTestHelpers.ResolveParser("composer");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("composer", "composer.json"), dir, "composer.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("composer", "composer.lock"), dir, "composer.lock");

            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Lock set only: pinned versions; manifest-only ranges gone.
            Assert.Equal("3.5.0", byName["monolog/monolog"]);
            Assert.Equal("3.0.0", byName["psr/log"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_Handle_File_When_CanHandleComposerManifests()
    {
        var parser = ParserTestHelpers.ResolveParser("composer");

        Assert.True(parser.CanHandle("composer.json"));
        Assert.True(parser.CanHandle("/some/dir/composer.lock"));
    }

    [Fact]
    public void Should_NotHandle_File_When_UnrelatedManifest()
    {
        var parser = ParserTestHelpers.ResolveParser("composer");

        Assert.False(parser.CanHandle("package.json"));
        Assert.False(parser.CanHandle("Gemfile"));
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_ComposerJsonMalformed()
    {
        var parser = ParserTestHelpers.ResolveParser("composer");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            var bad = Path.Combine(dir, "composer.json");
            File.WriteAllText(bad, "{ not json");

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
    public void Should_ReturnEmptyWithoutThrow_When_ComposerJsonMissing()
    {
        var parser = ParserTestHelpers.ResolveParser("composer");
        var missing = Path.Combine(ParserTestHelpers.CreateTempDir(), "composer.json");

        var deps = parser.Parse(missing);

        Assert.NotNull(deps);
        Assert.Empty(deps);
    }

    [Fact]
    public void Should_ParseDirectory_When_GivenDirWithComposerJson()
    {
        var parser = ParserTestHelpers.ResolveParser("composer");
        var tempDir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("composer", "composer.json"), "composer.json");
        try
        {
            var deps = parser.Parse(tempDir);

            Assert.NotEmpty(deps);
            Assert.Contains(deps, d => d.Name == "monolog/monolog" && d.Version == "^3.5");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
