using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// E2E tests: real network calls to raw.githubusercontent.com (vcpkg ports)
/// and conan.io/center/api (Conan). Uses a real HttpClient (no stub handler).
/// Marked with [Trait("Category", "E2E")] so CI can
/// filter with --filter "Category!=E2E" to keep unit tests fast.
/// Live-shape snapshot 2026-09-28: vcpkg fmt 200
/// license MIT (string); vcpkg zlib 200 license Zlib (array branch);
/// conan fmt/11.0.2 404, zlib/1.3.1 404, name-only 404, v1 API 404,
/// search 308 -&gt; 404 (endpoint dead though config.yml lists fmt 11.0.2
/// and the HTML page shows fmt 12.2.0). Pins: fmt (vcpkg name-only),
/// zlib 1.3.2 (vcpkg live port), fmt@12.2.0 (conan HTML-proven version).
/// Shared client E2ETestHelpers.CreateRealClient (no User-Agent needed).
/// NOTE: vcpkg ignores Dependency.Version (URL built from the name only),
/// so V3 uses a phantom NAME, not a bad version (Composer/Swift precedent).
/// Conan version is LOAD-BEARING (URL includes version unless "*"), so C2
/// pins a phantom name AND phantom version.
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

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_RealConanCall()
    {
        // pending #110: conan.io/center/api 404s for existing recipes
        // (fmt/11.0.2, zlib/1.3.1, name-only, v1 all 404 as of 2026-09-28),
        // so the real-package arm pins honest Unknown (endpoint artifact,
        // NOT a license fact). NOT Resolved by design.
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "fmt", "12.2.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_ConanPackageNotFound()
    {
        // Conan version is significant (URL includes version), so the
        // phantom pins a nonexistent name AND version.
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "this-package-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
