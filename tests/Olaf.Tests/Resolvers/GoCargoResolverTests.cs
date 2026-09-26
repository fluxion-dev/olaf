using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Go + Cargo license resolvers. Instantiated directly via
/// `new GoLicenseResolver(http)` / `new CargoLicenseResolver(http)` —
/// deliberately NOT via ResolverTestHelpers.ResolveResolver("go"), which
/// collides per #23 ("go" is a substring of "CargoLicenseResolver").
/// All HTTP is stubbed (no live network); unknown/offline paths use phantom
/// versions that can never resolve.
/// </summary>
public sealed class GoResolverTests
{
    [Fact]
    public async Task Should_ResolveSpdx_When_GoProxyReturnsLicense()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { License = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new GoLicenseResolver(http);
        var dep = new Dependency("go", "github.com/spf13/cobra", "v1.8.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("pkg.go.dev", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_EscapeVersion_When_GoVersionHasPlus()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { License = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new GoLicenseResolver(http);
        var dep = new Dependency("go", "github.com/spf13/cobra", "v1.8.0+incompatible", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.NotEmpty(handler.RequestedUrls);
        var url = handler.RequestedUrls[0]?.ToString() ?? string.Empty;
        Assert.Contains("proxy.golang.org", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%2B", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_GoProxy404()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new GoLicenseResolver(http);
        var dep = new Dependency("go", "example.com/this-module-definitely-does-not-exist-olaf-xyz", "v9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_OfflineTransportFails()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException("offline mode: no network"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new GoLicenseResolver(http);
        var dep = new Dependency("go", "example.com/this-module-definitely-does-not-exist-olaf-xyz", "v9.9.9", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("transport-error", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount); // retried exactly once
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_EcosystemMismatch()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { License = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new GoLicenseResolver(http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("ecosystem-mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }
}

public sealed class CargoResolverTests
{
    [Fact]
    public async Task Should_ResolveSpdx_When_CratesIoReturnsLicense()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { version = new { license = "MIT" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CargoLicenseResolver(http);
        var dep = new Dependency("cargo", "serde", "1.0.197", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("crates.io", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_EscapeVersion_When_CargoVersionHasPlus()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { version = new { license = "MIT" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CargoLicenseResolver(http);
        var dep = new Dependency("cargo", "serde", "1.0.0+build", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.NotEmpty(handler.RequestedUrls);
        var url = handler.RequestedUrls[0]?.ToString() ?? string.Empty;
        Assert.Contains("crates.io", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%2B", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_CratesIo404()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CargoLicenseResolver(http);
        var dep = new Dependency("cargo", "this-crate-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_OfflineTransportFails()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException("offline mode: no network"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CargoLicenseResolver(http);
        var dep = new Dependency("cargo", "this-crate-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("transport-error", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount); // retried exactly once
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_EcosystemMismatch()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { version = new { license = "MIT" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CargoLicenseResolver(http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("ecosystem-mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }
}
