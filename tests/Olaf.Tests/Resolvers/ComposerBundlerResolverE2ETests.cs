using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// E2E tests: real network calls to Packagist (https://repo.packagist.org)
/// and RubyGems (https://rubygems.org). Uses real HttpClient (not
/// StubHttpMessageHandler). Marked with [Trait("Category", "E2E")] so CI can
/// filter with --filter "Category!=E2E" to keep unit tests fast.
/// Packages are pinned versions with stable, well-known licenses.
/// Shared client E2ETestHelpers.CreateRealClient (no User-Agent needed).
/// The shared BundlerLicenseResolver serves both "bundler" and "gem" labels.
/// NOTE: both resolvers ignore Dependency.Version (URLs are built from the
/// name only), so not-found tests use phantom NAMES, not bad versions.
/// </summary>
public sealed class ComposerBundlerResolverE2ETests
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveMonolog_MIT_When_RealPackagistCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new ComposerLicenseResolver(http);
        var dep = new Dependency("composer", "monolog/monolog", "3.5.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("packagist.org", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveSymfonyConsole_MIT_When_RealPackagistCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new ComposerLicenseResolver(http);
        var dep = new Dependency("composer", "symfony/console", "6.3.4", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.SourceUrl);
        Assert.NotNull(result.LicenseText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveRails_MIT_When_RealRubyGemsCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("bundler", "rails", "7.0.8", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("rubygems.org", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.LicenseText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveRake_MIT_When_EcosystemIsGem()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("gem", "rake", "13.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_ComposerPackageNotFound()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new ComposerLicenseResolver(http);
        var dep = new Dependency("composer", "example/this-package-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_GemNotFound()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("bundler", "this-gem-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
