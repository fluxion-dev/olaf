namespace Olaf.Tests.Parsers;

/// <summary>
/// Red tests for NuGet: .csproj, packages.lock.json, packages.config.
/// Expects NuGetParser : IEcosystemParser with Ecosystem == "nuget".
/// </summary>
public sealed class NuGetParserTests
{
    [Fact]
    public void Should_ReturnDeps_When_CsprojHasPackageReference()
    {
        var parser = ParserTestHelpers.ResolveParser("nuget");
        var path = ParserTestHelpers.FixturePath("nuget", "sample.csproj");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.Equal("nuget", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("13.0.3", byName["Newtonsoft.Json"]);
        Assert.Equal("3.1.1", byName["Serilog"]);
        Assert.All(deps, d =>
        {
            Assert.False(d.IsTransitive);
            Assert.True(d.Direct);
        });
    }

    [Fact]
    public void Should_UseWildcard_When_VersionFloatingOrMissing()
    {
        var parser = ParserTestHelpers.ResolveParser("nuget");
        var path = ParserTestHelpers.FixturePath("nuget", "floating.csproj");

        var deps = parser.Parse(path);

        Assert.Equal(2, deps.Count);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("*", byName["FloatingPkg"]);
        Assert.Equal("*", byName["NoVersionPkg"]);
    }

    [Fact]
    public void Should_ReturnTransitive_When_PackagesLockJson()
    {
        var parser = ParserTestHelpers.ResolveParser("nuget");
        var path = ParserTestHelpers.FixturePath("nuget", "packages.lock.json");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        var byName = deps.ToDictionary(d => d.Name);
        Assert.Equal("13.0.3", byName["Newtonsoft.Json"].Version);
        Assert.Equal("8.0.4", byName["System.Text.Json"].Version);
        // Direct vs transitive split from requested/type metadata.
        Assert.False(byName["Newtonsoft.Json"].IsTransitive);
        Assert.True(byName["Newtonsoft.Json"].Direct);
        Assert.True(byName["System.Text.Json"].IsTransitive);
        Assert.False(byName["System.Text.Json"].Direct);
    }

    [Fact]
    public void Should_ReturnDeps_When_PackagesConfigLegacy()
    {
        var parser = ParserTestHelpers.ResolveParser("nuget");
        var path = ParserTestHelpers.FixturePath("nuget", "packages.config");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("13.0.3", byName["Newtonsoft.Json"]);
        Assert.Equal("3.13.2", byName["NUnit"]);
    }

    [Fact]
    public void Should_Handle_File_When_CanHandleCsproj()
    {
        var parser = ParserTestHelpers.ResolveParser("nuget");

        Assert.True(parser.CanHandle("sample.csproj"));
        Assert.True(parser.CanHandle("/some/dir/My.App.csproj"));
    }

    [Fact]
    public void Should_Handle_File_When_CanHandleLockAndConfig()
    {
        var parser = ParserTestHelpers.ResolveParser("nuget");

        Assert.True(parser.CanHandle("packages.lock.json"));
        Assert.True(parser.CanHandle("packages.config"));
    }

    [Fact]
    public void Should_NotHandle_File_When_NpmManifest()
    {
        var parser = ParserTestHelpers.ResolveParser("nuget");

        Assert.False(parser.CanHandle("package.json"));
        Assert.False(parser.CanHandle("requirements.txt"));
    }

    [Fact]
    public void Should_ParseDirectory_When_GivenDirWithCsproj()
    {
        var parser = ParserTestHelpers.ResolveParser("nuget");
        var tempDir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("nuget", "sample.csproj"), "sample.csproj");
        try
        {
            var deps = parser.Parse(tempDir);

            Assert.NotEmpty(deps);
            Assert.Contains(deps, d => d.Name == "Newtonsoft.Json");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
