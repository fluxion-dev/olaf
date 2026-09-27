using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Coverage for the thin Alpine apk parser (issue #64):
/// <c>installed</c> text stanzas (<c>P:</c>/<c>V:</c>), malformed input yields
/// <c>[]</c>, standalone registry scan by file name.
/// </summary>
public sealed class ApkParserTests
{
    [Fact]
    public void Should_ParseNameAndVersion_When_InstalledFile()
    {
        var parser = ParserTestHelpers.ResolveParser("apk");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "installed",
            ImageFixtureBuilder.ApkInstalled(("musl", "1.2.5-r0"), ("zlib", "1.3.1-r0")));

        var deps = parser.Parse(path);

        Assert.Equal(2, deps.Count);
        Assert.All(deps, d =>
        {
            Assert.Equal("apk", d.Ecosystem);
            Assert.False(d.IsTransitive);
            Assert.True(d.Direct);
        });
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("1.2.5-r0", byName["musl"]);
        Assert.Equal("1.3.1-r0", byName["zlib"]);
    }

    [Fact]
    public void Should_ReturnEmpty_When_MalformedInstalled()
    {
        var parser = ParserTestHelpers.ResolveParser("apk");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "installed",
            "this is not an apk database\nno colons here\nKeyWithoutValue\n");

        var deps = parser.Parse(path);

        Assert.Empty(deps);
    }

    [Fact]
    public void Should_SkipStanzas_When_MissingNameOrVersion()
    {
        var parser = ParserTestHelpers.ResolveParser("apk");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "installed",
            "P:nameless\nA:x86_64\n\nV:9.9.9\nA:x86_64\n\n" + ImageFixtureBuilder.ApkInstalled(("kept", "1.0-r0")));

        var deps = parser.Parse(path);

        var single = Assert.Single(deps);
        Assert.Equal("kept", single.Name);
        Assert.Equal("1.0-r0", single.Version);
    }

    [Fact]
    public void Should_ScanStandalone_When_RegistryScansInstalledFile()
    {
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "installed",
            ImageFixtureBuilder.ApkInstalled(("busybox", "1.36.1-r0")));

        var deps = new ParserRegistry().Scan(path);

        Assert.Contains(deps, d => d.Ecosystem == "apk" && d.Name == "busybox" && d.Version == "1.36.1-r0");
    }
}
