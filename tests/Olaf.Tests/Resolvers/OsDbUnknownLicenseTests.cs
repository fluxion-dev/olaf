using Olaf.Core;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// OS DB deps (apk/dpkg/rpm) have no registry-backed SPDX source: the caching
/// resolver returns <c>Unknown</c> with the exact
/// <c>license-unknown: unsupported ecosystem.</c> reason, offline, no HTTP.
/// Declared-license (apk <c>L:</c>) + arch + PURL enrichment is deferred to
/// issue #70 — no resolver changes here.
/// </summary>
public sealed class OsDbUnknownLicenseTests
{
    [Fact]
    public async Task Should_ReturnUnknownWithExactReason_When_ApkDep()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new InvalidOperationException("OS DB deps must not touch the network."));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveCachingResolver("apk", http);
        var dep = new Dependency("apk", "musl", "1.2.5-r0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.Equal("license-unknown: unsupported ecosystem.", result.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknownWithExactReason_When_DpkgDep()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new InvalidOperationException("OS DB deps must not touch the network."));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveCachingResolver("dpkg", http);
        var dep = new Dependency("dpkg", "bash", "2:5.2-5", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.Equal("license-unknown: unsupported ecosystem.", result.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknownWithExactReason_When_RpmDep()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new InvalidOperationException("OS DB deps must not touch the network."));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveCachingResolver("rpm", http);
        var dep = new Dependency("rpm", "glibc", "2.38-8", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.Equal("license-unknown: unsupported ecosystem.", result.Reason);
        Assert.Equal(0, handler.CallCount);
    }
}
