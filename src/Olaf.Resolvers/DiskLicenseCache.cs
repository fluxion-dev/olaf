using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Olaf.Core;

namespace Olaf.Resolvers;

// Issue #78: persistent on-disk license-resolution cache (B1/B2/B4).
//
// Store shape (B1): key = CacheKey.Of() (Trim + LowerInvariant + pip/pypi
// unification); value = {spdx, licenseText, sourceUrl, status, reason,
// fetchedAt (UTC), etag} + full Enrichment when non-null (omit-null, never
// re-scraped). licenseText > 1MiB is skipped for persist (per #71).
//
// TTL (B2): Resolved entries live `resolvedTtl` (default 30d); Unknown/not-found entries live 1d. Transport, timeout,
// and resolver-error reasons are NEVER cached (reason-prefix predicate).
// Expiry uses `age >= ttl` (exactly-at-TTL counts as expired) and evicts L2
// (the matching L1 key is evicted on the resolver miss path).
//
// Resilience (B4): corrupt file -> stderr warn + empty start, never exit 2;
// malformed fetchedAt -> single-entry miss (rest load); readers tolerate a
// partial file (warn + in-memory continue). Write-through per store
// (tmp+rename atomic under a shared SemaphoreSlim): a crash loses nothing
// already persisted; there is no save-at-end loss window.
//
// Paths (B3): explicit directory override (tests-only; the #123 CLI passes
// null and always lands on the OS-default cache file) + OLAF_CACHE_DIR.
// Precedence: explicit dir > OLAF_CACHE_DIR > XDG_CACHE_HOME > OS
// fallback (~/.cache/olaf, %LOCALAPPDATA%/olaf, ~/Library/Caches/olaf).
// Pinned decision: XDG_CACHE_HOME, when set and non-empty, wins on ALL OSes
// (not Linux-only); otherwise the OS-native fallback applies.
public sealed class DiskLicenseCache
{
    public const int MaxLicenseTextChars = 1024 * 1024;

    // Issue #123: on-disk schema version. Load validates it; a mismatch
    // (stale Unknown entries from an older schema) starts empty with a
    // stderr warning instead of serving stale records.
    public const int SchemaVersion = 2;

    public static readonly TimeSpan NotFoundTtl = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentDictionary<string, CacheEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    // Shared-instance write gate: atomic tmp+rename under contention (B4).
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public string FilePath { get; }

