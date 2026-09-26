using System.Text.Json;
using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Registry wiring for composer + bundler (issue #13): ecosystem filter,
/// recursive scan, and registry-level lock preference.
/// </summary>
public sealed class ComposerBundlerRegistryTests
{
    [Fact]
    public void Should_FilterComposer_When_EcosystemComposerGiven()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("composer", "composer.json"), dir, "composer.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("bundler", "Gemfile"), dir, "Gemfile");

            var registry = new ParserRegistry();
            var composerOnly = registry.Scan(dir, "composer");

            Assert.NotEmpty(composerOnly);
            Assert.All(composerOnly, d => Assert.Equal("composer", d.Ecosystem));
            Assert.Contains(composerOnly, d => d.Name == "monolog/monolog");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_FilterBundlerCaseInsensitive_When_EcosystemUppercase()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("composer", "composer.json"), dir, "composer.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("bundler", "Gemfile"), dir, "Gemfile");

            var registry = new ParserRegistry();
            var bundlerOnly = registry.Scan(dir, "BUNDLER");

            Assert.NotEmpty(bundlerOnly);
            Assert.All(bundlerOnly, d => Assert.Equal("bundler", d.Ecosystem));
            Assert.Contains(bundlerOnly, d => d.Name == "rails");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_FindBoth_When_PhpRubyMonorepoScanned()
    {
        var root = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("composer", "composer.json"),
                Path.Combine(root, "php"), "composer.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("bundler", "Gemfile"),
                Path.Combine(root, "ruby"), "Gemfile");

            var deps = new ParserRegistry().Scan(root);

            Assert.Contains(deps, d => d.Ecosystem == "composer" && d.Name == "monolog/monolog");
            Assert.Contains(deps, d => d.Ecosystem == "bundler" && d.Name == "rails");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferLock_When_ComposerManifestAndLockCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("composer", "composer.json"), dir, "composer.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("composer", "composer.lock"), dir, "composer.lock");

            var deps = new ParserRegistry().Scan(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("3.5.0", byName["monolog/monolog"]);
            Assert.Equal("3.0.0", byName["psr/log"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Should_PreferLock_When_GemfileAndLockCoexist()
    {
        var dir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("bundler", "Gemfile"), dir, "Gemfile");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("bundler", "Gemfile.lock"), dir, "Gemfile.lock");

            var deps = new ParserRegistry().Scan(dir);
            var byName = deps.ToDictionary(d => d.Name, d => d.Version);

            Assert.Equal("7.0.8", byName["rails"]);
            Assert.Equal("7.0.8", byName["actionpack"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
