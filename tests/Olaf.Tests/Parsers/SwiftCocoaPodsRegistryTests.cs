using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Registry wiring for swift + cocoapods (issue #14): ecosystem filter,
/// recursive scan, and registry-level lock preference.
/// </summary>
public sealed class SwiftCocoaPodsRegistryTests
{
    [Fact]
    public void Should_FilterSwift_When_EcosystemSwiftGiven()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("swift", "Package.swift"), dir, "Package.swift");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cocoapods", "Podfile"), dir, "Podfile");

            var registry = new ParserRegistry();
            var swiftOnly = registry.Scan(dir, "swift");

            Assert.NotEmpty(swiftOnly);
            Assert.All(swiftOnly, d => Assert.Equal("swift", d.Ecosystem));
            Assert.Contains(swiftOnly, d => d.Name == "Alamofire/Alamofire");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_FilterCocoaPodsCaseInsensitive_When_EcosystemUppercase()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("swift", "Package.swift"), dir, "Package.swift");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cocoapods", "Podfile"), dir, "Podfile");

            var registry = new ParserRegistry();
            var podsOnly = registry.Scan(dir, "COCOAPODS");

            Assert.NotEmpty(podsOnly);
            Assert.All(podsOnly, d => Assert.Equal("cocoapods", d.Ecosystem));
            Assert.Contains(podsOnly, d => d.Name == "Alamofire");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_FindBoth_When_SwiftAndPodsMonorepoScanned()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("swift", "Package.swift"),
                Path.Combine(root, "spm"), "Package.swift");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cocoapods", "Podfile"),
                Path.Combine(root, "ios"), "Podfile");

            var deps = new ParserRegistry().Scan(root);

            Assert.Contains(deps, d => d.Ecosystem == "swift" && d.Name == "Alamofire/Alamofire");
            Assert.Contains(deps, d => d.Ecosystem == "cocoapods" && d.Name == "Alamofire");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferLock_When_PackageSwiftAndResolvedCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("swift", "Package.swift"), dir, "Package.swift");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("swift", "Package.resolved"), dir, "Package.resolved");

            var deps = new ParserRegistry().Scan(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Lock set only: pinned 5.8.1, lock-exclusive swift-collections,
            // no manifest-only swift-argument-parser.
            Assert.Equal("5.8.1", byName["Alamofire/Alamofire"]);
            Assert.Equal("1.0.5", byName["apple/swift-collections"]);
            Assert.DoesNotContain("apple/swift-argument-parser", byName.Keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferLock_When_PodfileAndLockCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cocoapods", "Podfile"), dir, "Podfile");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("cocoapods", "Podfile.lock"), dir, "Podfile.lock");

            var deps = new ParserRegistry().Scan(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            // Lock pins win over manifest ranges/bare names.
            Assert.Equal("5.8.1", byName["Alamofire"]);
            Assert.Equal("5.0.1", byName["SwiftyJSON"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
