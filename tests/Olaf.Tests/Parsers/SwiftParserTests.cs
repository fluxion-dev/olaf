using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Swift SPM parser (issue #14): Package.swift .package(url:from:/exact:)
/// + Package.resolved pins preference (v1 object.pins, v2/v3 top-level pins).
/// </summary>
public sealed class SwiftParserTests
{
    [Fact]
    public void Should_HandleSwiftFiles_When_CanHandleChecked()
    {
        var parser = ParserTestHelpers.ResolveParser("swift");

        Assert.Equal("swift", parser.Ecosystem);
        Assert.True(parser.CanHandle("Package.swift"));
        Assert.True(parser.CanHandle("Package.resolved"));
        Assert.False(parser.CanHandle("Podfile"));
        Assert.False(parser.CanHandle("package.json"));
    }

    [Fact]
    public void Should_ParseManifest_When_PackageSwiftOnly()
    {
        var dir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("swift", "Package.swift"), "Package.swift");
        try
        {
            var parser = ParserTestHelpers.ResolveParser("swift");
            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("5.8.0", byName["Alamofire/Alamofire"]);
            Assert.Equal("5.6.0", byName["SnapKit/SnapKit"]);
            Assert.Equal("1.2.0", byName["apple/swift-argument-parser"]);
            Assert.All(deps, d => Assert.False(d.IsTransitive));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ParseResolvedV2_When_PackageResolvedOnly()
    {
        var dir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("swift", "Package.resolved"), "Package.resolved");
        try
        {
            var parser = ParserTestHelpers.ResolveParser("swift");
            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("5.8.1", byName["Alamofire/Alamofire"]);
            Assert.Equal("5.6.0", byName["SnapKit/SnapKit"]);
            Assert.Equal("1.0.5", byName["apple/swift-collections"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ParseResolvedV1_When_PinsNestedUnderObject()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "Package.resolved"),
                """{"object":{"pins":[{"package":"Alamofire","repositoryURL":"https://github.com/Alamofire/Alamofire.git","state":{"branch":null,"revision":"abc123","version":"5.8.1"}}]},"version":1}""");

            var parser = ParserTestHelpers.ResolveParser("swift");
            var deps = parser.Parse(dir);

            Assert.Contains(deps, d => d.Name == "Alamofire/Alamofire" && d.Version == "5.8.1");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_FallBackToRevision_When_StateHasNoVersion()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "Package.resolved"),
                """{"pins":[{"identity":"some-lib","location":"https://example.com/org/some-lib.git","state":{"branch":"main","revision":"deadbeef"}}],"version":3}""");

            var parser = ParserTestHelpers.ResolveParser("swift");
            var deps = parser.Parse(dir);

            Assert.Contains(deps, d => d.Name == "some-lib" && d.Version == "deadbeef");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ReturnEmpty_When_ResolvedMalformed()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Package.resolved"), "{not-json");

            var parser = ParserTestHelpers.ResolveParser("swift");
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
    public void Should_ParseMultilineAndSshUrls_When_ManifestHasVariants()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "Package.swift"),
                "import PackageDescription\n\nlet package = Package(\n"
                + "    name: \"Variants\",\n"
                + "    dependencies: [\n"
                + "        .package(url: \"https://github.com/Alamofire/Alamofire.git\",\n"
                + "                 from: \"5.8.0\"),\n"
                + "        .package(url: \"git@github.com:owner/repo.git\", exact: \"1.0.0\"),\n"
                + "    ]\n)\n");

            var parser = ParserTestHelpers.ResolveParser("swift");
            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("5.8.0", byName["Alamofire/Alamofire"]);
            Assert.Equal("1.0.0", byName["owner/repo"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
