using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// OS DB multi-manager registry coverage (issue #65): apk <c>installed</c> +
/// dpkg <c>status</c> + rpm <c>Packages</c> fixtures in one dir scan without
/// clashes (no language parser claims these basenames), sorted
/// apk &lt; dpkg &lt; rpm, same name in two ecosystems stays distinct.
/// </summary>
public sealed class OsDbRegistryTests
{
    [Fact]
    public void Should_ScanAllOsDbs_When_DirHasApkDpkgRpmFixtures()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("apk", "installed"), root, "installed");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("dpkg", "status"), root, "status");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("rpm", "Packages"), root, "Packages");

            var deps = new ParserRegistry().Scan(root);

            // 3 apk + 2 dpkg + 4 rpm; bash exists in dpkg AND rpm (distinct ecosystems).
            Assert.Equal(9, deps.Count);
            Assert.Contains(deps, d => d.Ecosystem == "apk" && d.Name == "musl" && d.Version == "1.2.5-r0");
            Assert.Contains(deps, d => d.Ecosystem == "dpkg" && d.Name == "bash" && d.Version == "2:5.2-5");
            Assert.Contains(deps, d => d.Ecosystem == "rpm" && d.Name == "bash" && d.Version == "5.2-5");
            Assert.Contains(deps, d => d.Ecosystem == "rpm" && d.Name == "gpg-pubkey" && d.Version == "abcdef12-1");
            Assert.Equal(
                new[] { "apk", "dpkg", "rpm" },
                deps.Select(d => d.Ecosystem).Distinct().ToList());
            Assert.All(
                new ParserRegistry().Scan(Path.Combine(root, "installed")),
                d => Assert.Equal("apk", d.Ecosystem));
            Assert.All(
                new ParserRegistry().Scan(Path.Combine(root, "status")),
                d => Assert.Equal("dpkg", d.Ecosystem));
            Assert.All(
                new ParserRegistry().Scan(Path.Combine(root, "Packages")),
                d => Assert.Equal("rpm", d.Ecosystem));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
