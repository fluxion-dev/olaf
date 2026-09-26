namespace Olaf.Tests.Parsers;

/// <summary>
/// Conan ecosystem: conanfile.txt [requires] + conanfile.py requires= +
/// conan.lock preference (graph_lock nodes, consumer skipped).
/// </summary>
public sealed class ConanParserTests
{
    [Fact]
    public void Should_HandleConanFiles_When_CanHandleChecked()
    {
        var parser = ParserTestHelpers.ResolveParser("conan");

        Assert.Equal("conan", parser.Ecosystem);
        Assert.True(parser.CanHandle("conanfile.txt"));
        Assert.True(parser.CanHandle("conanfile.py"));
        Assert.True(parser.CanHandle("conan.lock"));
        Assert.False(parser.CanHandle("vcpkg.json"));
        Assert.False(parser.CanHandle("package.json"));
    }

    [Fact]
    public void Should_ParseRequires_When_ConanfileTxtFixture()
    {
        var parser = ParserTestHelpers.ResolveParser("conan");
        var path = ParserTestHelpers.FixturePath("conan", "conanfile.txt");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.Equal("conan", d.Ecosystem));
        Assert.All(deps, d => Assert.False(d.IsTransitive));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("11.0.2", byName["fmt"]);
        Assert.Equal("1.3.1", byName["zlib"]);
        Assert.Equal("3.11.3", byName["nlohmann_json"]);
    }

    [Fact]
    public void Should_IgnoreOtherSections_When_ConanfileTxtHasGenerators()
    {
        var parser = ParserTestHelpers.ResolveParser("conan");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "conanfile.txt"),
                "[requires]\nfmt/11.0.2\n\n[generators]\nCMakeDeps\n");

            var deps = parser.Parse(dir);

            Assert.Single(deps);
            Assert.Equal("fmt", deps[0].Name);
            Assert.DoesNotContain(deps, d => d.Name == "CMakeDeps");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ParseRequiresAssign_When_ConanfilePyFixture()
    {
        var parser = ParserTestHelpers.ResolveParser("conan");
        var path = ParserTestHelpers.FixturePath("conan", "conanfile.py");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("11.0.2", byName["fmt"]);
        Assert.Equal("1.3.1", byName["zlib"]);
    }

    [Fact]
    public void Should_ParseRefs_When_ConanLockFixture()
    {
        var parser = ParserTestHelpers.ResolveParser("conan");
        var path = ParserTestHelpers.FixturePath("conan", "conan.lock");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("11.0.2", byName["fmt"]);
        Assert.Equal("1.3.1", byName["zlib"]);
        Assert.Equal("3.11.3", byName["nlohmann_json"]);
        Assert.DoesNotContain("olaf-fixture", byName.Keys);
        Assert.All(deps, d => Assert.True(d.IsTransitive));
    }

    [Fact]
    public void Should_PreferLock_When_DirectoryHasTxtAndLock()
    {
        var parser = ParserTestHelpers.ResolveParser("conan");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("conan", "conanfile.txt"), dir, "conanfile.txt");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("conan", "conan.lock"), dir, "conan.lock");

            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Lock set only: nlohmann_json is lock-exclusive.
            Assert.Equal("3.11.3", byName["nlohmann_json"]);
            Assert.All(deps, d => Assert.True(d.IsTransitive));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_ConanLockMalformed()
    {
        var parser = ParserTestHelpers.ResolveParser("conan");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "conan.lock"), "{ not json");

            var deps = parser.Parse(dir);

            Assert.NotNull(deps);
            Assert.Empty(deps);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
