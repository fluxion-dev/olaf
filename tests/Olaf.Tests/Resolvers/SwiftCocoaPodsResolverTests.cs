using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Swift (GitHub license probe) + CocoaPods (trunk API) license resolvers
/// (issue #14). All HTTP is stubbed (no live network); unknown/offline paths
/// use phantom names that can never resolve.
/// </summary>
public sealed class SwiftResolverTests
{
    [Fact]
    public async Task Should_ResolveSpdx_When_GitHubReturnsSpdxId()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { license = new { spdx_id = "MIT" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new SwiftLicenseResolver(http);
        var dep = new Dependency("swift", "Alamofire/Alamofire", "5.8.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("github.com", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_QueryLicenseEndpoint_When_Resolving()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { license = new { spdx_id = "Apache-2.0" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new SwiftLicenseResolver(http);
        var dep = new Dependency("swift", "Alamofire/Alamofire", "5.8.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.NotEmpty(handler.RequestedUrls);
        var url = handler.RequestedUrls[0]?.ToString() ?? string.Empty;
        Assert.Contains("api.github.com", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/license", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_GitHub404()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new SwiftLicenseResolver(http);
        var dep = new Dependency("swift", "example/this-package-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

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
        var resolver = new SwiftLicenseResolver(http);
        var dep = new Dependency("swift", "example/this-package-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("transport-error", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount); // retried exactly once
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_NameHasNoOwnerRepo()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { license = new { spdx_id = "MIT" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new SwiftLicenseResolver(http);
        var dep = new Dependency("swift", "some-lib-without-owner", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_EcosystemMismatch()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { license = new { spdx_id = "MIT" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new SwiftLicenseResolver(http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("ecosystem-mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }
}

public sealed class CocoaPodsResolverTests
{
    [Fact]
    public async Task Should_ResolveSpdx_When_TrunkReturnsLicenseString()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { license = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CocoaPodsLicenseResolver(http);
        var dep = new Dependency("cocoapods", "Alamofire", "5.8.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("cocoapods.org", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ResolveSpdx_When_TrunkReturnsLicenseObject()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { license = new { type = "Apache-2.0" } }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CocoaPodsLicenseResolver(http);
        var dep = new Dependency("cocoapods", "SnapKit", "5.6.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotEmpty(handler.RequestedUrls);
        var url = handler.RequestedUrls[0]?.ToString() ?? string.Empty;
        Assert.Contains("trunk.cocoapods.org", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_Trunk404()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CocoaPodsLicenseResolver(http);
        var dep = new Dependency("cocoapods", "this-pod-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

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
        var resolver = new CocoaPodsLicenseResolver(http);
        var dep = new Dependency("cocoapods", "this-pod-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

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
            StubHttpMessageHandler.Json(new { license = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CocoaPodsLicenseResolver(http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("ecosystem-mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Should_ResolveSpdx_When_PodspecReturnsLicenseString()
    {
        // Issue #84: trunk 200 with no usable license -> CDN podspec fallback
        // carries the license as a string. src/Olaf.Core/Dependency.cs:9
        // (Direct => !IsTransitive; isTransitive:false -> Direct:true).
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("cdn.cocoapods.org", StringComparison.OrdinalIgnoreCase))
            {
                return StubHttpMessageHandler.Json(new { license = "MIT" });
            }

            return StubHttpMessageHandler.Json(new { owners = new[] { "owner1" }, versions = new[] { new { name = "5.8.1" } } });
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CocoaPodsLicenseResolver(http);
        var dep = new Dependency("cocoapods", "Alamofire", "5.8.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("cocoapods.org", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount);
        Assert.True(handler.RequestedUrls.Count == 2);
        var trunkUrl = handler.RequestedUrls[0]?.ToString() ?? string.Empty;
        var cdnUrl = handler.RequestedUrls[1]?.ToString() ?? string.Empty;
        Assert.Contains("trunk.cocoapods.org", trunkUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cdn.cocoapods.org", cdnUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ResolveSpdx_When_PodspecReturnsLicenseObject()
    {
        // Issue #84: CDN podspec fallback carries the license as an object
        // ({"type": ...}). src/Olaf.Core/Dependency.cs:9
        // (Direct => !IsTransitive; isTransitive:false -> Direct:true).
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("cdn.cocoapods.org", StringComparison.OrdinalIgnoreCase))
            {
                return StubHttpMessageHandler.Json(new { license = new { type = "Apache-2.0" } });
            }

            return StubHttpMessageHandler.Json(new { owners = new[] { "owner1" }, versions = new[] { new { name = "5.6.0" } } });
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CocoaPodsLicenseResolver(http);
        var dep = new Dependency("cocoapods", "SnapKit", "5.6.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("cocoapods.org", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount);
        Assert.True(handler.RequestedUrls.Count == 2);
        var trunkUrl = handler.RequestedUrls[0]?.ToString() ?? string.Empty;
        var cdnUrl = handler.RequestedUrls[1]?.ToString() ?? string.Empty;
        Assert.Contains("trunk.cocoapods.org", trunkUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cdn.cocoapods.org", cdnUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_SkipCdn_When_TrunkHit()
    {
        // Issue #84: trunk-hit short-circuit performs zero 2nd GET.
        // src/Olaf.Core/Dependency.cs:9
        // (Direct => !IsTransitive; isTransitive:false -> Direct:true).
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { license = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CocoaPodsLicenseResolver(http);
        var dep = new Dependency("cocoapods", "Alamofire", "5.8.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(1, handler.CallCount);
        Assert.All(handler.RequestedUrls, u =>
            Assert.DoesNotContain("cdn.cocoapods.org", u?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_Podspec404()
    {
        // Issue #84: trunk 200 without license + CDN 404 -> both-miss
        // license-unknown (preserved vocabulary). src/Olaf.Core/Dependency.cs:9
        // (Direct => !IsTransitive; isTransitive:false -> Direct:true).
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("cdn.cocoapods.org", StringComparison.OrdinalIgnoreCase))
            {
                return StubHttpMessageHandler.NotFound();
            }

            return StubHttpMessageHandler.Json(new { owners = new[] { "owner1" }, versions = new[] { new { name = "5.8.1" } } });
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CocoaPodsLicenseResolver(http);
        var dep = new Dependency("cocoapods", "Alamofire", "5.8.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Should_SkipCdn_When_TrunkTransportFails()
    {
        // Issue #84: trunk transport failure short-circuits with
        // transport-error and never reaches the CDN (retried exactly once).
        // src/Olaf.Core/Dependency.cs:9
        // (Direct => !IsTransitive; isTransitive:false -> Direct:true).
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException("offline mode: no network"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CocoaPodsLicenseResolver(http);
        var dep = new Dependency("cocoapods", "Alamofire", "5.8.1", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("transport-error", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount); // retried exactly once
        Assert.All(handler.RequestedUrls, u =>
            Assert.Contains("trunk.cocoapods.org", u?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }
}
