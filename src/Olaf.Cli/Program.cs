using System.CommandLine;
using Olaf.Core;
using Olaf.Formatters;
using Olaf.Parsers;
using Olaf.Resolvers;

const string SupportedFormats = "json|yaml|xml|html|txt|md";
const string SupportedEcosystems = "npm|nuget|pip|go|cargo";

var inputOption = new Option<string?>("--input")
{
    Description = "Input file or directory to scan",
};
var formatOption = new Option<string>("--format")
{
    Description = $"Output format: {SupportedFormats} (default: json)",
    DefaultValueFactory = _ => "json",
};
var outOption = new Option<string?>("--out")
{
    Description = "Output file path (default: stdout; parent directories are created)",
};
var forceOption = new Option<bool>("--force")
{
    Description = "Overwrite output file if it exists",
};
var strictOption = new Option<bool>("--strict")
{
    Description = "Fail on unresolved or unknown licenses",
};
var ecosystemOption = new Option<string?>("--ecosystem")
{
    Description = $"Limit scan to ecosystem: {SupportedEcosystems} (pypi alias for pip)",
};
var verboseOption = new Option<bool>("--verbose")
{
    Description = "Enable verbose logging",
};
var quietOption = new Option<bool>("--quiet")
{
    Description = "Suppress informational logging",
};
var allowOption = new Option<string?>("--allow")
{
    Description = "Comma-separated SPDX allow-list; strict-gate fails licenses not in the list",
};
var denyOption = new Option<string?>("--deny")
{
    Description = "Comma-separated SPDX deny-list; strict-gate fails licenses in the list",
};

var rootCommand = new RootCommand($"""
    olaf license scanner
    Scans {SupportedEcosystems} projects, resolves licenses, and writes a report to stdout or a file.
    Strict policy gate: --strict fails (exit 1) on unknown licenses; --allow/--deny filter by SPDX (strict-gate semantics).

    Examples:
      olaf --input package.json
      olaf --input ./src --out report.json --format yaml
      olaf --input ./src --ecosystem npm
      olaf --input package.json --strict
      olaf --input package.json --strict --allow MIT,Apache-2.0
      olaf --input package.json --strict --deny GPL-2.0-only,GPL-3.0-only
      olaf --input ./src --out nested/dir/out.json
    """)
{
    inputOption,
    formatOption,
    outOption,
    forceOption,
    strictOption,
    allowOption,
    denyOption,
    ecosystemOption,
    verboseOption,
    quietOption,
};

rootCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var input = parseResult.GetValue(inputOption);
    var format = parseResult.GetValue(formatOption) ?? "json";
    var outPath = parseResult.GetValue(outOption);
    var force = parseResult.GetValue(forceOption);
    var strict = parseResult.GetValue(strictOption);
    var ecosystem = parseResult.GetValue(ecosystemOption);
    var verbose = parseResult.GetValue(verboseOption);
    var quiet = parseResult.GetValue(quietOption);
    var allowRaw = parseResult.GetValue(allowOption);
    var denyRaw = parseResult.GetValue(denyOption);

    static HashSet<string> ParseSpdxSet(string? csv)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv))
        {
            return set;
        }

        foreach (var entry in csv.Split(','))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length > 0)
            {
                set.Add(trimmed);
            }
        }

        return set;
    }

    static bool IsSupportedEcosystem(string? ecosystem)
    {
        return ecosystem is not null
            && (ecosystem.Equals("npm", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("nuget", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("pip", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("pypi", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("go", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("cargo", StringComparison.OrdinalIgnoreCase));
    }

    var allowed = ParseSpdxSet(allowRaw);
    var denied = ParseSpdxSet(denyRaw);
    var hasAllow = allowed.Count > 0;
    var hasDeny = denied.Count > 0;

    void LogVerbose(string message)
    {
        if (verbose && !quiet)
        {
            Console.Error.WriteLine(message);
        }
    }

    if (string.IsNullOrWhiteSpace(input))
    {
        Console.Error.WriteLine("Missing required --input <file|dir>.");
        return 2;
    }

    if (!File.Exists(input) && !Directory.Exists(input))
    {
        Console.Error.WriteLine($"Input not found: '{input}'.");
        return 2;
    }

    if (ecosystem is not null && !IsSupportedEcosystem(ecosystem))
    {
        Console.Error.WriteLine($"Unsupported ecosystem '{ecosystem}'. Supported: npm|nuget|pip|go|cargo.");
        return 2;
    }

    ILicenseFormatter formatter;
    try
    {
        formatter = new FormatterRegistry().GetFormatter(format);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
    {
        Console.Error.WriteLine($"Unsupported format '{format}'. Supported: json|yaml|xml|html|txt|md.");
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
        dependencies = new ParserRegistry().Scan(input, ecosystem);
    }
    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Failed to scan input '{input}': {ex.Message}");
        return 2;
    }

    LogVerbose($"Scanning '{input}'... found {dependencies.Count} dependencies.");

    using var http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    var cacheResolver = new CachingLicenseResolver(http);
    var fallbackResolver = new ClearlyDefinedFallbackResolver(http);

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
                if (license.Status.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
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
        var dep = dependencies[i];
        tasks[i] = ResolveWithFallbackAsync(dep, cancellationToken);
    }

    var results = await Task.WhenAll(tasks).ConfigureAwait(false);
    var resolved = new List<ResolvedLicense>(results);
    foreach (var license in results)
    {
        LogVerbose($"Resolved {FormatDependency(license.Dependency)} -> {license.Status}");
    }

    var scanResult = new ScanResult(resolved);
    var output = formatter.FormatResult(scanResult);

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

    static bool IsStrictViolation(bool strict, List<ResolvedLicense> licenses)
    {
        return strict && licenses.Any(r => r.Status.Equals("Unknown", StringComparison.OrdinalIgnoreCase));
    }

    static string EffectiveSpdx(ResolvedLicense license)
    {
        if (!string.IsNullOrWhiteSpace(license.SpdxId))
        {
            return license.SpdxId.Trim();
        }

        return "Unknown";
    }

    static string FormatDependency(Dependency dependency)
    {
        return $"{dependency.Ecosystem}:{dependency.Name}@{dependency.Version}";
    }

    static string FormatOffender(ResolvedLicense license)
    {
        return $"{FormatDependency(license.Dependency)} -> {EffectiveSpdx(license)}";
    }

    static bool IsPolicyOffender(ResolvedLicense license, bool enforceUnknown, HashSet<string> allowed, HashSet<string> denied)
    {
        var effective = EffectiveSpdx(license);
        var isUnknown = license.Status.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        var hasAllow = allowed.Count > 0;
        var hasDeny = denied.Count > 0;
        if (hasAllow && !allowed.Contains(effective))
        {
            return true;
        }

        if (hasDeny && denied.Contains(effective))
        {
            return true;
        }

        if (enforceUnknown && isUnknown && !(hasAllow && allowed.Contains(effective)))
        {
            return true;
        }

        return false;
    }

    // Default gate (no allow/deny): preserve exact strict message + exit 1.
    if (!hasAllow && !hasDeny)
    {
        if (IsStrictViolation(strict, resolved))
        {
            Console.Error.WriteLine("Strict mode: unknown licenses found.");
            return 1;
        }

        return 0;
    }

    // Allow/deny gate (report already written above — report-write-first order).
    var policyEnforced = strict || hasAllow || hasDeny;
    if (!strict)
    {
        Console.Error.WriteLine("Warning: --allow/--deny without --strict; applying policy gate.");
    }

    var offenders = resolved.Where(r => IsPolicyOffender(r, policyEnforced, allowed, denied)).ToList();
    if (offenders.Count > 0)
    {
        Console.Error.WriteLine("Strict mode: policy gate violations found.");
        foreach (var offender in offenders)
        {
            Console.Error.WriteLine(FormatOffender(offender));
        }

        Console.Error.WriteLine($"{offenders.Count} offender(s) found.");
        return 1;
    }

    return 0;
});

return await rootCommand.Parse(args).InvokeAsync();
