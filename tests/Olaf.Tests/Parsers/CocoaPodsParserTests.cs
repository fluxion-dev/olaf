using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// CocoaPods parser (issue #14): Podfile pod lines + Podfile.lock PODS preference.
/// </summary>
public sealed class CocoaPodsParserTests
{
    [Fact]
    public void Should_HandlePodFiles_When_CanHandleChecked()
    {
        var parser = ParserTestHelpers.ResolveParser("cocoapods");

        Assert.Equal("cocoapods", parser.Ecosystem);
        Assert.True(parser.CanHandle("Podfile"));
        Assert.True(parser.CanHandle("Podfile.lock"));
        Assert.False(parser.CanHandle("Package.swift"));
        Assert.False(parser.CanHandle("Gemfile"));
    }

    [Fact]
    public void Should_ParseManifest_When_PodfileOnly()
    {
        var dir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("cocoapods", "Podfile"), "Podfile");
        try
        {
            var parser = ParserTestHelpers.ResolveParser("cocoapods");
            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("~> 5.8", byName["Alamofire"]);
            Assert.Equal("5.6.0", byName["SnapKit"]);
            Assert.Equal("*", byName["SwiftyJSON"]);
            Assert.All(deps, d => Assert.False(d.IsTransitive));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ParseLock_When_PodfileLockOnly()
    {
        var dir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("cocoapods", "Podfile.lock"), "Podfile.lock");
        try
        {
            var parser = ParserTestHelpers.ResolveParser("cocoapods");
            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("5.8.1", byName["Alamofire"]);
            Assert.Equal("5.6.0", byName["SnapKit"]);
            Assert.Equal("5.0.1", byName["SwiftyJSON"]);

            // Subspec dependency lines (SnapKit/Core) must not become deps.
            Assert.DoesNotContain("SnapKit/Core", byName.Keys);
            Assert.All(deps, d => Assert.True(d.IsTransitive));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_SkipCommentsAndSources_When_ParsingPodfileVariants()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "Podfile"),
                "source 'https://cdn.cocoapods.org/'\n"
                + "# pod 'Ignored', '1.0'\n"
                + "target 'App' do\n"
                + "  pod 'Alamofire', '~> 5.8'\n"
                + "  pod 'SwiftyJSON'\n"
                + "end\n");

            var parser = ParserTestHelpers.ResolveParser("cocoapods");
            var deps = parser.Parse(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal(2, byName.Count);
            Assert.Equal("~> 5.8", byName["Alamofire"]);
            Assert.Equal("*", byName["SwiftyJSON"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ReturnEmpty_When_PodfileLockHasNoPodsSection()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "Podfile.lock"),
                "DEPENDENCIES:\n  - Alamofire (~> 5.8)\n\nCOCOAPODS: 1.14.3\n");

            var parser = ParserTestHelpers.ResolveParser("cocoapods");
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
