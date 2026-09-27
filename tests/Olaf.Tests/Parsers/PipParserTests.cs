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

    [Fact]
    public void Should_Handle_File_When_CanHandlePipfileLock()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");

        Assert.True(parser.CanHandle("Pipfile.lock"));
        Assert.True(parser.CanHandle("PIPFILE.LOCK"));
        Assert.True(parser.CanHandle("/some/dir/Pipfile.lock"));
        Assert.False(parser.CanHandle("Pipfile"));
    }

    [Fact]
    public void Should_StripVersionMarker_When_PipfileLockHasVersionedDeps()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "Pipfile.lock");

        var deps = parser.Parse(path);

        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("2.31.0", byName["requests"]);
        Assert.Equal("7.4.0", byName["pytest"]);
        Assert.All(deps, d =>
        {
            Assert.False(d.IsTransitive);
            Assert.True(d.Direct);
        });
    }

    [Fact]
    public void Should_IgnoreExtrasMarkersHashes_When_PipfileLockVersioned()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "Pipfile.lock");

        var deps = parser.Parse(path);

        var requests = deps.Single(d => d.Name == "requests");
        // extras (security), markers (python_version) and hashes must not leak into version.
        Assert.Equal("2.31.0", requests.Version);
        Assert.DoesNotContain("security", requests.Version);
        Assert.DoesNotContain("markers", requests.Version);
        Assert.DoesNotContain("sha256", requests.Version);
        Assert.DoesNotContain("==", requests.Version);
    }

    [Fact]
    public void Should_ReturnWildcard_When_PipfileLockHasGitRef()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "Pipfile.lock");

        var deps = parser.Parse(path);

        var git = deps.Single(d => d.Name == "my-git-dep");
        Assert.Equal("*", git.Version);
        Assert.False(git.IsTransitive);
        Assert.True(git.Direct);
    }

    [Fact]
    public void Should_ReturnWildcard_When_PipfileLockHasPathFileEditable()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "Pipfile.lock");

        var byName = parser.Parse(path).ToDictionary(d => d.Name, d => d.Version);

        Assert.Equal("*", byName["my-local"]);
        Assert.Equal("*", byName["my-file"]);
        Assert.Equal("*", byName["my-editable"]);
    }

    [Fact]
    public void Should_IncludeDevelopOnly_When_PipfileLockHasDevelopSection()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "Pipfile.lock");

        var deps = parser.Parse(path);

        Assert.Contains(deps, d => d.Name == "pytest" && d.Version == "7.4.0");
    }

    [Fact]
    public void Should_PreferDefault_When_PipfileLockDefaultDevelopCollide()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "Pipfile.lock");

        var byName = parser.Parse(path).ToDictionary(d => d.Name, d => d.Version);

        // shared-pkg exists in both sections: default (1.0.0) wins over develop (2.0.0).
        Assert.Equal("1.0.0", byName["shared-pkg"]);
    }

    [Fact]
    public void Should_ReturnSingleFileSet_When_PipfileLockParsed()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "Pipfile.lock");

        var deps = parser.Parse(path);

        Assert.Equal(7, deps.Count);
        Assert.DoesNotContain(deps, d => d.Name == "_meta");
        Assert.All(deps, d => Assert.Equal("pip", d.Ecosystem));
        Assert.All(deps, d =>
        {
            Assert.False(d.IsTransitive);
            Assert.True(d.Direct);
        });
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_PipfileLockMalformed()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            var bad = Path.Combine(dir, "Pipfile.lock");
            File.WriteAllText(bad, "{ not valid json !!!");

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
    public void Should_ReturnEmpty_When_PipfileLockMissingOrEmptySections()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            var metaOnly = Path.Combine(dir, "Pipfile.lock");
            File.WriteAllText(metaOnly, """{"_meta": {"pipfile-spec": 6}}""");

            Assert.Empty(parser.Parse(metaOnly));

            File.WriteAllText(metaOnly, """{"default": {}, "develop": {}}""");

            Assert.Empty(parser.Parse(metaOnly));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
