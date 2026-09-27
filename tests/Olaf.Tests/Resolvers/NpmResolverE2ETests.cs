using Olaf.Core;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// E2E tests: real network calls to npm registry (https://registry.npmjs.org).
/// Uses real HttpClient (not StubHttpMessageHandler). Marked with
/// [Trait("Category", "E2E")] so CI can filter with
/// --filter "Category!=E2E" to keep unit tests fast.
/// Packages are pinned versions with stable, well-known licenses.
/// </summary>
public sealed class NpmResolverE2ETests
{
    private static HttpClient CreateRealClient()
    {
        return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveExpress_MIT_When_RealNpmCall()
    {
        using var http = CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "express", "4.18.2", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.NotNull(result.LicenseText);
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("npmjs.com", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveLodash_MIT_When_RealNpmCall()
    {
        using var http = CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.SourceUrl);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveReact_MIT_When_RealNpmCall()
    {
        using var http = CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "react", "18.2.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.LicenseText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_PackageNotFound()
    {
        using var http = CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "this-package-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_VersionDoesNotExist()
    {
        using var http = CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "express", "999.999.999", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}