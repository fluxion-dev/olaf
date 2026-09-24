using System.CommandLine;
using Olaf.Core;
using Olaf.Formatters;
using Olaf.Parsers;
using Olaf.Resolvers;

var inputOption = new Option<string?>("--input")
{
    Description = "Input file or directory to scan",
};
var formatOption = new Option<string>("--format")
{
    Description = "Output format: json|yaml|xml|html",
    DefaultValueFactory = _ => "json",
};
var outOption = new Option<string?>("--out")
{
    Description = "Output file path (default: stdout)",
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
    Description = "Limit scan to ecosystem: npm|nuget|pip",
};
var verboseOption = new Option<bool>("--verbose")
{
    Description = "Enable verbose logging",
};
var quietOption = new Option<bool>("--quiet")
{
    Description = "Suppress informational logging",
};

var rootCommand = new RootCommand("olaf license scanner")
{
    inputOption,
    formatOption,
    outOption,
    forceOption,
    strictOption,
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

    void Log(string message)
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

    if (ecosystem is not null
        && !ecosystem.Equals("npm", StringComparison.OrdinalIgnoreCase)
        && !ecosystem.Equals("nuget", StringComparison.OrdinalIgnoreCase)
        && !ecosystem.Equals("pip", StringComparison.OrdinalIgnoreCase)
        && !ecosystem.Equals("pypi", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Unsupported ecosystem '{ecosystem}'. Supported: npm|nuget|pip.");
        return 2;
    }

    ILicenseFormatter formatter;
    try
    {
        formatter = new FormatterRegistry().GetFormatter(format);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
    {
        Console.Error.WriteLine($"Unsupported format '{format}'. Supported: json|yaml|xml|html.");
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

    Log($"Scanning '{input}'... found {dependencies.Count} dependencies.");

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
        Log($"Resolved {license.Dependency.Ecosystem}:{license.Dependency.Name}@{license.Dependency.Version} -> {license.Status}");
    }

    var scanResult = new ScanResult(resolved);
    var output = formatter.FormatResult(scanResult);

    if (outPath is not null)
    {
        try
        {
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

    if (strict && resolved.Any(r => r.Status.Equals("Unknown", StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine("Strict mode: unknown licenses found.");
        return 1;
    }

    return 0;
});

return await rootCommand.Parse(args).InvokeAsync();
