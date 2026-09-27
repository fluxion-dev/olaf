using Olaf.Tests.Parsers;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #66 registry/doc coverage (offline, fixture-based):
/// (1) every format resolved through FormatterRegistry (the --format path)
/// exposes the direct field; (2) the Direct flag surfaced in reports derives
/// from parser IsTransitive values with the documented heuristic (manifest
/// entries direct, lock entries transitive) on real fixtures.
/// </summary>
public sealed class TransitiveCoverageTests
{
    [Fact]
    public void Should_ExposeDirectField_When_AllFormatsResolvedViaRegistry()
    {
        var scan = new Olaf.Core.ScanResult(new List<Olaf.Core.ResolvedLicense>
        {
            new(
                new Olaf.Core.Dependency("npm", "express", "4.18.2", false),
                "MIT",
                "MIT License",
                "https://example.com/express/LICENSE",
                "Resolved",
                null),
            new(
                new Olaf.Core.Dependency("npm", "shadow-dep", "1.0.0", true),
                null,
                null,
                null,
                "Unknown",
                "not-found."),
        });

        foreach (var format in new[] { "json", "yaml", "xml", "html", "txt", "md" })
        {
            var formatter = FormatterTestHelpers.GetFormatterViaRegistry(format);
            var output = formatter.FormatResult(scan);
            FormatterTestHelpers.AssertDirectFieldPresent(output, format);
        }
    }

    [Fact]
    public void Should_DeriveDirectFromIsTransitive_When_ScanningManifestAndLockFixtures()
    {
        // Manifest entries are direct; lock entries are transitive (documented
        // heuristic — reports surface Direct = !IsTransitive).
        var manifestDir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("npm", "package.json"), "package.json");
        var lockDir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("npm", "package-lock.json"), "package-lock.json");
        try
        {
            var manifestDeps = new Olaf.Parsers.ParserRegistry().Scan(manifestDir);
            Assert.NotEmpty(manifestDeps);
            Assert.All(manifestDeps, d => Assert.True(d.Direct, $"Expected manifest dep '{d.Name}' to be direct."));

            var lockDeps = new Olaf.Parsers.ParserRegistry().Scan(lockDir);
            Assert.NotEmpty(lockDeps);
            Assert.All(lockDeps, d => Assert.False(d.Direct, $"Expected lock dep '{d.Name}' to be transitive."));
        }
        finally
        {
            Directory.Delete(manifestDir, recursive: true);
            Directory.Delete(lockDir, recursive: true);
        }
    }
}
