using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// E2E tests: real network calls to crates.io (https://crates.io/api/v1).
/// Uses real HttpClient (not StubHttpMessageHandler). Marked with
/// [Trait("Category", "E2E")] so CI can filter with
/// --filter "Category!=E2E" to keep unit tests fast.
/// Crates are pinned versions with stable, well-known licenses.
/// crates.io requires a User-Agent header (else 403), so the client
/// sets "olaf-license-scanner" (same precedent as SwiftLicenseResolver).
/// </summary>
public sealed class CargoResolverE2ETests
{
    private static HttpClient CreateRealClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "olaf-license-scanner");
        return http;
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveSerde_DualLicense_When_RealCratesCall()
    {
        using var http = CreateRealClient();
        var resolver = new CargoLicenseResolver(http);
        var dep = new Dependency("cargo", "serde", "1.0.197", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT OR Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.NotNull(result.LicenseText);
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("crates.io", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveTokio_MIT_When_RealCratesCall()
    {
        using var http = CreateRealClient();
        var resolver = new CargoLicenseResolver(http);
        var dep = new Dependency("cargo", "tokio", "1.36.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.SourceUrl);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveClap_DualLicense_When_RealCratesCall()
    {
        using var http = CreateRealClient();
        var resolver = new CargoLicenseResolver(http);
        var dep = new Dependency("cargo", "clap", "4.5.4", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT OR Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.LicenseText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_CrateNotFound()
    {
        using var http = CreateRealClient();
        var resolver = new CargoLicenseResolver(http);
        var dep = new Dependency("cargo", "this-crate-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

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
        var resolver = new CargoLicenseResolver(http);
        var dep = new Dependency("cargo", "serde", "999.999.999", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
