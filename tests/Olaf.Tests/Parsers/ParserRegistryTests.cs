using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Green-phase coverage for ParserRegistry (issue #1):
/// recursive scan, pypi→pip alias, lock-over-manifest, malformed/empty dirs, dedup+sort.
/// </summary>
public sealed class ParserRegistryTests
{
    [Fact]
    public void Should_FindAllEcosystems_When_NestedMonorepoDirs()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package.json"),
                Path.Combine(root, "a"), "package.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("nuget", "sample.csproj"),
                Path.Combine(root, "b", "nested"), "sample.csproj");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "requirements.txt"),
                Path.Combine(root, "c", "deep"), "requirements.txt");

            var deps = new ParserRegistry().Scan(root);

            Assert.Contains(deps, d => d.Ecosystem == "npm" && d.Name == "express");
            Assert.Contains(deps, d => d.Ecosystem == "nuget" && d.Name == "Newtonsoft.Json");
            Assert.Contains(deps, d => d.Ecosystem == "pip" && d.Name == "requests");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Should_TreatPypiAsPipAlias_When_EcosystemFilterGiven()
    {
        var dir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("pip", "requirements.txt"), "requirements.txt");
        try
        {
            var registry = new ParserRegistry();
            var viaPypi = registry.Scan(dir, "pypi");
            var viaPip = registry.Scan(dir, "pip");
            var viaUpper = registry.Scan(dir, "PIP");

            Assert.NotEmpty(viaPip);
            Assert.Equal(
                viaPip.Select(d => (d.Ecosystem, d.Name, d.Version)),
                viaPypi.Select(d => (d.Ecosystem, d.Name, d.Version)));
            Assert.Equal(
                viaPip.Select(d => (d.Ecosystem, d.Name, d.Version)),
                viaUpper.Select(d => (d.Ecosystem, d.Name, d.Version)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferLock_When_NpmManifestAndLockCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package.json"), dir, "package.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package-lock.json"), dir, "package-lock.json");

            var deps = new ParserRegistry().Scan(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Lock set only: pinned versions, no manifest-only ranges.
            Assert.Equal("4.18.2", byName["express"]);
            Assert.Equal("4.3.4", byName["debug"]);
            Assert.DoesNotContain("lodash", byName.Keys);
            Assert.DoesNotContain("react", byName.Keys);
            Assert.DoesNotContain("vitest", byName.Keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferLock_When_NuGetCsprojAndLockCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("nuget", "sample.csproj"), dir, "sample.csproj");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("nuget", "packages.lock.json"), dir, "packages.lock.json");

            var deps = new ParserRegistry().Scan(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Lock set: transitive System.Text.Json wins over manifest-only Serilog.
            Assert.Equal("8.0.4", byName["System.Text.Json"]);
            Assert.DoesNotContain("Serilog", byName.Keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferLock_When_PipRequirementsPyprojectAndLockCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "requirements.txt"), dir, "requirements.txt");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "pyproject.toml"), dir, "pyproject.toml");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "poetry.lock"), dir, "poetry.lock");

            var deps = new ParserRegistry().Scan(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Lock set only: certifi is lock-exclusive; flask/rich are manifest-only.
            Assert.Equal("2024.2.2", byName["certifi"]);
            Assert.DoesNotContain("flask", byName.Keys);
            Assert.DoesNotContain("rich", byName.Keys);
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

            var deps = new ParserRegistry().Scan(dir);
            var pip = deps.Where(d => d.Ecosystem == "pip").ToDictionary(d => d.Name, d => d.Version);

            // Triple-lock: Pipfile.lock wins outright, no double-count (7 deps).
            Assert.Equal(7, pip.Count);
            Assert.Equal("2.31.0", pip["requests"]);
            Assert.Equal("7.4.0", pip["pytest"]);
            Assert.Equal("1.0.0", pip["shared-pkg"]);
            Assert.DoesNotContain("certifi", pip.Keys);
            Assert.DoesNotContain("flask", pip.Keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_RegistryScansMalformedManifest()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "malformed-package.json"), dir, "package.json");

            var deps = new ParserRegistry().Scan(dir);

            Assert.NotNull(deps);
            Assert.Empty(deps);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_Throw_When_RegistryScansDirWithZeroManifests()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "README.md"), "# empty");

            var ex = Assert.Throws<InvalidOperationException>(() => new ParserRegistry().Scan(dir));
            Assert.Contains("No manifests", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_DedupAndSort_When_SameManifestCopiedInTwoSubdirs()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package.json"),
                Path.Combine(root, "svc-a"), "package.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package.json"),
                Path.Combine(root, "svc-b"), "package.json");

            var deps = new ParserRegistry().Scan(root);

            Assert.Single(deps, d => d.Ecosystem == "npm" && d.Name == "express");
            Assert.Equal(
                deps.Select(d => (d.Ecosystem, d.Name, d.Version)),
                deps
                    .OrderBy(d => d.Ecosystem, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Version, StringComparer.Ordinal)
                    .Select(d => (d.Ecosystem, d.Name, d.Version)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>
/// Issue #66: registry dedup direct-wins. On a same-triple
/// (Ecosystem, Name, Version) collision the IsTransitive:false survivor is
/// kept regardless of input order; canonical sort is preserved.
/// DeduplicateAndSort is exercised via reflection (private unit of the fix —
/// a Scan-level test cannot control filesystem enumeration order, so it
/// could not prove order-independence). Fully offline, no fixtures.
/// </summary>
public sealed class ParserRegistryDirectWinsTests
{
    private static IReadOnlyList<Olaf.Core.Dependency> Dedupe(List<Olaf.Core.Dependency> deps)
    {
        var method = typeof(ParserRegistry).GetMethod(
            "DeduplicateAndSort",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (IReadOnlyList<Olaf.Core.Dependency>)method.Invoke(null, new object[] { deps })!;
    }

    [Fact]
    public void Should_PreferDirect_When_SameTripleCollidesDirectFirst()
    {
        var deps = new List<Olaf.Core.Dependency>
        {
            new("npm", "express", "4.18.2", IsTransitive: false),
            new("npm", "express", "4.18.2", IsTransitive: true),
        };

        var result = Dedupe(deps);

        var single = Assert.Single(result);
        Assert.Equal("express", single.Name);
        Assert.False(single.IsTransitive);
        Assert.True(single.Direct);
    }

    [Fact]
    public void Should_PreferDirectRegardlessOfOrder_When_SameTripleCollidesTransitiveFirst()
    {
        var deps = new List<Olaf.Core.Dependency>
        {
            new("npm", "express", "4.18.2", IsTransitive: true),
            new("npm", "debug", "4.3.4", IsTransitive: true),
            new("npm", "express", "4.18.2", IsTransitive: false),
        };

        var result = Dedupe(deps);

        Assert.Equal(2, result.Count);
        var express = Assert.Single(result, d => d.Name == "express");
        Assert.False(express.IsTransitive);
        Assert.True(express.Direct);
        // Canonical sort preserved: debug < express.
        Assert.Equal("debug", result[0].Name);
        Assert.Equal("express", result[1].Name);
    }
}
