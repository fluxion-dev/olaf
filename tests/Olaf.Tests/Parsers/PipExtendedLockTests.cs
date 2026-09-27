using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Extended Python manifests (issue #16): uv.lock [[package]] + environment.yml.
/// Ecosystem stays "pip"; uv.lock is a lock (transitive), environment.yml a direct manifest.
/// Preference: lock &gt; requirements &gt; pyproject &gt; environment.
/// </summary>
public sealed class PipExtendedLockTests
{
    [Fact]
    public void Should_ReturnTransitive_When_UvLock()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "uv.lock");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.Equal("pip", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("2.31.0", byName["requests"]);
        Assert.Equal("2.2.1", byName["urllib3"]);
        Assert.Equal("2024.2.2", byName["certifi"]);
        Assert.All(deps, d => Assert.Equal(!d.IsTransitive, d.Direct));
    }

    [Fact]
    public void Should_ReturnDirectDeps_When_EnvironmentYml()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");
        var path = ParserTestHelpers.FixturePath("pip", "environment.yml");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.Equal("pip", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        // Conda single '=' pins; bare name is wildcard; nested pip: uses pip spec.
        Assert.Equal("2.31.0", byName["requests"]);
        Assert.Equal("*", byName["flask"]);
        Assert.Equal("13.7.0", byName["rich"]);
        Assert.Equal(">=0.20", byName["uvicorn"]);
        // Interpreter constraint and pip section header are not dependencies.
        Assert.DoesNotContain("python", byName.Keys);
        Assert.DoesNotContain("pip", byName.Keys);
        Assert.All(deps, d =>
        {
            Assert.False(d.IsTransitive);
            Assert.True(d.Direct);
        });
    }

    [Fact]
    public void Should_Handle_File_When_CanHandleExtendedManifests()
    {
        var parser = ParserTestHelpers.ResolveParser("pip");

        Assert.True(parser.CanHandle("uv.lock"));
        Assert.True(parser.CanHandle("environment.yml"));
        Assert.True(parser.CanHandle("environment.yaml"));
    }

    [Fact]
    public void Should_PreferLock_When_UvLockAndRequirementsCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "requirements.txt"), dir, "requirements.txt");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "uv.lock"), dir, "uv.lock");

            var deps = ParserTestHelpers.ResolveParser("pip").Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Lock-only dep present; requirements-only deps absent.
            Assert.Equal("2024.2.2", byName["certifi"]);
            Assert.DoesNotContain("flask", byName.Keys);
            Assert.DoesNotContain("dataclasses", byName.Keys);
            Assert.All(deps, d => Assert.True(d.IsTransitive));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferRequirements_When_RequirementsAndEnvironmentCoexistViaRegistry()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "requirements.txt"), dir, "requirements.txt");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "environment.yml"), dir, "environment.yml");

            var deps = new ParserRegistry().Scan(dir);
            var pip = deps.Where(d => d.Ecosystem == "pip").ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("3.0.2", pip["flask"]);
            Assert.DoesNotContain("uvicorn", pip.Keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferPyproject_When_PyprojectAndEnvironmentCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "pyproject.toml"), dir, "pyproject.toml");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "environment.yml"), dir, "environment.yml");

            var deps = ParserTestHelpers.ResolveParser("pip").Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Contains(deps, d => d.Name == "rich" && d.Version == "13.7.0");
            Assert.DoesNotContain("uvicorn", byName.Keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferPipfileLock_When_PipfileLockPoetryLockAndRequirementsCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "Pipfile.lock"), dir, "Pipfile.lock");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "poetry.lock"), dir, "poetry.lock");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "requirements.txt"), dir, "requirements.txt");

            var deps = ParserTestHelpers.ResolveParser("pip").Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Authoritative lock wins outright: 7 Pipfile.lock deps, no double-count.
            Assert.Equal(7, deps.Count);
            Assert.Equal("2.31.0", byName["requests"]);
            Assert.Equal("7.4.0", byName["pytest"]);
            Assert.Equal("*", byName["my-git-dep"]);
            // poetry.lock-exclusive dep absent; requirements-only dep absent.
            Assert.DoesNotContain("certifi", byName.Keys);
            Assert.DoesNotContain("flask", byName.Keys);
            Assert.All(deps, d =>
            {
                Assert.False(d.IsTransitive);
                Assert.True(d.Direct);
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
