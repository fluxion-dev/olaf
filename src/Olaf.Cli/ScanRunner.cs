using Olaf.Core;
using Olaf.Formatters;
using Olaf.Parsers;
using Olaf.Resolvers;

namespace Olaf.Cli;

// Issue #123: single-purpose scan+report core behind `olaf generate <DIR>`.
// Shared HttpClient with `User-Agent: olaf-license-scanner` once;
// SemaphoreSlim(8) fan-out; ClearlyDefined fallback offline-gated at the
// callsite (never inside ClearlyDefinedFallbackResolver). Report-write-FIRST:
// the report is written before the --strict gate runs; manifest-less dirs
// yield a valid empty report with exit 0. Exit codes: 0 ok, 1 strict-gate,
// 2 usage/scan/format/write errors.
public sealed class ScanRunner
{
    // Single home for the 7-token list lives in FormatterRegistry; this
    // alias keeps `generate --help` and the unsupported-format error on the
    // same literal without a second copy.
    public const string SupportedFormats = FormatterRegistry.SupportedFormats;

    // Single User-Agent for every outbound license request (#96, new shape).
    public const string UserAgent = "olaf-license-scanner";

    private static readonly HttpClient SharedHttp = CreateSharedClient();

    private readonly HttpClient _http;

    public ScanRunner(HttpClient? httpClient = null)
    {
        _http = httpClient ?? SharedHttp;
    }

    internal static HttpClient CreateSharedClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    public async Task<int> GenerateAsync(
        string dir,
        string format,
        string? outPath,
        bool force,
        bool strict,
        bool offline,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            Console.Error.WriteLine("Missing required PATH <dir>.");
            return 2;
        }

        if (!Directory.Exists(dir) && !File.Exists(dir))
        {
            Console.Error.WriteLine($"Input not found: '{dir}'.");
            return 2;
        }

        ILicenseFormatter formatter;
        try
        {
            formatter = new FormatterRegistry().GetFormatter(format);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Console.Error.WriteLine($"Unsupported format '{format}'. Supported: {SupportedFormats}.");
            return 2;
        }

        if (outPath is not null && File.Exists(outPath) && !force)
        {
            Console.Error.WriteLine($"Output file exists: '{outPath}'. Use --force to overwrite.");
            return 2;
        }

        IReadOnlyList<Dependency> dependencies;
        try
        {
            dependencies = new ParserRegistry().Scan(dir);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No manifests", StringComparison.Ordinal))
        {
            // Generate-only: manifest-less input yields a valid empty SBOM
            // (exit 0) via the normal format path below.
            dependencies = [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            Console.Error.WriteLine($"Failed to scan input '{dir}': {ex.Message}");
            return 2;
        }

        // Cache INTERNAL always-on, zero flags: L1+L2 via the OS-default
        // cache file; --offline order unchanged (memory -> disk ->
        // `offline-cache-miss`).
        var disk = new DiskLicenseCache(DiskLicenseCache.ResolveFilePath(null));
        var cacheResolver = new CachingLicenseResolver(
            _http,
            offline: offline,
            disk: disk,
            resolvedTtl: TimeSpan.FromDays(30));
        var fallbackResolver = new ClearlyDefinedFallbackResolver(_http);

        using var concurrency = new SemaphoreSlim(8);

        async Task<ResolvedLicense> ResolveWithFallbackAsync(Dependency dep, CancellationToken ct)
        {
            await concurrency.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ResolvedLicense license;
                try
                {
                    license = await cacheResolver.ResolveAsync(dep, ct).ConfigureAwait(false);
                    // Offline skips the ClearlyDefined fallback at the
                    // callsite (never inside ClearlyDefinedFallbackResolver) —
                    // offline misses stay Unknown (exit 0, or exit 1 with --strict).
                    if (!offline && license.Status.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var fb = await fallbackResolver.ResolveAsync(dep, ct).ConfigureAwait(false);
                            if (fb.Status.Equals("Resolved", StringComparison.OrdinalIgnoreCase))
                            {
                                license = fb;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            // Keep original Unknown — deterministic offline continue.
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    license = new ResolvedLicense(dep, null, null, null, "Unknown", $"resolver-error: {ex.Message}");
                }

                return license;
            }
            finally
            {
                concurrency.Release();
            }
        }

        var tasks = new Task<ResolvedLicense>[dependencies.Count];
        for (var i = 0; i < dependencies.Count; i++)
        {
            tasks[i] = ResolveWithFallbackAsync(dependencies[i], cancellationToken);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var scanResult = new ScanResult([.. results]);

        string output;
        try
        {
            output = formatter.FormatResult(scanResult);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            Console.Error.WriteLine($"Failed to format report: {ex.Message}");
            return 2;
        }

        if (outPath is not null)
        {
            try
            {
                if (Path.GetDirectoryName(outPath) is { Length: > 0 } parent)
                {
                    Directory.CreateDirectory(parent);
                }

                await File.WriteAllTextAsync(outPath, output, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"Failed to write output '{outPath}': {ex.Message}");
                return 2;
            }
        }
        else
        {
            Console.Out.Write(output);
        }

        // --strict = exit-1-on-Unknown only (report already written above).
        if (strict)
        {
            var offenders = results
                .Where(l => l.Status.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                .Select(l => $"{l.Dependency.Ecosystem}:{l.Dependency.Name}@{l.Dependency.Version} -> {LicenseDisplay.EffectiveSpdx(l)}")
                .ToList();
            if (offenders.Count > 0)
            {
                Console.Error.WriteLine("Strict mode: unknown licenses found.");
                foreach (var offender in offenders)
                {
                    Console.Error.WriteLine(offender);
                }

                Console.Error.WriteLine($"{offenders.Count} offender(s) found.");
                return 1;
            }
        }

        return 0;
    }
}