    public DiskLicenseCache(string filePath)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Load();
    }

    internal int Count => _entries.Count;

    public static string ResolveFilePath(string? flagDir)
    {
        if (!string.IsNullOrWhiteSpace(flagDir))
        {
            return Path.Combine(flagDir.Trim(), "cache.json");
        }

        var dir = GetEnvDir("OLAF_CACHE_DIR")
            ?? GetXdgDir()
            ?? GetDefaultDirectory();
        return Path.Combine(dir, "cache.json");
    }

    // Single home for trimmed env-dir reads (B3): null when unset/blank.
    private static string? GetEnvDir(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? GetXdgDir()
    {
        var xdg = GetEnvDir("XDG_CACHE_HOME");
        return xdg is null ? null : Path.Combine(xdg, "olaf");
    }

    public static string GetDefaultDirectory()
    {
        // Hoisted: one `OperatingSystem.*` token line each (fqn-lint counts
        // every `System.*` token line; the file stays within the ≤2 allowlist).
        var isWindows = OperatingSystem.IsWindows();
        var isMacOS = OperatingSystem.IsMacOS();
        if (isWindows && GetEnvDir("LOCALAPPDATA") is string local)
        {
            return Path.Combine(local, "olaf");
        }

        if (!isWindows && isMacOS && GetEnvDir("HOME") is string macHome)
        {
            return Path.Combine(macHome, "Library", "Caches", "olaf");
        }

        if (GetEnvDir("HOME") is string home)
        {
            return Path.Combine(home, ".cache", "olaf");
        }

        return Path.Combine(Directory.GetCurrentDirectory(), ".olaf-cache");
    }

    // Never-cached predicate (B2): transport/timeout/resolver-error reasons
    // bypass the disk entirely. Uses string literals + Ordinal.
    public static bool IsCacheable(ResolvedLicense result)
    {
        var reason = result.Reason ?? string.Empty;
        if (reason.StartsWith("timeout:", StringComparison.Ordinal)
            || reason.StartsWith("transport-error:", StringComparison.Ordinal)
            || reason.StartsWith("offline-cache-miss", StringComparison.Ordinal)
            || reason.StartsWith("resolver-error:", StringComparison.Ordinal))
        {
            return false;
        }

        return result.Status.Equals("Resolved", StringComparison.OrdinalIgnoreCase)
            || result.Status.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    public bool TryGet(Dependency dependency, TimeSpan resolvedTtl, out ResolvedLicense? license)
    {
        license = null;
        var key = CacheKey.Of(dependency);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        var ttl = entry.Status.Equals("Resolved", StringComparison.OrdinalIgnoreCase)
            ? resolvedTtl
            : NotFoundTtl;
        if (DateTime.UtcNow - entry.FetchedAtUtc >= ttl)
        {
            // Expiry evicts L2 (B2); persist the eviction best-effort.
            _entries.TryRemove(key, out _);
            PersistBestEffort();
            return false;
        }

        license = new ResolvedLicense(
            dependency,
            entry.Spdx,
            entry.LicenseText,
            entry.SourceUrl,
            entry.Status,
            entry.Reason,
            entry.Enrichment);
        return true;
    }

    public void Store(ResolvedLicense result)
    {
        if (!IsCacheable(result))
        {
            return;
        }

        if (result.LicenseText is not null && result.LicenseText.Length > MaxLicenseTextChars)
        {
            return;
        }

        var key = CacheKey.Of(result.Dependency);
        _entries[key] = new CacheEntry
        {
            Spdx = result.SpdxId,
            LicenseText = result.LicenseText,
            SourceUrl = result.SourceUrl,
            Status = result.Status,
            Reason = result.Reason,
            FetchedAtUtc = DateTime.UtcNow,
            Etag = null,
            Enrichment = result.Enrichment,
        };
        PersistBestEffort();
    }

    public void Evict(Dependency dependency)
    {
        if (_entries.TryRemove(CacheKey.Of(dependency), out _))
        {
            PersistBestEffort();
        }
    }

    private void Load()
    {
        string text;
        try
        {
            text = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) // allowlist: missing/unreadable cache file is an empty-start path, not a failure
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            if (File.Exists(FilePath))
            {
                Console.Error.WriteLine($"Warning: failed to read license cache '{FilePath}': {ex.Message}; starting empty.");
            }

            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Warning: corrupt license cache '{FilePath}': {ex.Message}; starting empty.");
            return;
        }

        using (doc)
        {
            if (!TryGetPropertyCaseInsensitive(doc.RootElement, "version", out var versionEl)
                || versionEl.ValueKind != JsonValueKind.Number
                || !versionEl.TryGetInt32(out var version)
                || version != SchemaVersion)
            {
                Console.Error.WriteLine($"Warning: unsupported license cache schema in '{FilePath}': expected version {SchemaVersion}; starting empty.");
                return;
            }

            if (!TryGetPropertyCaseInsensitive(doc.RootElement, "entries", out var entries)
                || entries.ValueKind != JsonValueKind.Object)
            {
                Console.Error.WriteLine($"Warning: corrupt license cache '{FilePath}': missing 'entries'; starting empty.");
                return;
            }

            foreach (var prop in entries.EnumerateObject())
            {
                try
                {
                    var entry = ParseEntry(prop.Value);
                    if (entry is not null)
                    {
                        _entries[prop.Name] = entry;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException) // allowlist: one malformed entry must not poison the remaining cache
                {
                    // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
                    Console.Error.WriteLine($"Warning: skipping malformed cache entry '{prop.Name}': {ex.Message}.");
                }
            }
        }
    }

    private static CacheEntry? ParseEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if ((!TryGetPropertyCaseInsensitive(element, "fetchedAt", out var fetchedAtEl)
            && !TryGetPropertyCaseInsensitive(element, "fetchedAtUtc", out fetchedAtEl))
            || fetchedAtEl.ValueKind != JsonValueKind.String
            || !DateTime.TryParse(
                fetchedAtEl.GetString(),
                null,
                DateTimeStyles.AdjustToUniversal,
                out var fetchedAt))
        {
            // Malformed fetchedAt -> entry-miss (B4): evict this entry only.
            return null;
        }

        static string? OptionalString(JsonElement el, string name)
            => TryGetPropertyCaseInsensitive(el, name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        Enrichment? enrichment = null;
        if (TryGetPropertyCaseInsensitive(element, "enrichment", out var enrichmentEl)
            && enrichmentEl.ValueKind == JsonValueKind.Object)
        {
            try
            {
                enrichment = enrichmentEl.Deserialize<Enrichment>(JsonOptions);
            }
            catch (JsonException)
            {
                enrichment = null;
            }
        }

        return new CacheEntry
        {
            Spdx = OptionalString(element, "spdx"),
            LicenseText = OptionalString(element, "licenseText"),
            SourceUrl = OptionalString(element, "sourceUrl"),
            Status = OptionalString(element, "status") ?? "Unknown",
            Reason = OptionalString(element, "reason"),
            FetchedAtUtc = fetchedAt.ToUniversalTime(),
            Etag = OptionalString(element, "etag"),
            Enrichment = enrichment,
        };
    }

    private void PersistBestEffort()
    {
        _writeGate.Wait();
        try
        {
            var parent = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var payload = new Dictionary<string, CacheEntry>(_entries, StringComparer.OrdinalIgnoreCase);
            var text = JsonSerializer.Serialize(new CacheFile(SchemaVersion, payload), JsonOptions);
            var tmpPath = FilePath + ".tmp";
            try
            {
                File.WriteAllText(tmpPath, text);
                File.Move(tmpPath, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) // allowlist: unwritable cache dir degrades to in-memory-only, never a scan failure
            {
                // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
                Console.Error.WriteLine($"Warning: failed to write license cache '{FilePath}': {ex.Message}; continuing in-memory.");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed class CacheEntry
    {
        [JsonPropertyName("spdx")]
        public string? Spdx { get; set; }

        [JsonPropertyName("licenseText")]
        public string? LicenseText { get; set; }

        [JsonPropertyName("sourceUrl")]
        public string? SourceUrl { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "Unknown";

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("fetchedAt")]
        public DateTime FetchedAtUtc { get; set; }

        [JsonPropertyName("etag")]
        public string? Etag { get; set; }

        [JsonPropertyName("enrichment")]
        public Enrichment? Enrichment { get; set; }
    }

    private sealed record CacheFile(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("entries")] Dictionary<string, CacheEntry> Entries);
}
