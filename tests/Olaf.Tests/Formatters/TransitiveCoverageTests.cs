using System.Text.Json;
using Olaf.Core;
using Olaf.Parsers;
using Olaf.Tests.Parsers;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #66 registry/doc coverage (offline, fixture-based, #123 shape — 7
/// canonical formats, zero aliases):
/// (1) every kept human format resolved through FormatterRegistry (the
/// --format path) exposes the direct field; (2) the Direct flag surfaced in
/// reports derives from parser IsTransitive values with the documented
/// heuristic (manifest entries direct, lock entries transitive) on real
/// fixtures.
/// </summary>
public sealed class TransitiveCoverageTests
{
    [Fact]
    public void Should_ExposeDirectField_When_AllFormatsResolvedViaRegistry()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "express", "4.18.2", false),
                "MIT",
                "MIT License",
                "https://example.com/express/LICENSE",
                "Resolved",
                null),
            new(
                new Dependency("npm", "shadow-dep", "1.0.0", true),
                null,
                null,
                null,
                "Unknown",
                "not-found."),
        });

        foreach (var format in new[] { "json", "yaml", "xml", "md" })
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
            var manifestDeps = new ParserRegistry().Scan(manifestDir);
            Assert.NotEmpty(manifestDeps);
            Assert.All(manifestDeps, d => Assert.True(d.Direct, $"Expected manifest dep '{d.Name}' to be direct."));

            var lockDeps = new ParserRegistry().Scan(lockDir);
            Assert.NotEmpty(lockDeps);
            Assert.All(lockDeps, d => Assert.False(d.Direct, $"Expected lock dep '{d.Name}' to be transitive."));
        }
        finally
        {
            Directory.Delete(manifestDir, recursive: true);
            Directory.Delete(lockDir, recursive: true);
        }
    }

    /// <summary>
    /// Issue #67 scope contract (offline, constructed ScanResult): every
    /// component resolved through FormatterRegistry for `cyclonedx-json`
    /// carries `scope` = required (direct) or optional (transitive).
    /// Separate helper + single-format asserts — AssertDirectFieldPresent
    /// is NOT extended (BOM has no `direct` field).
    /// </summary>
    private static void AssertScopePresent(string output)
    {
        using var doc = JsonDocument.Parse(output);
        var components = doc.RootElement.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(2, components.Count);
        foreach (var component in components)
        {
            Assert.True(component.TryGetProperty("scope", out var scope), "CycloneDX component missing 'scope'.");
            Assert.True(
                scope.GetString() is "required" or "optional",
                $"CycloneDX scope must be required|optional, got '{scope.GetString()}'.");
        }
    }

    [Fact]
    public void Should_ExposeScopeField_When_CycloneDxJsonResolvedViaRegistry()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "express", "4.18.2", false),
                "MIT",
                "MIT License",
                "https://example.com/express/LICENSE",
                "Resolved",
                null),
            new(
                new Dependency("npm", "shadow-dep", "1.0.0", true),
                null,
                null,
                null,
                "Unknown",
                "not-found."),
        });

        var formatter = FormatterTestHelpers.GetFormatterViaRegistry("cyclonedx-json");
        var output = formatter.FormatResult(scan);
        AssertScopePresent(output);

        // Scope mapping pinned: direct → required, transitive → optional.
        using var doc = JsonDocument.Parse(output);
        var byName = doc.RootElement.GetProperty("components").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.Equal("required", byName["express"].GetProperty("scope").GetString());
        Assert.Equal("optional", byName["shadow-dep"].GetProperty("scope").GetString());
    }
}
