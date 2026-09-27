using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Composer + Bundler license resolvers. Instantiated directly via
/// `new ComposerLicenseResolver(http)` / `new BundlerLicenseResolver(http)`.
/// All HTTP is stubbed (no live network); unknown/offline paths use phantom
/// names that can never resolve.
/// </summary>
public sealed class ComposerResolverTests
{
    [Fact]
    public async Task Should_ResolveSpdx_When_PackagistReturnsLicenseArray()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                packages = new Dictionary<string, object>
                {
                    ["monolog/monolog"] = new[] { new { version = "3.5.0", license = new[] { "MIT" } } },
                },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
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
    public async Task Should_RequestP2Metadata_When_PackageGiven()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                packages = new Dictionary<string, object>
                {
                    ["monolog/monolog"] = new[] { new { version = "3.5.0", license = new[] { "MIT" } } },
                },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ComposerLicenseResolver(http);
        var dep = new Dependency("composer", "monolog/monolog", "3.5.0", false);

        await resolver.ResolveAsync(dep);

        Assert.NotEmpty(handler.RequestedUrls);
        var url = handler.RequestedUrls[0]?.ToString() ?? string.Empty;
        Assert.Contains("repo.packagist.org/p2/monolog/monolog.json", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ResolveSpdx_When_PackagistReturnsLicenseString()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                packages = new Dictionary<string, object>
                {
                    ["symfony/console"] = new[] { new { version = "6.3.4", license = "MIT" } },
                },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ComposerLicenseResolver(http);
        var dep = new Dependency("composer", "symfony/console", "6.3.4", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_Packagist404()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ComposerLicenseResolver(http);
        var dep = new Dependency("composer", "example/this-package-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_NameNotVendorSlashPackage()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { packages = new Dictionary<string, object>() }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ComposerLicenseResolver(http);
        var dep = new Dependency("composer", "not-a-composer-name", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_OfflineTransportFails()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException("offline mode: no network"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ComposerLicenseResolver(http);
        var dep = new Dependency("composer", "example/this-package-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("transport-error", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount); // retried exactly once
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_PackagistBodyMalformed()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text("this is not json{{{"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ComposerLicenseResolver(http);
        var dep = new Dependency("composer", "monolog/monolog", "3.5.0", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_EcosystemMismatch()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { packages = new Dictionary<string, object>() }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ComposerLicenseResolver(http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("ecosystem-mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }
}

public sealed class BundlerResolverTests
{
    [Fact]
    public async Task Should_ResolveSpdx_When_RubyGemsReturnsLicenses()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { name = "rails", licenses = new[] { "MIT" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("bundler", "rails", "7.0.8", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("rubygems.org", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_RequestGemsApi_When_GemGiven()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { name = "rails", licenses = new[] { "MIT" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("bundler", "rails", "7.0.8", false);

        await resolver.ResolveAsync(dep);

        Assert.NotEmpty(handler.RequestedUrls);
        var url = handler.RequestedUrls[0]?.ToString() ?? string.Empty;
        Assert.Contains("rubygems.org/api/v1/gems/rails.json", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ResolveSpdx_When_RubyGemsReturnsLicenseString()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { name = "rake", licenses = Array.Empty<string>(), license = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("bundler", "rake", "13.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_RubyGems404()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("bundler", "this-gem-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_LicensesEmpty()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { name = "rails", licenses = Array.Empty<string>() }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("bundler", "rails", "7.0.8", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_OfflineTransportFails()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException("offline mode: no network"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("bundler", "this-gem-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("transport-error", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount); // retried exactly once
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_RubyGemsBodyMalformed()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text("this is not json{{{"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("bundler", "rails", "7.0.8", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_EcosystemMismatch()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { name = "rails", licenses = new[] { "MIT" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("ecosystem-mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }
}
