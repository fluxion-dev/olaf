using System.Collections.Concurrent;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class CachingLicenseResolver : ILicenseResolver
{
    private static readonly ConcurrentDictionary<string, ResolvedLicense> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly bool _offline;
    private readonly DiskLicenseCache? _disk;
    private readonly TimeSpan _resolvedTtl;
    private readonly bool _readDisk;

    public CachingLicenseResolver(HttpClient httpClient, bool offline = false)
        : this(httpClient, offline, disk: null)
    {
    }

    // Issue #78: disk is the L2 (B5). refreshCache skips L2 reads but keeps
    // L2 writes; a null disk bypasses L2 entirely (the #123 CLI always
    // passes a disk; null is the tests-only bypass).
    public CachingLicenseResolver(
        HttpClient httpClient,
        bool offline = false,
        DiskLicenseCache? disk = null,
        TimeSpan? resolvedTtl = null,
        bool refreshCache = false)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _offline = offline;
        _disk = disk;
        _resolvedTtl = resolvedTtl ?? TimeSpan.FromDays(30);
        _readDisk = disk is not null && !refreshCache;
    }

    internal static void ClearCache() => Cache.Clear();

    // Late-hit fallback shared by the transport arms below (B5): a record
    // cached concurrently (L1) or already on disk (L2) rescues a failed
    // resolve. Ordering lives here + the L1→L2 head above — never inside the
    // LicenseTextFetcher chain (facade-double-lookup rule).
    private ResolvedLicense? TryLateHit(string key, Dependency dependency)
    {
        if (Cache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        if (_readDisk && _disk!.TryGet(dependency, _resolvedTtl, out var diskHit) && diskHit is not null)
        {
            Cache[key] = diskHit;
            return diskHit;
        }

        return null;
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        var key = CacheKey.Of(dependency);

        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // Issue #78 (B5): L1 -> L2 disk -> offline-miss. A disk hit rescues
        // offline runs and skips HTTP online (ordering stays OUTSIDE the
        // LicenseTextFetcher chain — facade-double-lookup rule). A disk miss
        // (absent or TTL-expired) also evicts any matching L1 key.
        if (_readDisk && _disk!.TryGet(dependency, _resolvedTtl, out var diskHit) && diskHit is not null)
        {
            Cache[key] = diskHit;
            return diskHit;
        }

        Cache.TryRemove(key, out _);

        if (_offline)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "offline-cache-miss: no cached entry and no network in offline mode.");
        }

        try
        {
            var inner = CreatePrimary(dependency);
            if (inner is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: unsupported ecosystem.");
            }

            var result = await inner.ResolveAsync(dependency, cancellationToken).ConfigureAwait(false);
            // In-memory only (whole record incl. LicenseText). Unknown —
            // including text-stage failures — stays uncached in L1. #78 disk
            // L2 persists Resolved (30d default) + not-found Unknown (1d);
            // transport/timeout/resolver-error never persist (B2 predicate
            // inside DiskLicenseCache.Store).
            if (result.Status.Equals("Resolved", StringComparison.OrdinalIgnoreCase))
            {
                Cache[key] = result;
            }
            else if (Cache.TryGetValue(key, out var lateHit))
            {
                return lateHit;
            }

            _disk?.Store(result);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return TryLateHit(key, dependency)
                ?? new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: request timed out: {ex.Message}");
        }
        catch (HttpRequestException)
        {
            return TryLateHit(key, dependency)
                ?? new ResolvedLicense(dependency, null, null, null, "Unknown", "offline-cache-miss: no cached entry and no network.");
        }
        catch (Exception ex)
        {
            return TryLateHit(key, dependency)
                ?? new ResolvedLicense(dependency, null, null, null, "Unknown", $"resolver-error: {ex.Message}");
        }
    }

    // Single home for ecosystem -> primary-resolver mapping. ClearlyDefinedFallbackResolver
    // stays fallback-only (no CreatePrimary duplicate); primary+fallback orchestration lives in ScanRunner.
    private ILicenseResolver? CreatePrimary(Dependency dependency)
    {
        if (dependency.Ecosystem.Equals("nuget", StringComparison.OrdinalIgnoreCase))
        {
            return new NuGetLicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("npm", StringComparison.OrdinalIgnoreCase))
        {
            return new NpmLicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("pypi", StringComparison.OrdinalIgnoreCase)
            || dependency.Ecosystem.Equals("pip", StringComparison.OrdinalIgnoreCase))
        {
            return new PyPILicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("go", StringComparison.OrdinalIgnoreCase))
        {
            return new GoLicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("cargo", StringComparison.OrdinalIgnoreCase))
        {
            return new CargoLicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("maven", StringComparison.OrdinalIgnoreCase)
            || dependency.Ecosystem.Equals("gradle", StringComparison.OrdinalIgnoreCase))
        {
            return new MavenLicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("composer", StringComparison.OrdinalIgnoreCase))
        {
            return new ComposerLicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("bundler", StringComparison.OrdinalIgnoreCase)
            || dependency.Ecosystem.Equals("gem", StringComparison.OrdinalIgnoreCase))
        {
            return new BundlerLicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("swift", StringComparison.OrdinalIgnoreCase))
        {
            return new SwiftLicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("cocoapods", StringComparison.OrdinalIgnoreCase))
        {
            return new CocoaPodsLicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("vcpkg", StringComparison.OrdinalIgnoreCase))
        {
            return new VcpkgLicenseResolver(_http);
        }

        // Conan primary restored off the conan-center-index recipe data
        // (config.yml versions->folder + conanfile.py license attribute):
        // the conan.io/center/api endpoint stays dead (see #123) and the
        // ClearlyDefined fallback (conancenter arm, kept) serves 200-empty.
        if (dependency.Ecosystem.Equals("conan", StringComparison.OrdinalIgnoreCase))
        {
            return new ConanLicenseResolver(_http);
        }

        return null;
    }
}
