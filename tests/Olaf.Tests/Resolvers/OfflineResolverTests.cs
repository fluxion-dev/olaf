using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

// Issue #77 (D5/D6) offline resolvers: every offline path resolves from the
// embedded DB only — zero HTTP, proven with a throwing handler (any send
// throws, so CallCount == 0 is air-gap proof).
//
// D3 seed-variant note: the checked-in DB ships metadata for the 35 seed ids
// with text:"" (full texts hydrate later via tools/update-spdx-db.sh), so a
// DB-hit-WITH-text is currently unreachable — texts resolve through the
// SpdxLicenseTexts seed fallback at runtime. The deferral is pinned in
// DbSeedVariant_DocumentsTextDeferral below; remove that pin once hydration
// lands (DB-hit-with-text becomes the primary arm).
public class OfflineResolverTests
{
    private static HttpClient ThrowingClient(StubHttpMessageHandler handler)
    {
        return ResolverTestHelpers.CreateClient(handler);
    }

    private static StubHttpMessageHandler ThrowingHandler()
    {
        return new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException($"offline: unexpected HTTP to {req.RequestUri}"));
    }

    private static Dependency NpmDep(string name)
    {
        return new Dependency(Ecosystem: "npm", Name: name, Version: "1.0.0", IsTransitive: false);
    }

    // Offline row 1/6 — caching resolver short-circuits before any resolver:
    // Unknown + offline-cache-miss reason, zero HTTP.
    [Fact]
    public async Task OfflineCache_Miss_ReturnsOfflineCacheMissReason_ZeroHttp()
    {
        var handler = ThrowingHandler();
        using var http = ThrowingClient(handler);
        var resolver = new CachingLicenseResolver(http, offline: true);

        var result = await resolver.ResolveAsync(NpmDep("olaf-offline-miss-xyz-77a"));

        Assert.Equal("Unknown", result.Status);
        Assert.NotNull(result.Reason);
        Assert.StartsWith("offline-cache-miss", result.Reason, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // Offline row 2/6 — fetcher resolves MIT text offline, zero HTTP.
    [Fact]
    public async Task FetcherOffline_MitText_ZeroHttp()
    {
        var handler = ThrowingHandler();
        using var http = ThrowingClient(handler);

        var (text, reason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http, tarballUrl: null, licenseUrl: null, "MIT", CancellationToken.None, offline: true);

        Assert.StartsWith("MIT License", text, StringComparison.Ordinal);
        Assert.Null(reason);
        Assert.Equal(0, handler.CallCount);
    }

    // Offline row 3/6 — fetcher resolves Apache-2.0 + GPL-3.0-only offline,
    // zero HTTP. (tail: 2 ids in one Fact.)
    [Fact]
    public async Task FetcherOffline_ApacheAndGplTexts_ZeroHttp()
    {
        var handler = ThrowingHandler();
        using var http = ThrowingClient(handler);

        var (apache, apacheReason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http, tarballUrl: null, licenseUrl: null, "Apache-2.0", CancellationToken.None, offline: true);
        var (gpl, gplReason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http, tarballUrl: null, licenseUrl: null, "GPL-3.0-only", CancellationToken.None, offline: true);

        Assert.StartsWith("Apache License", apache, StringComparison.Ordinal);
        Assert.Null(apacheReason);
        Assert.False(string.IsNullOrWhiteSpace(gpl));
        Assert.Null(gplReason);
        Assert.Equal(0, handler.CallCount);
    }

    // Offline row 4/6 — unknown id offline: null text + spdxdb-miss reason
    // (first-failure-wins with network stages skipped), zero HTTP.
    [Fact]
    public async Task FetcherOffline_UnknownId_NullWithSpdxDbMiss_ZeroHttp()
    {
        var handler = ThrowingHandler();
        using var http = ThrowingClient(handler);

        var (text, reason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http, tarballUrl: null, licenseUrl: null, "NO-SUCH-LICENSE-077", CancellationToken.None, offline: true);

        Assert.Null(text);
        Assert.Equal("spdxdb-miss:NO-SUCH-LICENSE-077", reason);
        Assert.Equal(0, handler.CallCount);
    }

    // Offline row 5/6 — network stages stay gated even when tarball +
    // licenseUrl are present: seed text wins, zero HTTP.
    [Fact]
    public async Task FetcherOffline_NetworkStagesGated_ZeroHttp()
    {
        var handler = ThrowingHandler();
        using var http = ThrowingClient(handler);

        var (text, reason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http,
            tarballUrl: "https://registry.npmjs.org/left-pad/-/left-pad-1.0.0.tgz",
            licenseUrl: "https://example.com/LICENSE",
            "MIT",
            CancellationToken.None,
            offline: true);

        Assert.StartsWith("MIT License", text, StringComparison.Ordinal);
        Assert.Null(reason);
        Assert.Equal(0, handler.CallCount);
    }

    // Offline row 6/6 — D3 seed-variant deferral pin: metadata resolves from
    // the DB today (TryGet true) while entry text is still "" (TryGetText
    // false); the runtime text comes from the SpdxLicenseTexts seed fallback.
    // Delete this pin when tools/update-spdx-db.sh hydrates full texts.
    [Fact]
    public void DbSeedVariant_DocumentsTextDeferral()
    {
        Assert.True(SpdxLicenseDb.TryGet("MIT", out var entry));
        Assert.NotNull(entry);
        Assert.False(SpdxLicenseDb.TryGetText("MIT", out _), "seed variant ships text:\"\" — DB-hit-with-text deferred until hydration.");
        Assert.True(SpdxLicenseTexts.TryGetText("MIT", out var seedText));
        Assert.StartsWith("MIT License", seedText, StringComparison.Ordinal);
    }
}
