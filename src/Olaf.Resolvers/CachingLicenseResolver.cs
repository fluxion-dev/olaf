using System.Collections.Concurrent;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class CachingLicenseResolver : ILicenseResolver
{
    private static readonly ConcurrentDictionary<string, ResolvedLicense> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly bool _offline;

    public CachingLicenseResolver(HttpClient httpClient, bool offline = false)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _offline = offline;
    }

    internal static void ClearCache() => Cache.Clear();

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        var key = $"{dependency.Ecosystem.ToLowerInvariant()}:{dependency.Name.ToLowerInvariant()}@{dependency.Version.ToLowerInvariant()}";

        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

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
            if (result.Status.Equals("Resolved", StringComparison.OrdinalIgnoreCase))
            {
                Cache[key] = result;
            }
            else if (Cache.TryGetValue(key, out var lateHit))
            {
                return lateHit;
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                return hit;
            }

            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: request timed out: {ex.Message}");
        }
        catch (HttpRequestException)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                return hit;
            }

            return new ResolvedLicense(dependency, null, null, null, "Unknown", "offline-cache-miss: no cached entry and no network.");
        }
        catch (Exception ex)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                return hit;
            }

            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"resolver-error: {ex.Message}");
        }
    }

    // Single home for ecosystem -> primary-resolver mapping. ClearlyDefinedFallbackResolver
    // stays fallback-only (no CreatePrimary duplicate); primary+fallback orchestration lives in Program.cs.
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

        return null;
    }
}
