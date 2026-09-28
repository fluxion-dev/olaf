using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// E2E tests: real network calls to api.github.com (Swift) and
/// trunk.cocoapods.org (CocoaPods). Uses a real HttpClient (no stub handler).
/// Marked with [Trait("Category", "E2E")] so CI can
/// filter with --filter "Category!=E2E" to keep unit tests fast.
/// Repos/pods are pinned versions with stable, well-known licenses.
/// Shared client E2ETestHelpers.CreateRealClient(useUserAgent: true);
/// GitHub 403 rationale lives in the helper remarks.
/// NOTE: the Swift resolver ignores Dependency.Version (URLs are built from
/// the name only), so Swift not-found tests use phantom NAMES, not bad
/// versions (Composer precedent). The CocoaPods resolver uses
/// Dependency.Version verbatim in the CDN podspec path (null/empty/* ->
/// latest from the already-fetched trunk versions).
/// </summary>
public sealed class SwiftCocoaPodsResolverE2ETests
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveAlamofire_MIT_When_RealGitHubCall()
    {
        using var http = E2ETestHelpers.CreateRealClient(useUserAgent: true);
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
    [Trait("Category", "E2E")]
    public async Task Should_ResolveArgumentParser_Apache2_When_RealGitHubCall()
    {
        using var http = E2ETestHelpers.CreateRealClient(useUserAgent: true);
        var resolver = new SwiftLicenseResolver(http);
        var dep = new Dependency("swift", "apple/swift-argument-parser", "1.2.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("github.com", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_SwiftPackageNotFound()
    {
        using var http = E2ETestHelpers.CreateRealClient(useUserAgent: true);
        var resolver = new SwiftLicenseResolver(http);
        var dep = new Dependency("swift", "example/this-package-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnLicenseUnknown_When_SwiftNameHasNoOwner()
    {
        // No owner segment → license-unknown with zero HTTP (instant return).
        using var http = E2ETestHelpers.CreateRealClient(useUserAgent: true);
        var resolver = new SwiftLicenseResolver(http);
        var dep = new Dependency("swift", "some-lib-without-owner", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveAlamofire_MIT_When_RealPodspecCall()
    {
        // Issue #84: trunk returns owners + versions only (no license
        // payload), so the resolver falls back to the CDN podspec — the
        // real-pod arm is Resolved MIT by design.
        using var http = E2ETestHelpers.CreateRealClient(useUserAgent: true);
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
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_PodNotFound()
    {
        using var http = E2ETestHelpers.CreateRealClient(useUserAgent: true);
        var resolver = new CocoaPodsLicenseResolver(http);
        var dep = new Dependency("cocoapods", "this-pod-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
