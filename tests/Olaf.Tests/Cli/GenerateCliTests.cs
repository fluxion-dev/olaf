using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olaf.Tests.Cli;

/// <summary>
/// Issue #76: `generate [PATH]` subcommand (zero-config) end-to-end.
/// The CLI is invoked as a subprocess (same harness as CliTests) so tests
/// exercise real exit codes/stdout. Offline only: committed fixtures
/// (exact CanHandle filenames: package.json, requirements.txt) + temp dirs
/// + phantom/Unknown-tolerant asserts; never resolved-license strings.
/// Exit-code contract parity: beyond pinned 0/1/2 rows, tests assert
/// exit(generate X) == exit(root --input X) per arm.
/// </summary>
public sealed class GenerateCliTests
{
    private static CliResult RunCliInDir(string workDir, params string[] args)
    {
        var dll = CliTestHelpers.GetCliDllPath();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start CLI process.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(120_000))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException("CLI did not exit within 120s.");
        }

        return new CliResult(proc.ExitCode, stdout ?? string.Empty, stderr ?? string.Empty);
    }

    private static int JsonTotal(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32();
    }

    private static string CreateMonorepoFixture()
    {
        // Exact CanHandle filenames: package.json (npm), requirements.txt (pip).
        // Caller owns cleanup via DeleteTempDir (try/finally).
        var root = CliTestHelpers.CreateTempDir();
        var svcA = Path.Combine(root, "svc-a");
        var svcB = Path.Combine(root, "svc-b");
        Directory.CreateDirectory(svcA);
        Directory.CreateDirectory(svcB);
        File.Copy(CliTestHelpers.FixturePath("npm", "package.json"), Path.Combine(svcA, "package.json"));
        File.Copy(CliTestHelpers.FixturePath("pip", "requirements.txt"), Path.Combine(svcB, "requirements.txt"));
        return root;
    }

    private static string NormalizedCycloneDx(string stdout)
    {
        var node = JsonNode.Parse(stdout) ?? throw new InvalidOperationException("Empty CycloneDX output.");
        node["serialNumber"] = "NORMALIZED";
        node["metadata"]!["timestamp"] = "NORMALIZED";
        return node.ToJsonString();
    }

    [Fact]
    public void Should_OutputJsonToStdout_When_GeneratePositionalFile()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("generate", input, "--format", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        using var doc = JsonDocument.Parse(result.Stdout); // stdout must be JSON
        Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
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
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var input = CliTestHelpers.FixturePath("npm", "package.json");
            var outFile = Path.Combine(tempDir, "sbom.json");

            var result = CliTestHelpers.RunCli("generate", input, "--format", "json", "--out", outFile);

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

            var result = RunCliInDir(tempDir, "generate", "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_MatchRootStdout_When_GenerateVsInputJson()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var root = CliTestHelpers.RunCli("--input", input, "--format", "json");
        var generate = CliTestHelpers.RunCli("generate", input, "--format", "json");

        Assert.Equal(0, root.ExitCode);
        Assert.Equal(root.ExitCode, generate.ExitCode);
        Assert.Equal(root.Stdout, generate.Stdout); // byte-identical (json has no per-run fields)
    }

    [Fact]
    public void Should_MatchRootStdout_When_GenerateVsInputCycloneDxJson()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var root = CliTestHelpers.RunCli("--input", input, "--format", "cyclonedx-json");
        var generate = CliTestHelpers.RunCli("generate", input, "--format", "cyclonedx-json");

        Assert.Equal(0, root.ExitCode);
        Assert.Equal(root.ExitCode, generate.ExitCode);
        // serialNumber + timestamp are per-run; normalize, then byte-compare.
        Assert.Equal(NormalizedCycloneDx(root.Stdout), NormalizedCycloneDx(generate.Stdout));
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
            Assert.Equal(0, JsonTotal(result.Stdout));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_ExitZeroWithEmptyEnvelope_When_GenerateEmptyDirSpdxJson_And_LegacyInputEmptyStaysExit2()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var generate = CliTestHelpers.RunCli("generate", tempDir, "--format", "spdx-json");

            Assert.Equal(0, generate.ExitCode);
            using var doc = JsonDocument.Parse(generate.Stdout); // valid envelope
            Assert.Contains("\"packages\":[]", generate.Stdout, StringComparison.Ordinal);

            var legacy = CliTestHelpers.RunCli("--input", tempDir, "--format", "json");

            Assert.Equal(2, legacy.ExitCode); // legacy --input <emptydir> stays exit 2
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_MatchRootExit_When_StrictAllowDenyOnBothPaths()
    {
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            // Strict gate: phantom dep is always Unknown (404 or offline
            // cache-miss) -> both paths exit 1.
            var rootStrict = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--strict");
            var generateStrict = CliTestHelpers.RunCli("generate", fixtureDir, "--format", "json", "--strict");
            Assert.Equal(1, rootStrict.ExitCode);
            Assert.Equal(rootStrict.ExitCode, generateStrict.ExitCode);

            // Explicit allow rescues Unknown on both paths -> both exit 0.
            var rootAllow = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--allow", "Unknown");
            var generateAllow = CliTestHelpers.RunCli("generate", fixtureDir, "--format", "json", "--allow", "Unknown");
            Assert.Equal(rootAllow.ExitCode, generateAllow.ExitCode);
            Assert.Equal(0, generateAllow.ExitCode);

            // Deny Unknown fails on both paths -> both exit 1.
            var rootDeny = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--deny", "Unknown");
            var generateDeny = CliTestHelpers.RunCli("generate", fixtureDir, "--format", "json", "--deny", "Unknown");
            Assert.Equal(rootDeny.ExitCode, generateDeny.ExitCode);
            Assert.Equal(1, generateDeny.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    [Fact]
    public void Should_HonorFormatFlag_When_GenerateTxt()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("generate", input, "--format", "txt");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Total:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("express@", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ShowHelpWithFlagsAndExamples_When_GenerateHelp_And_RootHelpUntouched()
    {
        var generate = CliTestHelpers.RunCli("generate", "--help");

        Assert.Equal(0, generate.ExitCode);
        Assert.Contains("--format", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("--strict", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("--allow", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("olaf generate .", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("olaf generate ./svc --format cyclonedx-json", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("olaf generate . --out sbom", generate.Stdout, StringComparison.Ordinal);

        var root = CliTestHelpers.RunCli("--help");

        Assert.Equal(0, root.ExitCode);
        Assert.Contains("olaf --input package.json", root.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ReportSumOfParts_When_GenerateMonorepoRoot()
    {
        var root = CreateMonorepoFixture();
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
            Assert.Equal(JsonTotal(partA.Stdout) + JsonTotal(partB.Stdout), JsonTotal(whole.Stdout));
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
            var input = CliTestHelpers.FixturePath("npm", "package.json");
            var outFile = Path.Combine(tempDir, "sbom.json");

            var result = CliTestHelpers.RunCli("generate", input, "--format", "bogus", "--out", outFile);

            Assert.Equal(2, result.ExitCode);
            Assert.False(File.Exists(outFile)); // no partial write
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
    public void Should_Exit2_When_GenerateBadEcosystem_And_MatchRootExit()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var generate = CliTestHelpers.RunCli("generate", input, "--format", "json", "--ecosystem", "bogus");
        var root = CliTestHelpers.RunCli("--input", input, "--format", "json", "--ecosystem", "bogus");

        Assert.Equal(2, generate.ExitCode);
        Assert.Equal(root.ExitCode, generate.ExitCode);
        Assert.Contains("Supported:", generate.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    public void Should_Exit2_When_GenerateMaxImageMbInvalid(string value)
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var generate = CliTestHelpers.RunCli("generate", input, "--format", "json", "--max-image-mb", value);
        var root = CliTestHelpers.RunCli("--input", input, "--format", "json", "--max-image-mb", value);

        Assert.Equal(2, generate.ExitCode);
        Assert.Equal(root.ExitCode, generate.ExitCode);
        Assert.Contains("--max-image-mb", generate.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Exit2_When_GenerateMaxImageMbMissingValue()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var generate = CliTestHelpers.RunCli("generate", input, "--format", "json", "--max-image-mb");
        var root = CliTestHelpers.RunCli("--input", input, "--format", "json", "--max-image-mb");

        Assert.Equal(2, generate.ExitCode);
        Assert.Equal(root.ExitCode, generate.ExitCode);
        Assert.Contains("--max-image-mb", generate.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Exit2WithoutOverwrite_When_GenerateOutExists_And_ForceOverwrites()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var input = CliTestHelpers.FixturePath("npm", "package.json");
            var outFile = Path.Combine(tempDir, "sbom.json");
            File.WriteAllText(outFile, "sentinel");

            var blocked = CliTestHelpers.RunCli("generate", input, "--format", "json", "--out", outFile);

            Assert.Equal(2, blocked.ExitCode);
            Assert.Equal("sentinel", File.ReadAllText(outFile)); // no partial overwrite

            var forced = CliTestHelpers.RunCli("generate", input, "--format", "json", "--out", outFile, "--force");

            Assert.Equal(0, forced.ExitCode);
            Assert.Contains("express", File.ReadAllText(outFile), StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_PreferInputFlag_When_GeneratePathAndInputBothGiven()
    {
        // --input overrides the PATH positional (no conflict error): scanning
        // an empty dir with --input <npm fixture> yields the fixture's deps.
        var emptyDir = CliTestHelpers.CreateTempDir();
        try
        {
            var input = CliTestHelpers.FixturePath("npm", "package.json");

            var result = CliTestHelpers.RunCli("generate", emptyDir, "--input", input, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
            Assert.NotEqual(0, JsonTotal(result.Stdout)); // fixture won, not the empty PATH
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(emptyDir);
        }
    }

    [Fact]
    public void Should_ContainBothEcosystems_When_GenerateMonorepoRoot()
    {
        // Strengthens the sum-of-parts pin: the whole-tree report must name
        // deps from EACH subtree ecosystem (npm express + pip requests).
        var root = CreateMonorepoFixture();
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
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var input = CliTestHelpers.FixturePath("npm", "package.json");
            var outFile = Path.Combine(tempDir, "sbom.json");

            var stdoutRun = CliTestHelpers.RunCli("generate", input, "--format", "json");
            var fileRun = CliTestHelpers.RunCli("generate", input, "--format", "json", "--out", outFile);

            Assert.Equal(0, stdoutRun.ExitCode);
            Assert.Equal(0, fileRun.ExitCode);
            Assert.True(string.IsNullOrEmpty(fileRun.Stdout)); // report -> file only
            Assert.Equal(stdoutRun.Stdout, File.ReadAllText(outFile));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }
}
