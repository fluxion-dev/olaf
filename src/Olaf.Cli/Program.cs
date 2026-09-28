using System.CommandLine;
using System.Globalization;
using Olaf.Cli;
using Olaf.Core;
using Olaf.Formatters;
using Olaf.Parsers;
using Olaf.Resolvers;
using YamlDotNet.Core;

const string SupportedFormats = "json|yaml|xml|html|txt|md|cyclonedx-json|cyclonedx|cyclonedx-xml|spdx-json";
const string SupportedEcosystems = "npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan|apk|dpkg|rpm";

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
var templateOption = new Option<string?>("--template")
{
    Description = "Custom attribution template file (overrides --format)",
};
var forceOption = new Option<bool>("--force")
{
    Description = "Overwrite output file if it exists",
};
var strictOption = new Option<bool>("--strict")
{
    Description = "Fail on unresolved or unknown licenses",
};
var offlineOption = new Option<bool>("--offline")
{
    Description = "Resolve licenses from the embedded offline DB only (no network; unknown licenses stay Unknown)",
};
var ecosystemOption = new Option<string?>("--ecosystem")
{
    Description = $"Limit scan to ecosystem: {SupportedEcosystems} (pypi alias for pip)",
};
var maxImageMbOption = new Option<string?>("--max-image-mb")
{
    Description = $"Cap container-image scan at N megabytes uncompressed handled (default: {ContainerImageParser.DefaultMaxImageMb}; must be > 0)",
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
var rulesOption = new Option<string?>("--rules")
{
    Description = "Policy rules file (.sbom-rules.yaml)",
};
var directOnlyOption = new Option<bool>("--direct-only")
{
    Description = "Report direct dependencies only (exclude transitive; strict/allow/deny gates see the filtered set)",
};
var groupByLicenseOption = new Option<bool>("--group-by-license")
{
    Description = "Group txt/md/html output by license (ignored for SBOM formats)",
};
var includeTransitiveOption = new Option<bool>("--include-transitive")
{
    Description = "Explicitly include transitive dependencies (same as default: report all)",
};

var pathArgument = new Argument<string>("PATH")
{
    Description = "Project path to scan (default: current directory)",
    DefaultValueFactory = _ => ".",
};

var rootCommand = new RootCommand($"""
    olaf license scanner
    Scans {SupportedEcosystems} projects, resolves licenses, and writes a report to stdout or a file.
    Strict policy gate: --strict fails (exit 1) on unknown licenses; --allow/--deny filter by SPDX (strict-gate semantics).

    Examples:
      olaf --input package.json
      olaf --input ./src --out report.json --format yaml
      olaf --input package.json --format spdx-json
      olaf --input ./src --ecosystem npm
      olaf --input package.json --strict
      olaf --input package.json --strict --allow MIT,Apache-2.0
      olaf --input package.json --strict --deny GPL-2.0-only,GPL-3.0-only
      olaf --input ./src --out nested/dir/out.json
      olaf --input package.json --direct-only
      olaf --input package.json --include-transitive
    Transitive filter: neither flag (or --include-transitive) reports all
    dependencies; --direct-only reports direct dependencies only. Both flags
    together is a conflict (exit 2). Strict/allow/deny gates see the FILTERED set.
    """)
{
    inputOption,
    formatOption,
    templateOption,
    outOption,
    forceOption,
    strictOption,
    offlineOption,
    allowOption,
    denyOption,
    rulesOption,
    directOnlyOption,
    includeTransitiveOption,
    groupByLicenseOption,
    ecosystemOption,
    maxImageMbOption,
    verboseOption,
    quietOption,
};

// Issue #76: shared scan+report core. BOTH the legacy root (--input) path
// and the `generate [PATH]` subcommand call this; the ONLY behavioral fork
// is AllowEmpty (generate-only valid-empty-SBOM on manifest-less input).
async Task<int> ScanAndReportAsync(ScanOpts o, CancellationToken cancellationToken)
{
    var input = o.Input;
    var format = o.Format;
    var templatePath = o.TemplatePath;
    var outPath = o.OutPath;
    var force = o.Force;
    var ecosystem = o.Ecosystem;
    var maxImageMbRaw = o.MaxImageMbRaw;
    var verbose = o.Verbose;
    var quiet = o.Quiet;
    var allowRaw = o.AllowRaw;
    var denyRaw = o.DenyRaw;
    var directOnly = o.DirectOnly;
    var includeTransitive = o.IncludeTransitive;
    var groupByLicense = o.GroupByLicense;
    var rulesPath = o.RulesPath;
    // Issue #77: offline resolves from the embedded DB only (no network).
    var offline = o.Offline;
    // Issue #75: flag "presence" is token-presence (even an empty --allow ""
    // replaces the file list for that key). String options consume value
    // tokens; bool --strict takes none, so presence uses IsImplicit.
    // (Presence is computed per-command in each SetAction and packed into
    // ScanOpts, so this core never touches ParseResult/Option instances.)
    var allowFlagPresent = o.AllowFlagPresent;
    var denyFlagPresent = o.DenyFlagPresent;
    var strictFlagPresent = o.StrictFlagPresent;
    var formatFlagPresent = o.FormatFlagPresent;
    var allowEmpty = o.AllowEmpty;

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
                || ecosystem.Equals("cargo", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("maven", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("gradle", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("composer", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("bundler", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("swift", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("cocoapods", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("vcpkg", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("conan", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("apk", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("dpkg", StringComparison.OrdinalIgnoreCase)
                || ecosystem.Equals("rpm", StringComparison.OrdinalIgnoreCase));
    }

    // Issue #75: pypi is an alias for pip on both sides of the exclude
    // compare (decision recorded in Factory-Notes).
    static bool IsExcludedEcosystem(string ecosystem, HashSet<string> excludes)
    {
        var normalized = ecosystem.Equals("pypi", StringComparison.OrdinalIgnoreCase) ? "pip" : ecosystem;
        foreach (var exclude in excludes)
        {
            var want = exclude.Equals("pypi", StringComparison.OrdinalIgnoreCase) ? "pip" : exclude;
            if (normalized.Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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
        Console.Error.WriteLine($"Unsupported ecosystem '{ecosystem}'. Supported: {SupportedEcosystems}.");
        return 2;
    }

    if (directOnly && includeTransitive)
    {
        Console.Error.WriteLine("Conflicting flags: --direct-only and --include-transitive cannot be used together.");
        return 2;
    }

    if (maxImageMbRaw is not null)
    {
        if (!double.TryParse(
                maxImageMbRaw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var maxImageMb)
            || double.IsNaN(maxImageMb)
            || double.IsInfinity(maxImageMb)
            || maxImageMb <= 0)
        {
            Console.Error.WriteLine($"Invalid --max-image-mb '{maxImageMbRaw}': must be a positive number of megabytes.");
            return 2;
        }

        const double bytesPerMb = 1024 * 1024;
        ContainerImageParser.MaxImageBytes = maxImageMb * bytesPerMb >= long.MaxValue
            ? long.MaxValue
            : (long)(maxImageMb * bytesPerMb);
    }
    else
    {
        ContainerImageParser.MaxImageBytes = ContainerImageParser.DefaultMaxImageBytes;
    }

    // Issue #75: policy rules resolution — explicit --rules > input-adjacent
    // (.sbom-rules.yaml then .olaf-rules.yaml) > none. Schema/YAML errors
    // exit 2 BEFORE any report write (no partial report, no --out file).
    string? rulesFile = null;
    var fileRules = new RulesFile();
    if (rulesPath is not null)
    {
        if (!File.Exists(rulesPath))
        {
            Console.Error.WriteLine($"Rules file not found: '{rulesPath}'.");
            return 2;
        }

        rulesFile = rulesPath;
    }
    else
    {
        rulesFile = RulesLoader.FindAdjacentRulesFile(input, out var shadowed);
        if (shadowed)
        {
            LogVerbose("Using '.sbom-rules.yaml'; ignoring '.olaf-rules.yaml'.");
        }
    }

    if (rulesFile is not null)
    {
        string rulesText;
        try
        {
            rulesText = await File.ReadAllTextAsync(rulesFile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            Console.Error.WriteLine($"Failed to read rules file '{rulesFile}': {ex.Message}");
            return 2;
        }

        RulesFile? parsedRules;
        IReadOnlyList<string> ruleErrors;
        try
        {
            (parsedRules, ruleErrors) = RulesLoader.TryParse(rulesText, rulesFile);
        }
        catch (YamlException ex)
        {
            var line = ex.Start.Line <= 0 ? 1 : ex.Start.Line;
            var col = ex.Start.Column <= 0 ? 1 : ex.Start.Column;
            Console.Error.WriteLine($"{rulesFile}:{line}:{col}: {ex.Message}");
            return 2;
        }

        foreach (var error in ruleErrors)
        {
            Console.Error.WriteLine(error);
        }

        if (parsedRules is null)
        {
            return 2;
        }

        fileRules = parsedRules;
    }

    string? templateText = null;
    if (templatePath is not null)
    {
        if (!File.Exists(templatePath))
        {
            Console.Error.WriteLine($"Template not found: '{templatePath}'.");
            return 2;
        }

        try
        {
            templateText = await File.ReadAllTextAsync(templatePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            Console.Error.WriteLine($"Failed to read template '{templatePath}': {ex.Message}");
            return 2;
        }
    }

    ILicenseFormatter formatter;
    if (templateText is not null)
    {
        if (formatFlagPresent)
        {
            LogVerbose($"Template overrides --format '{format}'.");
        }

        formatter = new TemplateFormatter(templateText);
    }
    else
    {
        try
        {
            formatter = new FormatterRegistry().GetFormatter(format);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Console.Error.WriteLine($"Unsupported format '{format}'. Supported: {SupportedFormats}.");
            return 2;
        }
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
    catch (InvalidOperationException ex) when (allowEmpty && ex.Message.Contains("No manifests", StringComparison.Ordinal))
    {
        // Issue #76 generate-only: manifest-less input yields a valid empty
        // SBOM (exit 0) via the normal format path below. The legacy --input
        // path (allowEmpty: false) skips this arm and exits 2 below.
        dependencies = [];
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
    {
        // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
        Console.Error.WriteLine($"Failed to scan input '{input}': {ex.Message}");
        return 2;
    }

    LogVerbose($"Scanning '{input}'... found {dependencies.Count} dependencies.");

    using var http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    var cacheResolver = new CachingLicenseResolver(http, offline: offline);
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
                // Issue #77: offline skips the ClearlyDefined fallback at the
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
        var dep = dependencies[i];
        tasks[i] = ResolveWithFallbackAsync(dep, cancellationToken);
    }

    var results = await Task.WhenAll(tasks).ConfigureAwait(false);
    var resolved = new List<ResolvedLicense>(results);
    // Transitive filter (post-scan/pre-format): --direct-only keeps direct
    // dependencies only; neither flag or --include-transitive keeps all.
    // Strict/allow/deny gates below see the FILTERED set; ScanResult counts
    // recompute from the filtered set.
    if (directOnly)
    {
        resolved = resolved.Where(r => r.Dependency.Direct).ToList();
        LogVerbose($"Transitive filter (--direct-only): reporting {resolved.Count} of {results.Length} resolved licenses.");
    }

    // Issue #75: file excludeEcosystems filters the resolved list pre-gate
    // AND pre-report (same point as --direct-only); intersects --ecosystem.
    if (fileRules.ExcludeEcosystems.Count > 0)
    {
        var beforeExcludes = resolved.Count;
        resolved = resolved.Where(r => !IsExcludedEcosystem(r.Dependency.Ecosystem, fileRules.ExcludeEcosystems)).ToList();
        LogVerbose($"Ecosystem excludes ({string.Join(", ", fileRules.ExcludeEcosystems)}): reporting {resolved.Count} of {beforeExcludes} resolved licenses.");
    }

    foreach (var license in results)
    {
        LogVerbose($"Resolved {FormatDependency(license.Dependency)} -> {license.Status}");
    }

    var scanResult = new ScanResult(resolved);
    // Issue #74: grouping is display-only (post-scan/pre-format, post-filter
    // so counts are post --direct-only). Gates below see the flat filtered
    // list; formatter swap only. SBOM formats ignore with a verbose note.
    if (groupByLicense && templateText is null)
    {
        var formatKey = format.ToLowerInvariant();
        if (formatKey.Equals("txt", StringComparison.Ordinal))
        {
            formatter = new TxtFormatter(groupByLicense: true);
        }
        else if (formatKey.Equals("md", StringComparison.Ordinal) || formatKey.Equals("markdown", StringComparison.Ordinal))
        {
            formatter = new MarkdownFormatter(groupByLicense: true);
        }
        else if (formatKey.Equals("html", StringComparison.Ordinal))
        {
            formatter = new HtmlFormatter(groupByLicense: true);
        }
        else if (formatKey.Equals("cyclonedx-json", StringComparison.Ordinal)
            || formatKey.Equals("cyclonedx", StringComparison.Ordinal)
            || formatKey.Equals("cyclonedx-xml", StringComparison.Ordinal)
            || formatKey.Equals("spdx-json", StringComparison.Ordinal))
        {
            LogVerbose($"--group-by-license ignored for SBOM format '{format}'.");
        }
    }

    string output;
    try
    {
        output = formatter.FormatResult(scanResult);
    }
    catch (TemplateSyntaxException ex)
    {
        Console.Error.WriteLine($"template error line {ex.Line}: {ex.Message}");
        return 2;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
        Console.Error.WriteLine($"template error: {ex.Message}");
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

    // Issue #75: merged policy gate (flags > file > defaults, PER KEY).
    // Present flag REPLACES the file list for that key; absent flag keeps
    // the file value. --strict (token-present) forces failOnUnknown:true.
    var allowed = allowFlagPresent ? ParseSpdxSet(allowRaw) : new HashSet<string>(fileRules.Allow, StringComparer.OrdinalIgnoreCase);
    var denied = denyFlagPresent ? ParseSpdxSet(denyRaw) : new HashSet<string>(fileRules.Deny, StringComparer.OrdinalIgnoreCase);
    var hasAllow = allowed.Count > 0;
    var hasDeny = denied.Count > 0;
    var enforceUnknown = strictFlagPresent || fileRules.FailOnUnknown;
    var enforceUnresolved = fileRules.FailOnUnresolved;
    var policyEnforced = enforceUnknown || hasAllow || hasDeny;
    var today = RulesLoader.UtcToday();

    var offenderLines = new List<string>();
    foreach (var license in resolved)
    {
        var exception = RulesLoader.MatchException(license, fileRules.Exceptions);
        if (exception is not null)
        {
            if (RulesLoader.IsExceptionActive(exception, today))
            {
                Console.Error.WriteLine($"Suppressed {FormatOffender(license)} (exception: {exception.Reason}).");
                continue;
            }

            offenderLines.Add($"{FormatOffender(license)} (exception expired: {exception.Reason})");
            continue;
        }

        if (IsPolicyOffender(license, policyEnforced, allowed, denied)
            || (enforceUnresolved && RulesLoader.IsUnresolved(license)))
        {
            offenderLines.Add(FormatOffender(license));
        }
    }

    static string FormatDependency(Dependency dependency)
    {
        return $"{dependency.Ecosystem}:{dependency.Name}@{dependency.Version}";
    }

    static string FormatOffender(ResolvedLicense license)
    {
        return $"{FormatDependency(license.Dependency)} -> {LicenseDisplay.EffectiveSpdx(license)}";
    }

    static bool IsPolicyOffender(ResolvedLicense license, bool enforceUnknownGate, HashSet<string> allowedSet, HashSet<string> deniedSet)
    {
        var effective = LicenseDisplay.EffectiveSpdx(license);
        var isUnknown = license.Status.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        var hasAllowGate = allowedSet.Count > 0;
        var hasDenyGate = deniedSet.Count > 0;
        // Issue #75: explicit allow rescues — a license on the allow list is
        // never an offender even if it also appears on the deny list.
        // Allow-first order preserved (allow-miss still offenders first).
        if (hasAllowGate && allowedSet.Contains(effective))
        {
            return false;
        }

        if (hasAllowGate)
        {
            return true;
        }

        if (hasDenyGate && deniedSet.Contains(effective))
        {
            return true;
        }

        if (enforceUnknownGate && isUnknown)
        {
            return true;
        }

        return false;
    }

    // Default gate (no allow/deny): preserve exact strict message + exit 1.
    if (!hasAllow && !hasDeny)
    {
        if (offenderLines.Count == 0)
        {
            return 0;
        }

        Console.Error.WriteLine(strictFlagPresent
            ? "Strict mode: unknown licenses found."
            : "Policy gate: license violations found (--rules).");
        if (!strictFlagPresent || offenderLines.Any(l => l.Contains("(exception expired:", StringComparison.Ordinal)))
        {
            foreach (var line in offenderLines)
            {
                Console.Error.WriteLine(line);
            }

            Console.Error.WriteLine($"{offenderLines.Count} offender(s) found.");
        }

        return 1;
    }

    // Allow/deny gate (report already written above — report-write-first order).
    // The --allow/--deny warning fires only when flag tokens drive the gate;
    // file-driven gates declare intent in version control (no warning text).
    if (!strictFlagPresent && (allowFlagPresent || denyFlagPresent))
    {
        Console.Error.WriteLine("Warning: --allow/--deny without --strict; applying policy gate.");
    }

    if (offenderLines.Count > 0)
    {
        Console.Error.WriteLine("Strict mode: policy gate violations found.");
        foreach (var offender in offenderLines)
        {
            Console.Error.WriteLine(offender);
        }

        Console.Error.WriteLine($"{offenderLines.Count} offender(s) found.");
        return 1;
    }

    return 0;
}

ScanOpts ReadScanOpts(ParseResult parseResult, string? inputOverride, bool allowEmpty)
{
    return new ScanOpts(
        Input: inputOverride ?? parseResult.GetValue(inputOption),
        Format: parseResult.GetValue(formatOption) ?? "json",
        TemplatePath: parseResult.GetValue(templateOption),
        OutPath: parseResult.GetValue(outOption),
        Force: parseResult.GetValue(forceOption),
        StrictFlagPresent: parseResult.GetResult(strictOption)?.IdentifierToken is not null,
        Ecosystem: parseResult.GetValue(ecosystemOption),
        MaxImageMbRaw: parseResult.GetValue(maxImageMbOption),
        Verbose: parseResult.GetValue(verboseOption),
        Quiet: parseResult.GetValue(quietOption),
        AllowRaw: parseResult.GetValue(allowOption),
        DenyRaw: parseResult.GetValue(denyOption),
        DirectOnly: parseResult.GetValue(directOnlyOption),
        IncludeTransitive: parseResult.GetValue(includeTransitiveOption),
        GroupByLicense: parseResult.GetValue(groupByLicenseOption),
        RulesPath: parseResult.GetValue(rulesOption),
        AllowFlagPresent: parseResult.GetResult(allowOption)?.Tokens.Count > 0,
        DenyFlagPresent: parseResult.GetResult(denyOption)?.Tokens.Count > 0,
        FormatFlagPresent: parseResult.GetResult(formatOption)?.Tokens.Count > 0,
        AllowEmpty: allowEmpty,
        Offline: parseResult.GetValue(offlineOption));
}

rootCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    return await ScanAndReportAsync(ReadScanOpts(parseResult, null, allowEmpty: false), cancellationToken).ConfigureAwait(false);
});

var generateCommand = new Command("generate", """
    Scan a project path and write a license report (zero-config).
    Defaults: PATH "."; json report to stdout unless --out is given
    (file output reuses the same atomic-write path as the root command).

    Examples:
      olaf generate .
      olaf generate ./svc --format cyclonedx-json
      olaf generate . --out sbom
    """)
{
    pathArgument,
    inputOption,
    formatOption,
    templateOption,
    outOption,
    forceOption,
    strictOption,
    offlineOption,
    allowOption,
    denyOption,
    rulesOption,
    directOnlyOption,
    includeTransitiveOption,
    groupByLicenseOption,
    ecosystemOption,
    maxImageMbOption,
    verboseOption,
    quietOption,
};

generateCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var path = parseResult.GetValue(pathArgument) ?? ".";
    var input = parseResult.GetValue(inputOption);
    var resolvedInput = string.IsNullOrWhiteSpace(input) ? path : input;
    return await ScanAndReportAsync(ReadScanOpts(parseResult, resolvedInput, allowEmpty: true), cancellationToken).ConfigureAwait(false);
});

rootCommand.Subcommands.Add(generateCommand);

// System.CommandLine reports a trailing valueless option as a parse error
// (exit 1); the exit-code contract requires exit 2 for a bad --max-image-mb.
if (args.Length > 0 && string.Equals(args[^1], "--max-image-mb", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Invalid --max-image-mb: missing value; must be a positive number of megabytes.");
    return 2;
}

return await rootCommand.Parse(args).InvokeAsync();

// Issue #76: value object carrying every flag for ScanAndReportAsync.
// Presence booleans are computed per-command in ReadScanOpts so the shared
// core never touches ParseResult/Option instances.
internal sealed record ScanOpts(
    string? Input,
    string Format,
    string? TemplatePath,
    string? OutPath,
    bool Force,
    bool StrictFlagPresent,
    string? Ecosystem,
    string? MaxImageMbRaw,
    bool Verbose,
    bool Quiet,
    string? AllowRaw,
    string? DenyRaw,
    bool DirectOnly,
    bool IncludeTransitive,
    bool GroupByLicense,
    string? RulesPath,
    bool AllowFlagPresent,
    bool DenyFlagPresent,
    bool FormatFlagPresent,
    bool AllowEmpty,
    bool Offline = false);
