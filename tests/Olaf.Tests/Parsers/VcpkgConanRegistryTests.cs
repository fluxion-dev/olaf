using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Registry wiring for vcpkg + conan (issue #15): ecosystem filter,
/// recursive scan, and registry-level lock preference for conan.
/// </summary>
public sealed class VcpkgConanRegistryTests
{
    [Fact]
    public void Should_FilterVcpkg_When_EcosystemVcpkgGiven()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("vcpkg", "vcpkg.json"), dir, "vcpkg.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("conan", "conanfile.txt"), dir, "conanfile.txt");

            var vcpkgOnly = new ParserRegistry().Scan(dir, "vcpkg");

            Assert.NotEmpty(vcpkgOnly);
            Assert.All(vcpkgOnly, d => Assert.Equal("vcpkg", d.Ecosystem));
            Assert.Contains(vcpkgOnly, d => d.Name == "fmt");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_FilterConanCaseInsensitive_When_EcosystemUppercase()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("vcpkg", "vcpkg.json"), dir, "vcpkg.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("conan", "conanfile.txt"), dir, "conanfile.txt");

            var conanOnly = new ParserRegistry().Scan(dir, "CONAN");

            Assert.NotEmpty(conanOnly);
            Assert.All(conanOnly, d => Assert.Equal("conan", d.Ecosystem));
            Assert.Contains(conanOnly, d => d.Name == "fmt");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_FindBoth_When_VcpkgAndConanMonorepoScanned()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("vcpkg", "vcpkg.json"),
                Path.Combine(root, "native-vcpkg"), "vcpkg.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("conan", "conanfile.txt"),
                Path.Combine(root, "native-conan"), "conanfile.txt");

            var deps = new ParserRegistry().Scan(root);

            Assert.Contains(deps, d => d.Ecosystem == "vcpkg" && d.Name == "fmt");
            Assert.Contains(deps, d => d.Ecosystem == "conan" && d.Name == "fmt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferLock_When_ConanfileTxtAndLockCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("conan", "conanfile.txt"), dir, "conanfile.txt");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("conan", "conan.lock"), dir, "conan.lock");

            var deps = new ParserRegistry().Scan(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("3.11.3", byName["nlohmann_json"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
