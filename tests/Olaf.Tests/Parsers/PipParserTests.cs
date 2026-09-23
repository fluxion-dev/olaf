namespace Olaf.Tests.Parsers;

/// <summary>
/// Red tests for pip: requirements.txt, pyproject.toml, poetry.lock.
/// Expects PipParser : IEcosystemParser with Ecosystem == "pip".
/// </summary>
public sealed class PipParserTests
{
    [Fact]
    public void Should_ReturnPinnedDeps_When_RequirementsTxt()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "requirements.txt");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.Equal("pip", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        // Pinned == preserved, comments/-r/env-markers stripped.
        Assert.Equal("2.31.0", byName["requests"]);
        Assert.Equal("3.0.2", byName["flask"]);
        Assert.Equal("2.2.1", byName["urllib3"]);
        Assert.DoesNotContain("other-requirements.txt", byName.Keys);
        Assert.All(deps, d =>
        {
            Assert.DoesNotContain(";", d.Version);
            Assert.DoesNotContain("#", d.Name);
            Assert.False(d.IsTransitive);
            Assert.True(d.Direct);
        });
    }

    [Fact]
    public void Should_ReturnDeps_When_PyprojectProjectDependencies()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "pyproject.toml");

        var deps = parser.Parse(path);

        Assert.Contains(deps, d => d.Name == "requests" && d.Version == "2.31.0");
        Assert.Contains(deps, d => d.Name == "flask" && d.Version == ">=3.0.0");
    }

    [Fact]
    public void Should_ReturnDeps_When_PyprojectPoetrySection()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "pyproject.toml");

        var deps = parser.Parse(path);

        // Poetry section: python constraint itself is not a dependency.
        Assert.DoesNotContain(deps, d => d.Name == "python");
        Assert.Contains(deps, d => d.Name == "rich" && d.Version == "13.7.0");
    }

    [Fact]
    public void Should_ReturnTransitive_When_PoetryLock()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "poetry.lock");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("2.31.0", byName["requests"]);
        Assert.Equal("2.2.1", byName["urllib3"]);
        Assert.Equal("2024.2.2", byName["certifi"]);
        Assert.All(deps, d => Assert.Equal(!d.IsTransitive, d.Direct));
    }

    [Fact]
    public void Should_Handle_File_When_CanHandlePipManifests()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");

        Assert.True(parser.CanHandle("requirements.txt"));
        Assert.True(parser.CanHandle("pyproject.toml"));
        Assert.True(parser.CanHandle("poetry.lock"));
    }

    [Fact]
    public void Should_NotHandle_File_When_NpmManifest()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");

        Assert.False(parser.CanHandle("package.json"));
        Assert.False(parser.CanHandle("sample.csproj"));
    }

    [Fact]
    public void Should_ParseDirectory_When_GivenDirWithRequirements()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var tempDir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("pip", "requirements.txt"), "requirements.txt");
        try
        {
            var deps = parser.Parse(tempDir);

            Assert.NotEmpty(deps);
            Assert.Contains(deps, d => d.Name == "requests");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
