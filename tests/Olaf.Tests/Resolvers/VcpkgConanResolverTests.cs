using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Vcpkg (embedded license field from ports vcpkg.json) license resolver
/// (issue #15). All HTTP is stubbed (no live network); unknown/offline
/// paths use phantom names. Conan primary (conan-center-index recipe data)
/// coverage lives in ConanResolverTests; conan ClearlyDefined-fallback
/// coverage lives in FallbackResolverTests.
/// </summary>
public sealed class VcpkgResolverTests
{
    [Fact]
    public async Task Should_ResolveSpdx_When_PortJsonHasLicenseString()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { name = "fmt", license = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new VcpkgLicenseResolver(http);
        var dep = new Dependency("vcpkg", "fmt", "*", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("vcpkg.io", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ResolveSpdx_When_PortJsonHasLicenseArray()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { name = "zlib", license = new[] { "Zlib" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new VcpkgLicenseResolver(http);
        var dep = new Dependency("vcpkg", "zlib", "1.3.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Zlib", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    public async Task Should_QueryPortsRegistry_When_Resolving()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { name = "fmt", license = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new VcpkgLicenseResolver(http);
        var dep = new Dependency("vcpkg", "fmt", "*", false);

        await resolver.ResolveAsync(dep);

        Assert.NotEmpty(handler.RequestedUrls);
        var url = handler.RequestedUrls[0]?.ToString() ?? string.Empty;
        Assert.Contains("raw.githubusercontent.com/microsoft/vcpkg", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ports/fmt/vcpkg.json", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_Port404()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new VcpkgLicenseResolver(http);
        var dep = new Dependency("vcpkg", "this-port-definitely-does-not-exist-olaf-xyz", "*", false);

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
        var resolver = new VcpkgLicenseResolver(http);
        var dep = new Dependency("vcpkg", "this-port-definitely-does-not-exist-olaf-xyz", "*", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("transport-error", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_EcosystemMismatch()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { name = "fmt", license = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new VcpkgLicenseResolver(http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("ecosystem-mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Should_FallbackToClearlyDefined_When_VcpkgRegistryMissing()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("clearlydefined", StringComparison.OrdinalIgnoreCase))
            {
                return StubHttpMessageHandler.Json(new { licensed = new { declared = "MIT" } });
            }

            return StubHttpMessageHandler.NotFound();
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveFallbackResolver("vcpkg", http);
        var dep = new Dependency("vcpkg", "fmt", "*", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("clearlydefined", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }
}
