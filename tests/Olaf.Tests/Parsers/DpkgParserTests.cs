using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Coverage for the thin Debian dpkg parser (issue #64):
/// <c>status</c> text stanzas (<c>Package:</c>/<c>Version:</c>), malformed input
/// yields <c>[]</c>, standalone registry scan by file name.
/// </summary>
public sealed class DpkgParserTests
{
    [Fact]
    public void Should_ParseNameAndVersion_When_StatusFile()
    {
        var parser = ParserTestHelpers.ResolveParser("dpkg");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "status",
            ImageFixtureBuilder.DpkgStatus(("bash", "5.2-5"), ("coreutils", "9.4-1")));

        var deps = parser.Parse(path);

        Assert.Equal(2, deps.Count);
        Assert.All(deps, d =>
        {
            Assert.Equal("dpkg", d.Ecosystem);
            Assert.False(d.IsTransitive);
            Assert.True(d.Direct);
        });
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("5.2-5", byName["bash"]);
        Assert.Equal("9.4-1", byName["coreutils"]);
    }

    [Fact]
    public void Should_ReturnEmpty_When_MalformedStatus()
    {
        var parser = ParserTestHelpers.ResolveParser("dpkg");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "status",
            "random text\nNo-Colon-Lines\nJust a description here\n");

        var deps = parser.Parse(path);

        Assert.Empty(deps);
    }

    [Fact]
    public void Should_SkipStanzas_When_MissingNameOrVersion()
    {
        var parser = ParserTestHelpers.ResolveParser("dpkg");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "status",
            "Package: noversion\nStatus: install ok installed\n\nVersion: 9.9.9\nStatus: install ok installed\n\n"
                + ImageFixtureBuilder.DpkgStatus(("kept", "2.0-1")));

        var deps = parser.Parse(path);

        var single = Assert.Single(deps);
        Assert.Equal("kept", single.Name);
        Assert.Equal("2.0-1", single.Version);
    }

    [Fact]
    public void Should_ScanStandalone_When_RegistryScansStatusFile()
    {
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "status",
            ImageFixtureBuilder.DpkgStatus(("grep", "3.11-1")));

        var deps = new ParserRegistry().Scan(path);

        Assert.Contains(deps, d => d.Ecosystem == "dpkg" && d.Name == "grep" && d.Version == "3.11-1");
    }

    [Fact]
    public void Should_KeepEpochVerbatim_When_ScanningDpkgFixture()
    {
        var deps = new ParserRegistry().Scan(ParserTestHelpers.FixturePath("dpkg", "status"));

        Assert.Equal(2, deps.Count);
        Assert.All(deps, d => Assert.Equal("dpkg", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("2:5.2-5", byName["bash"]);
        Assert.Equal("9.4-1", byName["coreutils"]);
    }

    [Fact]
    public void Should_IgnoreFoldedLines_When_DescriptionContinuationsLookLikeKeys()
    {
        // Folded Description: bodies (leading space/tab) must NOT false-match
        // Package:/Version:.
        var parser = ParserTestHelpers.ResolveParser("dpkg");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "status",
            "Package: folded\nStatus: install ok installed\nVersion: 1.0-1\nDescription: long\n Package: fake\n Version: 9.9-9\n\tArchitecture: fake-arch\n\n");

        var deps = parser.Parse(path);

        var single = Assert.Single(deps);
        Assert.Equal("folded", single.Name);
        Assert.Equal("1.0-1", single.Version);
    }

    [Fact]
    public void Should_ParseSuccessfully_When_ArchitecturePresent()
    {
        // Architecture: is validated-but-deferred to #70: tolerated, not stored.
        var parser = ParserTestHelpers.ResolveParser("dpkg");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "status",
            ImageFixtureBuilder.DpkgStatusWithArch(("arch-pkg", "4.2-1", "arm64")));

        var deps = parser.Parse(path);

        var single = Assert.Single(deps);
        Assert.Equal("arch-pkg", single.Name);
        Assert.Equal("4.2-1", single.Version);
    }

    [Fact]
    public void Should_IgnoreStatusValue_When_StatusFieldVaries()
    {
        var parser = ParserTestHelpers.ResolveParser("dpkg");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "status",
            "Package: removed-but-listed\nStatus: deinstall ok config-files\nVersion: 7.0-1\n\n"
                + "Package: not-installed-pkg\nStatus: unknown ok not-installed\nVersion: 8.0-2\n\n");

        var deps = parser.Parse(path);

        Assert.Equal(2, deps.Count);
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("7.0-1", byName["removed-but-listed"]);
        Assert.Equal("8.0-2", byName["not-installed-pkg"]);
    }
}
