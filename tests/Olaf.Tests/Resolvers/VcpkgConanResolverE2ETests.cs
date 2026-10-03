using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// E2E tests: real network calls to raw.githubusercontent.com (vcpkg ports,
/// conan-center-index recipes). Uses a real HttpClient (no stub handler).
/// Marked with [Trait("Category", "E2E")] so CI can
/// filter with --filter "Category!=E2E" to keep unit tests fast.
/// Live-shape snapshot 2026-09-28: vcpkg fmt 200
/// license MIT (string); vcpkg zlib 200 license Zlib (array branch).
/// Conan primary restored off conan-center-index recipe data (2026-10-03):
/// conan.io/center/api stays dead (former primary deleted in #123), but
/// recipes/{name}/config.yml + conanfile.py license attributes resolve
/// (fmt/11.0.2 MIT, zlib/1.3.1 Zlib). Pins: fmt (vcpkg name-only),
/// zlib 1.3.2 (vcpkg live port), fmt 11.0.2 + zlib 1.3.1 (conan live).
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

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveFmt_MIT_When_RealConanCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "fmt", "11.0.2", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("conan.io", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveZlib_Zlib_When_RealConanCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "zlib", "1.3.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Zlib", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("conan.io", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_ConanRecipeNotFound()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "this-recipe-definitely-does-not-exist-olaf-xyz", "*", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
