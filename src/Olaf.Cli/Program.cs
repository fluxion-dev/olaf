using System.CommandLine;
using Olaf.Cli;

// Issue #123: single-purpose `olaf generate <DIR> --format <fmt>`.
// This file is wiring-only: options, the generate subcommand, and the
// exit-code contract (0 ok, 1 strict-gate, 2 usage/scan/format/write).
// Scan+report lives in ScanRunner. Parse errors (unknown options,
// missing values) map to exit 2.
var formatOption = new Option<string>("--format")
{
    Description = $"Output format: {ScanRunner.SupportedFormats} (default: json)",
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
    Description = "Fail (exit 1) on unknown licenses",
};
var offlineOption = new Option<bool>("--offline")
{
    Description = "Resolve licenses from the embedded offline DB only (no network; unknown licenses stay Unknown)",
};

var pathArgument = new Argument<string>("PATH")
{
    Description = "Project directory to scan (default: current directory)",
    DefaultValueFactory = _ => ".",
};

var generateCommand = new Command("generate", """
    Scan a project directory and write a license report (zero-config).
    Defaults: PATH "."; json report to stdout unless --out is given.

    Examples:
      olaf generate .
      olaf generate ./svc --format cyclonedx-json
      olaf generate . --out sbom.json --format spdx-json
      olaf generate ./svc --strict --offline
    """)
{
    pathArgument,
    formatOption,
    outOption,
    forceOption,
    strictOption,
    offlineOption,
};

generateCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var runner = new ScanRunner();
    return await runner.GenerateAsync(
        parseResult.GetValue(pathArgument) ?? ".",
        parseResult.GetValue(formatOption) ?? "json",
        parseResult.GetValue(outOption),
        parseResult.GetValue(forceOption),
        parseResult.GetValue(strictOption),
        parseResult.GetValue(offlineOption),
        cancellationToken).ConfigureAwait(false);
});

var rootCommand = new RootCommand("""
    olaf license scanner
    Scans a project directory, resolves licenses, and writes a report to stdout or a file.

    Usage: olaf generate <DIR> [--format <fmt>] [--out <file>] [--force] [--strict] [--offline]
    """)
{
    generateCommand,
};

rootCommand.SetAction((ParseResult _, CancellationToken _) =>
{
    Console.Error.WriteLine("Use: olaf generate <DIR> [--format <fmt>] [--out <file>] [--force] [--strict] [--offline]");
    return Task.FromResult(2);
});

var parseResult = rootCommand.Parse(args);
if (parseResult.Errors.Count > 0)
{
    foreach (var error in parseResult.Errors)
    {
        Console.Error.WriteLine(error.Message);
    }

    return 2;
}

return await parseResult.InvokeAsync();
