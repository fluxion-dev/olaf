using Olaf.Core;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// E2E tests: real network calls to PyPI (https://pypi.org).
/// Uses real HttpClient (not StubHttpMessageHandler). Marked with
/// [Trait("Category", "E2E")] so CI can filter with
/// --filter "Category!=E2E" to keep unit tests fast.
/// Packages are pinned versions with stable, well-known licenses.
/// Shared client E2ETestHelpers.CreateRealClient (no User-Agent needed).
/// </summary>
public sealed class PyPIResolverE2ETests
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveRequests_Apache2_When_RealPyPICall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "requests", "2.31.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.NotNull(result.LicenseText);
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("pypi.org", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveFlask_BSD3_When_RealPyPICall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "flask", "3.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("BSD-3-Clause", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.LicenseText);
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("pypi.org", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveDjango_BSD3_When_RealPyPICall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "django", "5.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("BSD-3-Clause", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.LicenseText);
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("pypi.org", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_PackageNotFound()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "this-package-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

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
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "requests", "999.999.999", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
