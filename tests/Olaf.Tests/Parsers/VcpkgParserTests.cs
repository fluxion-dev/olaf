namespace Olaf.Tests.Parsers;

/// <summary>
/// Vcpkg ecosystem: vcpkg.json dependencies (string + {name,version} forms).
/// vcpkg-configuration.json is registry config — never a manifest.
/// </summary>
public sealed class VcpkgParserTests
{
    [Fact]
    public void Should_HandleVcpkgJson_When_CanHandleChecked()
    {
        var parser = ParserTestHelpers.ResolveParser("vcpkg");

        Assert.Equal("vcpkg", parser.Ecosystem);
        Assert.True(parser.CanHandle("vcpkg.json"));
        Assert.True(parser.CanHandle("/some/dir/vcpkg.json"));
        Assert.False(parser.CanHandle("vcpkg-configuration.json"));
        Assert.False(parser.CanHandle("package.json"));
    }

    [Fact]
    public void Should_ParseMixedForms_When_VcpkgJsonFixture()
    {
        var parser = ParserTestHelpers.ResolveParser("vcpkg");
        var path = ParserTestHelpers.FixturePath("vcpkg", "vcpkg.json");

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.Equal("vcpkg", d.Ecosystem));
        Assert.All(deps, d => Assert.False(d.IsTransitive));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("*", byName["fmt"]);
        Assert.Equal("1.3.1", byName["zlib"]);
        Assert.Equal("3.11.3", byName["nlohmann-json"]);
    }

    [Fact]
    public void Should_ParseVersionFields_When_ObjectFormsVary()
    {
        var parser = ParserTestHelpers.ResolveParser("vcpkg");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "vcpkg.json"),
                """{"name":"x","version":"1.0.0","dependencies":["a",{"name":"b","version>=":"2.0"},{"name":"c","version-string":"3.0#1"},{"name":"d"}]}""");

            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("*", byName["a"]);
            Assert.Equal("2.0", byName["b"]);
            Assert.Equal("3.0#1", byName["c"]);
            Assert.Equal("*", byName["d"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_VcpkgJsonMalformed()
    {
        var parser = ParserTestHelpers.ResolveParser("vcpkg");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "vcpkg.json"), "{ not json");

            var deps = parser.Parse(dir);

            Assert.NotNull(deps);
            Assert.Empty(deps);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ReturnEmptyWithoutThrow_When_VcpkgJsonMissing()
    {
        var parser = ParserTestHelpers.ResolveParser("vcpkg");
        var missing = Path.Combine(ParserTestHelpers.CreateTempDir(), "vcpkg.json");

        var deps = parser.Parse(missing);

        Assert.NotNull(deps);
        Assert.Empty(deps);
    }
}
