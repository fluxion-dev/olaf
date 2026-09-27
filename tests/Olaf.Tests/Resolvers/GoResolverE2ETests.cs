using Olaf.Core;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// E2E tests: real network calls to Go proxy (https://proxy.golang.org).
/// Uses real HttpClient (not StubHttpMessageHandler). Marked with
/// [Trait("Category", "E2E")] so CI can filter with
/// --filter "Category!=E2E" to keep unit tests fast.
/// Packages are pinned versions with stable, well-known licenses.
/// </summary>
public sealed class GoResolverE2ETests
{
    private static HttpClient CreateRealClient()
    {
        return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveCobra_Apache2_When_RealGoProxyCall()
    {
        using var http = CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("go", http);
        var dep = new Dependency("go", "github.com/spf13/cobra", "v1.8.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.NotNull(result.LicenseText);
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("pkg.go.dev", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveGin_MIT_When_RealGoProxyCall()
    {
        using var http = CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("go", http);
        var dep = new Dependency("go", "github.com/gin-gonic/gin", "v1.9.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.SourceUrl);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveGolangText_BSD3_When_RealGoProxyCall()
    {
        using var http = CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("go", http);
        var dep = new Dependency("go", "golang.org/x/text", "v0.14.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("BSD-3-Clause", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.LicenseText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_ModuleNotFound()
    {
        using var http = CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("go", http);
        var dep = new Dependency("go", "example.com/this-module-definitely-does-not-exist-olaf-xyz", "v9.9.9", false);

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
        var resolver = ResolverTestHelpers.ResolveResolver("go", http);
        var dep = new Dependency("go", "github.com/spf13/cobra", "v999.999.999", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}