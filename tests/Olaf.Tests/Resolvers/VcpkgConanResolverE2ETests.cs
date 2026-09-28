using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// E2E tests: real network calls to raw.githubusercontent.com (vcpkg ports).
/// Uses a real HttpClient (no stub handler).
/// Marked with [Trait("Category", "E2E")] so CI can
/// filter with --filter "Category!=E2E" to keep unit tests fast.
/// Live-shape snapshot 2026-09-28: vcpkg fmt 200
/// license MIT (string); vcpkg zlib 200 license Zlib (array branch).
/// Conan primary deleted in #123 (conan.io/center/api 404s for existing
/// recipes: fmt/11.0.2, zlib/1.3.1, name-only, v1 all 404; search 308 -&gt;
/// 404 — endpoint dead though config.yml lists fmt 11.0.2 and the HTML page
/// shows fmt 12.2.0). Pins: fmt (vcpkg name-only),
/// zlib 1.3.2 (vcpkg live port).
/// Shared client E2ETestHelpers.CreateRealClient (no User-Agent needed).
/// NOTE: vcpkg ignores Dependency.Version (URL built from the name only),
/// so V3 uses a phantom NAME, not a bad version (Composer/Swift precedent).
/// </summary>
public sealed class VcpkgConanResolverE2ETests
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveFmt_MIT_When_RealVcpkgCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new VcpkgLicenseResolver(http);
        var dep = new Dependency("vcpkg", "fmt", "*", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("vcpkg.io", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveZlib_Zlib_When_RealVcpkgCall()
    {
        // zlib exercises the license-array branch (VcpkgLicenseResolver :40).
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new VcpkgLicenseResolver(http);
        var dep = new Dependency("vcpkg", "zlib", "1.3.2", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Zlib", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("vcpkg.io", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_VcpkgPortNotFound()
    {
        // Version is ignored (URL built from name only), so the phantom is
        // in the NAME, not the version (Composer/Swift precedent).
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new VcpkgLicenseResolver(http);
        var dep = new Dependency("vcpkg", "this-port-definitely-does-not-exist-olaf-xyz", "*", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
