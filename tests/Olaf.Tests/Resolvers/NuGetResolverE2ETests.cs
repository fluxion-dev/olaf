using Olaf.Core;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// E2E tests: real network calls to NuGet registry (https://api.nuget.org).
/// Uses real HttpClient (not StubHttpMessageHandler). Marked with
/// [Trait("Category", "E2E")] so CI can filter with
/// --filter "Category!=E2E" to keep unit tests fast.
/// Packages are pinned versions with stable, well-known licenses.
/// Shared client E2ETestHelpers.CreateRealClient (no User-Agent needed).
/// </summary>
public sealed class NuGetResolverE2ETests
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveNewtonsoftJson_MIT_When_RealNuGetCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("nuget", http);
        var dep = new Dependency("nuget", "Newtonsoft.Json", "13.0.3", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.NotNull(result.LicenseText);
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("nuget.org", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveSerilog_Apache2_When_RealNuGetCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("nuget", http);
        var dep = new Dependency("nuget", "Serilog", "3.1.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.SourceUrl);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveDapper_Apache2_When_RealNuGetCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("nuget", http);
        var dep = new Dependency("nuget", "Dapper", "2.1.35", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.LicenseText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_PackageNotFound()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("nuget", http);
        var dep = new Dependency("nuget", "this-package-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

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
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("nuget", http);
        var dep = new Dependency("nuget", "Newtonsoft.Json", "999.999.999", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
