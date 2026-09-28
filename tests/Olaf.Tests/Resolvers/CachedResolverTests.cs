using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

// Issue #78 (B6 rows R1–R4): resolver/disk composition (B5 TTL + B2
// never-cached predicate). Throwing-handler style per Resolvers/ convention:
// any HTTP send throws, so CallCount == 0 is air-gap proof. Temp cache dirs
// per test + ClearCache before/after (static L1 isolation); unique dep names
// per test so no cross-test L1 key ever collides.
public class CachedResolverTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "olaf-cachedres-78-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static StubHttpMessageHandler ThrowingHandler()
    {
        return new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException($"offline: unexpected HTTP to {req.RequestUri}"));
    }

    private static StubHttpMessageHandler MitHandler()
    {
        return new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                name = "stubbed",
                version = "0.0.0",
                license = "MIT",
            }));
    }

    private static void SeedRawJson(string filePath, string entriesJson)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // Lowercase schema per DiskLicenseCache.ParseEntry (B1 store shape),
        // including the current SchemaVersion (#123 load-validate).
        File.WriteAllText(filePath, "{\"version\":" + DiskLicenseCache.SchemaVersion + ",\"entries\":{" + entriesJson + "}}");
    }

    private static string EntryJson(string? spdx, string status, string reason, DateTime fetchedAtUtc)
    {
        var stamp = fetchedAtUtc.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        return "{\"spdx\":" + (spdx is null ? "null" : "\"" + spdx + "\"")
            + ",\"licenseText\":null,\"sourceUrl\":null"
            + ",\"status\":\"" + status + "\""
            + ",\"reason\":\"" + reason + "\""
            + ",\"fetchedAt\":\"" + stamp + "\""
            + ",\"etag\":null}";
    }

    // R1 — Resolved results persist: online resolve stores to disk, a fresh
    // DiskLicenseCache over the same file serves the hit.
    [Fact]
    public async Task Should_PersistResolvedEntry_When_OnlineResolveStoresToDisk()
    {
        CachingLicenseResolver.ClearCache();
        var dir = CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "cache.json");
            var handler = MitHandler();
            using var http = ResolverTestHelpers.CreateClient(handler);
            var disk = new DiskLicenseCache(file);
            var resolver = new CachingLicenseResolver(http, false, disk);
            var dep = new Dependency("npm", "olaf-r1-disk-78", "1.0.0", false);

            var result = await resolver.ResolveAsync(dep);

            Assert.Equal("Resolved", result.Status);
            Assert.Equal("MIT", result.SpdxId);
            Assert.Equal(1, handler.CallCount);

            var reloaded = new DiskLicenseCache(file);
            Assert.True(reloaded.TryGet(dep, TimeSpan.FromDays(30), out var license));
            Assert.NotNull(license);
            Assert.Equal("MIT", license.SpdxId);
        }
        finally
        {
            DeleteTempDir(dir);
            CachingLicenseResolver.ClearCache();
        }
    }

    // R2 — not-found short TTL: an Unknown entry aged 2d is evicted even
    // though the Resolved TTL is 30d, so the resolver re-issues HTTP; a
    // Resolved entry aged 2d still hits with zero additional HTTP. Tail: 2
    // arms in one Fact (evicted-Unknown re-resolves; fresh-Resolved hits).
    [Fact]
    public async Task Should_EvictNotFoundAfterOneDay_When_ResolvedTtlIsThirtyDays()
    {
        CachingLicenseResolver.ClearCache();
        var dir = CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "cache.json");
            var now = DateTime.UtcNow;
            var unknownDep = new Dependency("npm", "olaf-r2-unknown-78", "1.0.0", false);
            var resolvedDep = new Dependency("npm", "olaf-r2-resolved-78", "1.0.0", false);
            SeedRawJson(file,
                "\"" + CacheKey.Of(unknownDep) + "\":" + EntryJson(null, "Unknown", "license-unknown: stub", now.AddDays(-2))
                + ",\"" + CacheKey.Of(resolvedDep) + "\":" + EntryJson("MIT", "Resolved", "registry:npm", now.AddDays(-2)));

            var disk = new DiskLicenseCache(file);
            var handler = MitHandler();
            using var http = ResolverTestHelpers.CreateClient(handler);
            var resolver = new CachingLicenseResolver(http, false, disk);

            var unknownResult = await resolver.ResolveAsync(unknownDep);
            Assert.Equal("MIT", unknownResult.SpdxId);
            Assert.Equal(1, handler.CallCount);

            var resolvedResult = await resolver.ResolveAsync(resolvedDep);
            Assert.Equal("Resolved", resolvedResult.Status);
            Assert.Equal("MIT", resolvedResult.SpdxId);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            DeleteTempDir(dir);
            CachingLicenseResolver.ClearCache();
        }
    }

    // R3 — transport/timeout/resolver-error results are NEVER cached (B2
    // reason-prefix predicate). Tail: 3 reason-prefix arms in one Fact
    // (timeout: / offline-cache-miss / resolver-error:), each with a fresh
    // temp dir + unique dep; every arm asserts the reason prefix, zero
    // disk persistence, and zero leaked L1 state.
    [Fact]
    public async Task Should_NeverPersistTransportFailures_When_ReasonPrefixes()
    {
        CachingLicenseResolver.ClearCache();
        try
        {
            await NeverCachedArmAsync(new TaskCanceledException("simulated timeout"), "timeout:", "olaf-r3-timeout-78");
            // NOTE: HttpRequestException is swallowed by the inner npm resolver
            // into "transport-error:" (it never reaches the caching layer's own
            // offline-cache-miss arm); B2 requires it excluded all the same.
            await NeverCachedArmAsync(new HttpRequestException("simulated link down"), "transport-error:", "olaf-r3-httpreq-78");
            // NOTE: InvalidOperationException is mapped by primaries to
            // "parse-error:" (D2 contract, ResolverTests ResolverErrorIsolationTests),
            // so the resolver-error: arm uses NotSupportedException, which falls
            // through the primary's catch filters into its own "resolver-error:" arm.
            await NeverCachedArmAsync(new NotSupportedException("simulated bug"), "resolver-error:", "olaf-r3-reserr-78");
        }
        finally
        {
            CachingLicenseResolver.ClearCache();
        }
    }

    private static async Task NeverCachedArmAsync(Exception failure, string expectedPrefix, string depName)
    {
        var dir = CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "cache.json");
            var handler = new StubHttpMessageHandler((req, _) => throw failure);
            using var http = ResolverTestHelpers.CreateClient(handler);
            var disk = new DiskLicenseCache(file);
            var resolver = new CachingLicenseResolver(http, false, disk);
            var dep = new Dependency("npm", depName, "1.0.0", false);

            var result = await resolver.ResolveAsync(dep);

            Assert.Equal("Unknown", result.Status);
            Assert.NotNull(result.Reason);
            Assert.StartsWith(expectedPrefix, result.Reason, StringComparison.Ordinal);
            Assert.Equal(0, disk.Count);
            Assert.False(new DiskLicenseCache(file).TryGet(dep, TimeSpan.FromDays(30), out _));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // R4 — offline disk-hit: a disk entry rescues an offline run with zero
    // HTTP (throwing handler; CallCount == 0). Same-instance Store keeps this
    // independent of the file round-trip covered by U1/R1.
    [Fact]
    public async Task Should_ServeOfflineFromDisk_WithoutHttp_When_DiskHit()
    {
        CachingLicenseResolver.ClearCache();
        var dir = CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "cache.json");
            var dep = new Dependency("npm", "olaf-r4-offline-78", "1.0.0", false);
            var disk = new DiskLicenseCache(file);
            disk.Store(new ResolvedLicense(dep, "MIT", null, null, "Resolved", "registry:npm", null));

            var throwing = ThrowingHandler();
            using var http = ResolverTestHelpers.CreateClient(throwing);
            var offline = new CachingLicenseResolver(http, true, disk);

            var result = await offline.ResolveAsync(dep);

            Assert.Equal("Resolved", result.Status);
            Assert.Equal("MIT", result.SpdxId);
            Assert.Equal(0, throwing.CallCount);
        }
        finally
        {
            DeleteTempDir(dir);
            CachingLicenseResolver.ClearCache();
        }
    }
}
