using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

// Issue #78 (B6 rows U1–U5): persistent disk-cache store shape (B1), TTL
// (B2), and resilience/paths (B4/B3). Direct DiskLicenseCache use — no HTTP
// at all (no handler needed). Temp dirs per test; static L1 is untouched
// here (disk-only, no CachingLicenseResolver), so no ClearCache needed.
public class DiskLicenseCacheTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "olaf-diskcache-78-" + Guid.NewGuid().ToString("N"));
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

    private static void SeedRawJson(string filePath, string entriesJson)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // Lowercase schema per DiskLicenseCache.ParseEntry (B1 store shape).
        File.WriteAllText(filePath, "{\"entries\":{" + entriesJson + "}}");
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

    // U1 — round-trip: resolve-shaped Resolved stored, reloaded from a fresh
    // instance, served as a hit with identical SPDX/status.
    [Fact]
    public void Should_HitReloadedCache_When_ResolvedStoredThenReloaded()
    {
        var dir = CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "cache.json");
            var dep = new Dependency("npm", "lodash", "4.17.21", false);
            var stored = new ResolvedLicense(dep, "MIT", "MIT License text", "https://example.com/lodash", "Resolved", "registry:npm", null);

            var writer = new DiskLicenseCache(file);
            writer.Store(stored);

            var reloaded = new DiskLicenseCache(file);
            var hit = reloaded.TryGet(dep, TimeSpan.FromDays(30), out var license);

            Assert.True(hit);
            Assert.NotNull(license);
            Assert.Equal("MIT", license.SpdxId);
            Assert.Equal("Resolved", license.Status);
            Assert.Equal("registry:npm", license.Reason);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // U2 — TTL expiry: Resolved TTL 30d default; age >= ttl evicts
    // (exactly-at-TTL counts as expired per B2 implementer pin). Tail: 3
    // entries in one Fact (just-inside hit, exactly-30d miss, older miss).
    // NOTE: wall-clock epsilon makes the exactly-30d seed read as 30d+e at
    // lookup, so the miss holds under either >= or >; the >= pin itself is
    // documented in DiskLicenseCache.TryGet.
    [Fact]
    public void Should_EvictAtThirtyDayBoundary_When_ResolvedTtlExpires()
    {
        var dir = CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "cache.json");
            var now = DateTime.UtcNow;
            var fresh = new Dependency("npm", "olaf-u2-fresh-78", "1.0.0", false);
            var boundary = new Dependency("npm", "olaf-u2-boundary-78", "1.0.0", false);
            var old = new Dependency("npm", "olaf-u2-old-78", "1.0.0", false);
            SeedRawJson(file,
                "\"" + CacheKey.Of(fresh) + "\":" + EntryJson("MIT", "Resolved", "registry:npm", now.AddDays(-29))
                + ",\"" + CacheKey.Of(boundary) + "\":" + EntryJson("MIT", "Resolved", "registry:npm", now.AddDays(-30))
                + ",\"" + CacheKey.Of(old) + "\":" + EntryJson("MIT", "Resolved", "registry:npm", now.AddDays(-31)));

            var cache = new DiskLicenseCache(file);
            var ttl = TimeSpan.FromDays(30);

            Assert.True(cache.TryGet(fresh, ttl, out var freshLicense));
            Assert.NotNull(freshLicense);
            Assert.Equal("MIT", freshLicense.SpdxId);
            Assert.False(cache.TryGet(boundary, ttl, out _));
            Assert.False(cache.TryGet(old, ttl, out _));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // U3 — corrupt file: stderr warn + empty start, never throws (B4: never
    // exit 2 via return-2 arms; the CLI maps this to exit 0 downstream).
    [Fact]
    public void Should_StartEmpty_WithWarn_When_CacheFileCorrupt()
    {
        var dir = CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "cache.json");
            File.WriteAllText(file, "{ this is not valid json !!!");

            var capture = new StringWriter();
            var original = Console.Error;
            Console.SetError(capture);
            DiskLicenseCache cache;
            try
            {
                cache = new DiskLicenseCache(file);
            }
            finally
            {
                Console.SetError(original);
            }

            Assert.Equal(0, cache.Count);
            Assert.False(cache.TryGet(new Dependency("npm", "anything", "1.0.0", false), TimeSpan.FromDays(30), out _));
            Assert.Contains("corrupt", capture.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // U4 — precedence: flag > OLAF_CACHE_DIR > XDG_CACHE_HOME > OS fallback
    // (B3). Tail: 3 precedence arms in one Fact (flag wins; env used with no
    // flag; XDG used with no flag and no env). Env save/restore via
    // try/finally; no other test class reads these vars (grep-verified).
    [Fact]
    public void Should_PreferFlagOverEnvOverXdg_When_ResolvingCachePath()
    {
        var prevOlaf = Environment.GetEnvironmentVariable("OLAF_CACHE_DIR");
        var prevXdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("OLAF_CACHE_DIR", "/tmp/olaf-env-cache-78");
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", "/tmp/olaf-xdg-78");

            Assert.Equal(
                Path.Combine("/tmp/olaf-flag-78", "cache.json"),
                DiskLicenseCache.ResolveFilePath("/tmp/olaf-flag-78"));
            Assert.Equal(
                Path.Combine("/tmp/olaf-env-cache-78", "cache.json"),
                DiskLicenseCache.ResolveFilePath(null));

            Environment.SetEnvironmentVariable("OLAF_CACHE_DIR", null);
            Assert.Equal(
                Path.Combine("/tmp/olaf-xdg-78", "olaf", "cache.json"),
                DiskLicenseCache.ResolveFilePath(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLAF_CACHE_DIR", prevOlaf);
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", prevXdg);
        }
    }

    // U5 — OS-path table + key normalization (B1/B3). The implementation
    // uses OperatingSystem checks directly (no injected path func), so this
    // pins the observable arms without mutating HOME: CacheKey table
    // (Trim + LowerInvariant + pypi->pip single home matching CreatePrimary)
    // plus default-dir shape (ends in "olaf"; exact on Linux). Tail: 4 key
    // cases + dir-shape asserts in one Fact.
    [Fact]
    public void Should_NormalizeKeysAndDefaultDir_When_OsPathTable()
    {
        Assert.Equal("npm:lodash@4.17.21", CacheKey.Of(new Dependency("npm", "lodash", "4.17.21", false)));
        Assert.Equal("npm:lodash@4.17.21", CacheKey.Of(new Dependency("NPM", "  Lodash ", " 4.17.21 ", false)));
        Assert.Equal("pip:requests@2.0.0", CacheKey.Of(new Dependency("pypi", "requests", "2.0.0", false)));
        Assert.Equal("pip:requests@2.0.0", CacheKey.Of(new Dependency("pip", "requests", "2.0.0", false)));

        var dir = DiskLicenseCache.GetDefaultDirectory();
        Assert.EndsWith("olaf", dir, StringComparison.Ordinal);
        if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                Assert.Equal(Path.Combine(home.Trim(), ".cache", "olaf"), dir);
            }
        }
    }
}
