using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olaf.Tests.Cli;

/// <summary>
/// Issue #123: `generate [PATH]` subcommand (zero-config) end-to-end.
/// The CLI is invoked as a subprocess (same harness as CliTests) so tests
/// exercise real exit codes/stdout. Offline only: committed fixtures copied
/// into temp dirs (generate takes a DIRECTORY — single-file input is exit 2)
/// + phantom/Unknown-tolerant asserts; never resolved-license strings.
/// Exit-code contract: 0 success (including manifest-less empty report),
/// 1 --strict gate on Unknown, 2 usage/scan/format/write errors.
/// </summary>
public sealed class GenerateCliTests
{
    private static string CreateNpmFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.Copy(CliTestHelpers.FixturePath("npm", "package.json"), Path.Combine(dir, "package.json"));
        return dir;
    }

    [Fact]
    public void Should_OutputJsonToStdout_When_GeneratePositionalDirectory()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            File.Copy(CliTestHelpers.FixturePath("npm", "package.json"), Path.Combine(tempDir, "package.json"));

            var result = CliTestHelpers.RunCli("generate", tempDir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_WriteOutputFile_When_GenerateOutSpecified()
    {
        var tempDir = CreateNpmFixtureDir();
        try
        {
            var outFile = Path.Combine(tempDir, "sbom.json");

            var result = CliTestHelpers.RunCli("generate", tempDir, "--format", "json", "--out", outFile);

            Assert.Equal(0, result.ExitCode);
            Assert.True(string.IsNullOrEmpty(result.Stdout)); // report -> file only
            Assert.True(File.Exists(outFile));
            var content = File.ReadAllText(outFile);
            using var doc = JsonDocument.Parse(content);
            Assert.Contains("express", content, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_ScanCurrentDirectory_When_GeneratePathOmitted()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            File.Copy(CliTestHelpers.FixturePath("npm", "package.json"), Path.Combine(tempDir, "package.json"));

            var result = CliTestHelpers.RunCli(workDir: tempDir, args: ["generate", "--format", "json"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_ExitZeroWithEmptyEnvelope_When_GenerateEmptyDirJson()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", tempDir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            using var doc = JsonDocument.Parse(result.Stdout); // valid envelope
            Assert.Equal(0, CliTestHelpers.JsonTotal(result.Stdout));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_ExitZeroWithEmptyEnvelope_When_GenerateEmptyDirSpdxJson()
    {
        // Manifest-less dirs yield a valid empty report (exit 0) — the
        // generate-only behavior for missing manifests.
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var generate = CliTestHelpers.RunCli("generate", tempDir, "--format", "spdx-json");

            Assert.Equal(0, generate.ExitCode);
            using var doc = JsonDocument.Parse(generate.Stdout); // valid envelope
            Assert.Contains("\"packages\":[]", generate.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_ExitZeroWithEmptyEnvelope_When_GenerateMalformedManifest()
    {
        // Malformed manifests are swallowed by the parsers (never throw), so
        // a dir whose only manifest is corrupt reports an empty envelope,
        // not an error.
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "package.json"), "{not valid json");

            var result = CliTestHelpers.RunCli("generate", tempDir, "--format", "json", "--offline");

            Assert.Equal(0, result.ExitCode);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal(0, CliTestHelpers.JsonTotal(result.Stdout));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("xml")]
    [InlineData("md")]
    [InlineData("cyclonedx-json")]
    [InlineData("cyclonedx-xml")]
    [InlineData("spdx-json")]
    public void Should_ExitZeroWithReport_When_GenerateKeptFormat(string format)
    {
        // Every canonical format (7, zero aliases) renders the npm fixture
        // dir to stdout with exit 0.
        var tempDir = CreateNpmFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", tempDir, "--format", format, "--offline");

            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_ShowHelpWithFlagsAndExamples_When_GenerateHelp()
    {
        var generate = CliTestHelpers.RunCli("generate", "--help");

        Assert.Equal(0, generate.ExitCode);
        Assert.Contains("--format", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("--out", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("--force", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("--strict", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("--offline", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("olaf generate .", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("olaf generate ./svc --format cyclonedx-json", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("olaf generate . --out sbom", generate.Stdout, StringComparison.Ordinal);

        var root = CliTestHelpers.RunCli("--help");

        Assert.Equal(0, root.ExitCode);
        Assert.Contains("generate", root.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ReportSumOfParts_When_GenerateMonorepoRoot()
    {
        var root = CliTestHelpers.CreateMixedNpmPipFixtureDir();
        try
        {
            var svcA = Path.Combine(root, "svc-a");
            var svcB = Path.Combine(root, "svc-b");

            var whole = CliTestHelpers.RunCli("generate", root, "--format", "json");
            var partA = CliTestHelpers.RunCli("generate", svcA, "--format", "json");
            var partB = CliTestHelpers.RunCli("generate", svcB, "--format", "json");

            Assert.Equal(0, whole.ExitCode);
            Assert.Equal(0, partA.ExitCode);
            Assert.Equal(0, partB.ExitCode);
            Assert.Equal(CliTestHelpers.JsonTotal(partA.Stdout) + CliTestHelpers.JsonTotal(partB.Stdout), CliTestHelpers.JsonTotal(whole.Stdout));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(root);
        }
    }

    [Fact]
    public void Should_Exit2WithoutPartialWrite_When_GenerateBadFormat()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var input = CreateNpmFixtureDir();
            try
            {
                var outFile = Path.Combine(tempDir, "sbom.json");

                var result = CliTestHelpers.RunCli("generate", input, "--format", "bogus", "--out", outFile);

                Assert.Equal(2, result.ExitCode);
                Assert.False(File.Exists(outFile)); // no partial write
            }
            finally
            {
                CliTestHelpers.DeleteTempDir(input);
            }
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_Exit2_When_GenerateMissingInput()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var missing = Path.Combine(tempDir, "does-not-exist");

            var result = CliTestHelpers.RunCli("generate", missing, "--format", "json");

            Assert.Equal(2, result.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_Exit2_When_GenerateSingleFile()
    {
        // Generate takes a project directory: a manifest FILE path is a
        // usage error naming single-file rejection (exit 2), not a scan.
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("generate", input, "--format", "json");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Single-file scan is not supported", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Exit2WithoutOverwrite_When_GenerateOutExists_And_ForceOverwrites()
    {
        var tempDir = CreateNpmFixtureDir();
        try
        {
            var outFile = Path.Combine(tempDir, "sbom.json");
            File.WriteAllText(outFile, "sentinel");

            var blocked = CliTestHelpers.RunCli("generate", tempDir, "--format", "json", "--out", outFile);

            Assert.Equal(2, blocked.ExitCode);
            Assert.Equal("sentinel", File.ReadAllText(outFile)); // no partial overwrite

            var forced = CliTestHelpers.RunCli("generate", tempDir, "--format", "json", "--out", outFile, "--force");

            Assert.Equal(0, forced.ExitCode);
            Assert.Contains("express", File.ReadAllText(outFile), StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_ContainBothEcosystems_When_GenerateMonorepoRoot()
    {
        // Strengthens the sum-of-parts pin: the whole-tree report must name
        // deps from EACH subtree ecosystem (npm express + pip requests).
        var root = CliTestHelpers.CreateMixedNpmPipFixtureDir();
        try
        {
            var whole = CliTestHelpers.RunCli("generate", root, "--format", "json");

            Assert.Equal(0, whole.ExitCode);
            Assert.Contains("express", whole.Stdout, StringComparison.Ordinal);
            Assert.Contains("requests", whole.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(root);
        }
    }

    [Fact]
    public void Should_MatchStdoutBytes_When_GenerateOutFileVsStdout()
    {
        // R2 sidecar pin: --out file content is byte-identical to the stdout run.
        var outer = CliTestHelpers.CreateTempDir();
        var input = CreateNpmFixtureDir();
        try
        {
            var outFile = Path.Combine(outer, "sbom.json");

            var stdoutRun = CliTestHelpers.RunCli("generate", input, "--format", "json");
            var fileRun = CliTestHelpers.RunCli("generate", input, "--format", "json", "--out", outFile);

            Assert.Equal(0, stdoutRun.ExitCode);
            Assert.Equal(0, fileRun.ExitCode);
            Assert.True(string.IsNullOrEmpty(fileRun.Stdout)); // report -> file only
            Assert.Equal(stdoutRun.Stdout, File.ReadAllText(outFile));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(input);
            CliTestHelpers.DeleteTempDir(outer);
        }
    }

    [Fact]
    public void Should_Exit2_When_BarePositionalWithoutSubcommand()
    {
        // Bare-positional rejection pin: a positional path with no subcommand
        // is not routed to generate — the parser rejects it with exit 2.
        // Users must spell `generate .` explicitly. Offline, no fixture.
        var result = CliTestHelpers.RunCli(".");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Unrecognized command", result.Stderr, StringComparison.Ordinal);
    }
}
