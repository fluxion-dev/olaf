namespace Olaf.Tests.Parsers;

/// <summary>
/// Bundler ecosystem: Gemfile direct deps + Gemfile.lock transitive + .gemspec fallback.
/// </summary>
public sealed class BundlerParserTests
{
    [Fact]
    public void Should_ReturnDirectDeps_When_GemfileHasGemLines()
    {
        var parser = ParserTestHelpers.ResolveParser("bundler");
        var path = ParserTestHelpers.FixturePath("bundler", "Gemfile");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.Equal("bundler", d.Ecosystem));
        Assert.All(deps, d => Assert.False(d.IsTransitive));

        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("~> 7.0.0", byName["rails"]);
        Assert.Equal("~> 3.12", byName["rspec"]);
        Assert.Equal("*", byName["puma"]);
    }

    [Fact]
    public void Should_ReturnTransitiveDeps_When_GemfileLockHasGemSpecs()
    {
        var parser = ParserTestHelpers.ResolveParser("bundler");
        var path = ParserTestHelpers.FixturePath("bundler", "Gemfile.lock");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("7.0.8", byName["rails"]);
        Assert.Equal("7.0.8", byName["actionpack"]);
        Assert.Equal("3.12.0", byName["rspec"]);
        Assert.Equal("6.4.2", byName["puma"]);
        Assert.All(deps, d => Assert.Equal(!d.IsTransitive, d.Direct));
    }

    [Fact]
    public void Should_PreferLock_When_DirectoryHasGemfileAndLock()
    {
        var parser = ParserTestHelpers.ResolveParser("bundler");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("bundler", "Gemfile"), dir, "Gemfile");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("bundler", "Gemfile.lock"), dir, "Gemfile.lock");

            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Lock set only: pinned versions + transitive actionpack.
            Assert.Equal("7.0.8", byName["rails"]);
            Assert.Equal("7.0.8", byName["actionpack"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ParseGemspec_When_GemspecFileGiven()
    {
        var parser = ParserTestHelpers.ResolveParser("bundler");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            var gemspec = Path.Combine(dir, "olaf-fixture.gemspec");
            File.WriteAllText(gemspec, """
                Gem::Specification.new do |s|
                  s.name = "olaf-fixture"
                  s.add_dependency "thor", "~> 1.3"
                  s.add_development_dependency "rake", "~> 13.0"
                end
                """);

            var deps = parser.Parse(gemspec);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("~> 1.3", byName["thor"]);
            Assert.Equal("~> 13.0", byName["rake"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_Handle_File_When_CanHandleBundlerManifests()
    {
        var parser = ParserTestHelpers.ResolveParser("bundler");

        Assert.True(parser.CanHandle("Gemfile"));
        Assert.True(parser.CanHandle("/some/dir/Gemfile.lock"));
        Assert.True(parser.CanHandle("olaf-fixture.gemspec"));
        Assert.True(parser.CanHandle("/some/dir/olaf-fixture.GEMSPEC"));
    }

    [Fact]
    public void Should_NotHandle_File_When_UnrelatedManifest()
    {
        var parser = ParserTestHelpers.ResolveParser("bundler");

        Assert.False(parser.CanHandle("package.json"));
        Assert.False(parser.CanHandle("composer.json"));
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_GemfileMissing()
    {
        var parser = ParserTestHelpers.ResolveParser("bundler");
        var missing = Path.Combine(ParserTestHelpers.CreateTempDir(), "Gemfile");

        var deps = parser.Parse(missing);

        Assert.NotNull(deps);
        Assert.Empty(deps);
    }

    [Fact]
    public void Should_ParseDirectory_When_GivenDirWithGemfile()
    {
        var parser = ParserTestHelpers.ResolveParser("bundler");
        var tempDir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("bundler", "Gemfile"), "Gemfile");
        try
        {
            var deps = parser.Parse(tempDir);

            Assert.NotEmpty(deps);
            Assert.Contains(deps, d => d.Name == "rails" && d.Version == "~> 7.0.0");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
