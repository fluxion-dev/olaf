using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Coverage for the rpm text-dump parser (issue #65): <c>Packages</c> NVRA
/// lines (<c>name-ver-rel.arch</c>, <c>rpm -qa</c> output), right-split so
/// hyphenated names keep their hyphens, arch stripped, versions verbatim.
/// Binary BerkeleyDB bytes and empty/malformed input yield <c>[]</c>, never
/// throw. Standalone registry scan by file name.
/// </summary>
public sealed class RpmParserTests
{
    [Fact]
    public void Should_ParseNameAndVersion_When_PackagesFile()
    {
        var parser = ParserTestHelpers.ResolveParser("rpm");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "Packages",
            ImageFixtureBuilder.RpmPackages("bash-5.2-5.x86_64", "coreutils-9.4-1.x86_64"));

        var deps = parser.Parse(path);

        Assert.Equal(2, deps.Count);
        Assert.All(deps, d =>
        {
            Assert.Equal("rpm", d.Ecosystem);
            Assert.False(d.IsTransitive);
            Assert.True(d.Direct);
        });
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("5.2-5", byName["bash"]);
        Assert.Equal("9.4-1", byName["coreutils"]);
    }

    [Fact]
    public void Should_KeepVerRelVerbatim_When_VersionHasRelease()
    {
        var parser = ParserTestHelpers.ResolveParser("rpm");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "Packages",
            ImageFixtureBuilder.RpmPackages("glibc-2.38-8.x86_64", "legacypkg-2.0.noarch"));

        var deps = parser.Parse(path);

        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("2.38-8", byName["glibc"]);
        Assert.Equal("2.0", byName["legacypkg"]);
    }

    [Fact]
    public void Should_KeepHyphensInName_When_HyphenatedPackage()
    {
        // Right-split: the cut counts from the RIGHT, so hyphenated names
        // (e.g. gpg-pubkey-style) keep all their hyphens.
        var parser = ParserTestHelpers.ResolveParser("rpm");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "Packages",
            ImageFixtureBuilder.RpmPackages("gpg-pubkey-abcdef12-1.noarch", "my-lib-extra-1.2-3.x86_64"));

        var deps = parser.Parse(path);

        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("abcdef12-1", byName["gpg-pubkey"]);
        Assert.Equal("1.2-3", byName["my-lib-extra"]);
    }

    [Fact]
    public void Should_StripArchSuffix_When_AnyArch()
    {
        var parser = ParserTestHelpers.ResolveParser("rpm");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "Packages",
            ImageFixtureBuilder.RpmPackages("tool-4.0-2.noarch", "other-1.1-1.aarch64"));

        var deps = parser.Parse(path);

        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("4.0-2", byName["tool"]);
        Assert.Equal("1.1-1", byName["other"]);
    }

    [Fact]
    public void Should_MatchPackagesOnly_When_CanHandle()
    {
        var parser = ParserTestHelpers.ResolveParser("rpm");

        Assert.True(parser.CanHandle("Packages"));
        Assert.True(parser.CanHandle("PACKAGES"));
        Assert.True(parser.CanHandle(Path.Combine("var", "lib", "rpm", "Packages")));
        Assert.False(parser.CanHandle("installed"));
        Assert.False(parser.CanHandle("status"));
        Assert.False(parser.CanHandle("package.json"));
        Assert.False(parser.CanHandle("image.tar"));
        Assert.False(parser.CanHandle("Packages.lock"));
    }

    [Fact]
    public void Should_ReturnEmpty_When_BinaryPackages()
    {
        // Native BerkeleyDB bytes are deferred: sniffed, never crash.
        var parser = ParserTestHelpers.ResolveParser("rpm");
        var path = ImageFixtureBuilder.WriteTempBytesFile("Packages", ImageFixtureBuilder.BinaryRpmBytes());

        var deps = parser.Parse(path);

        Assert.Empty(deps);
    }

    [Fact]
    public void Should_ReturnEmpty_When_EmptyPackages()
    {
        var parser = ParserTestHelpers.ResolveParser("rpm");
        var path = ImageFixtureBuilder.WriteTempTextFile("Packages", string.Empty);

        var deps = parser.Parse(path);

        Assert.Empty(deps);
    }

    [Fact]
    public void Should_SkipLines_When_Malformed()
    {
        var parser = ParserTestHelpers.ResolveParser("rpm");
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "Packages",
            ImageFixtureBuilder.RpmPackages(
                "dash-only-no-dot",
                "nodotsordashes",
                "nameonly.x86_64",
                "-1.0.x86_64",
                string.Empty,
                "kept-1.0-1.x86_64"));

        var deps = parser.Parse(path);

        var single = Assert.Single(deps);
        Assert.Equal("kept", single.Name);
        Assert.Equal("1.0-1", single.Version);
    }

    [Fact]
    public void Should_KeepFirst_When_DuplicateNamesInDir()
    {
        var parser = ParserTestHelpers.ResolveParser("rpm");
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "Packages"),
                ImageFixtureBuilder.RpmPackages("tool-1.0-1.x86_64", "tool-2.0-1.x86_64", "other-3.0-2.noarch"));

            var deps = parser.Parse(dir);

            Assert.Equal(2, deps.Count);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);
            Assert.Equal("1.0-1", byName["tool"]);
            Assert.Equal("3.0-2", byName["other"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ScanStandalone_When_RegistryScansPackagesFixture()
    {
        var deps = new ParserRegistry().Scan(ParserTestHelpers.FixturePath("rpm", "Packages"));

        Assert.Equal(4, deps.Count);
        Assert.All(deps, d => Assert.Equal("rpm", d.Ecosystem));
        var byName = deps.ToDictionary(d => d.Name, d => d.Version);
        Assert.Equal("5.2-5", byName["bash"]);
        Assert.Equal("abcdef12-1", byName["gpg-pubkey"]);
    }
}
