using System.Diagnostics;
using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// TDD Red: CLI end-to-end. The CLI is invoked as a subprocess
/// (`dotnet &lt;Olaf.Cli.dll&gt; args`) so no compile-time reference to the
/// executable project is needed and the tests exercise real exit codes/stdout.
/// Inputs reuse tests/Olaf.Tests/Fixtures (local files + temp dirs only —
/// no live network). Exit-code contract: 0 success, 1 --strict violation,
/// 2 usage/IO errors (missing input, unsupported format, --out exists w/o --force).
/// NOTE: the future implementation must resolve the strict-fixture package
/// deterministically offline (unknown name guarantees Unknown via 404 or
/// offline cache-miss) — these tests must never depend on live registries.
/// </summary>
internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);

internal static class CliTestHelpers
{
    internal static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(dir);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "olaf.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new NotImplementedException("Repo root (olaf.slnx) not found.");
    }

    internal static string FixturePath(params string[] segments)
    {
        return Path.Combine(new[] { RepoRoot(), "tests", "Olaf.Tests", "Fixtures" }.Concat(segments).ToArray());
    }

    internal static string GetCliDllPath()
    {
        var root = RepoRoot();
        var debug = Path.Combine(root, "src", "Olaf.Cli", "bin", "Debug", "net10.0", "Olaf.Cli.dll");
        if (File.Exists(debug))
        {
            return debug;
        }

        var release = Path.Combine(root, "src", "Olaf.Cli", "bin", "Release", "net10.0", "Olaf.Cli.dll");
        if (File.Exists(release))
        {
            return release;
        }

        throw new NotImplementedException("Olaf.Cli.dll not built (missing CLI implementation).");
    }

    internal static CliResult RunCli(params string[] args)
    {
        var dll = GetCliDllPath();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
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

    internal static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "olaf-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Manifest whose single dependency can never resolve (unknown name), so
    /// --strict deterministically exits 1 whether resolvers go over the
    /// network (404) or stay offline (cache-miss) — no live network required.
    /// </summary>
    internal static string CreateStrictFixtureDir()
    {
        var dir = CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "package.json"),
            """{"name":"olaf-strict-fixture","version":"1.0.0","dependencies":{"this-package-definitely-does-not-exist-olaf-xyz":"9.9.9"}}""");
        return dir;
    }

    internal static void DeleteTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }
}

public sealed class CliEndToEndTests
{
    [Fact]
    public void Should_OutputJsonToStdout_When_InputIsFileAndFormatJson()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        using var doc = JsonDocument.Parse(result.Stdout); // stdout must be JSON
        Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_OutputTxtToStdout_When_FormatTxt()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "txt");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        Assert.Contains("Total:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("express@", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_OutputMarkdownToStdout_When_FormatMd()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "md");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        Assert.Contains("# Third-Party Attribution", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_OutputMarkdownToStdout_When_FormatMarkdownAlias()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "markdown");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        Assert.Contains("# Third-Party Attribution", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Should_ExitZero_When_InputIsFileOrDirectory(bool useDirectory)
    {
        string? tempDir = null;
        try
        {
            string input;
            if (useDirectory)
            {
                tempDir = CliTestHelpers.CreateTempDir();
                File.Copy(CliTestHelpers.FixturePath("npm", "package.json"), Path.Combine(tempDir, "package.json"));
                input = tempDir;
            }
            else
            {
                input = CliTestHelpers.FixturePath("npm", "package.json");
            }

            var result = CliTestHelpers.RunCli("--input", input, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            if (tempDir is not null)
            {
                CliTestHelpers.DeleteTempDir(tempDir);
            }
        }
    }

    [Fact]
    public void Should_WriteOutputFile_When_OutSpecified()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var input = CliTestHelpers.FixturePath("npm", "package.json");
            var outFile = Path.Combine(tempDir, "out.json");

            var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--out", outFile);

            Assert.Equal(0, result.ExitCode);
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
    public void Should_Exit2_When_OutExistsWithoutForce()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var input = CliTestHelpers.FixturePath("npm", "package.json");
            var outFile = Path.Combine(tempDir, "out.json");
            File.WriteAllText(outFile, "sentinel");

            var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--out", outFile);

            Assert.Equal(2, result.ExitCode);
            Assert.Equal("sentinel", File.ReadAllText(outFile)); // must not overwrite
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_Overwrite_When_ForceSpecified()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var input = CliTestHelpers.FixturePath("npm", "package.json");
            var outFile = Path.Combine(tempDir, "out.json");
            File.WriteAllText(outFile, "sentinel");

            var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--out", outFile, "--force");

            Assert.Equal(0, result.ExitCode);
            var content = File.ReadAllText(outFile);
            Assert.NotEqual("sentinel", content);
            Assert.Contains("express", content, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void Should_Exit1_When_StrictAndUnknownLicenses()
    {
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--strict");

            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    [Fact]
    public void Should_Exit0_When_NotStrictAndUnknownLicenses()
    {
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    [Fact]
    public void Should_Exit2_When_InputFileMissing()
    {
        var missing = Path.Combine(CliTestHelpers.CreateTempDir(), "does-not-exist.json");

        var result = CliTestHelpers.RunCli("--input", missing, "--format", "json");

        Assert.Equal(2, result.ExitCode);
    }

    /// <summary>
    /// Policy gate (--allow/--deny, issue #5): all cases use the phantom
    /// fixture whose single dependency never resolves, so the effective SPDX
    /// is always "Unknown" whether resolvers go over the network (404) or
    /// stay offline (cache-miss) — no live network required.
    /// Implemented behavior (verified via dotnet run): --allow/--deny imply
    /// the gate even without --strict (with a warning on stderr); an
    /// allow-list containing "Unknown" exempts unknown licenses.
    /// QA note: enforcing without --strict goes beyond a strict-only reading
    /// of the issue — flag if the spec intended --strict to be required.
    /// </summary>
    [Theory]
    [InlineData(true, "MIT", null, 1)] // allow-list excludes Unknown -> violation
    [InlineData(true, "Unknown", null, 0)] // Unknown explicitly allowed
    [InlineData(true, "MIT,Unknown", null, 0)] // multi-entry allow-list
    [InlineData(true, null, "Unknown", 1)] // deny-list hits Unknown
    [InlineData(false, null, "Unknown", 1)] // enforced without --strict
    [InlineData(false, "Unknown", null, 0)] // allow passes without --strict
    [InlineData(true, null, "MIT", 1)] // deny misses, Unknown-fallback still fails
    public void Should_PolicyGate_When_AllowDeny(bool strict, string? allow, string? deny, int expectedExit)
    {
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var args = new List<string> { "--input", fixtureDir, "--format", "json" };
            if (strict)
            {
                args.Add("--strict");
            }

            if (allow is not null)
            {
                args.Add("--allow");
                args.Add(allow);
            }

            if (deny is not null)
            {
                args.Add("--deny");
                args.Add(deny);
            }

            var result = CliTestHelpers.RunCli([.. args]);

            Assert.Equal(expectedExit, result.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    [Fact]
    public void Should_PolicyGate_DenyPresentSpdx_OnNpmFixture()
    {
        // MIT is the SPDX the npm fixture would resolve to; --deny MIT fails
        // (exit 1) whether the sandbox resolves it (direct deny match) or
        // stays Unknown (unknown-fallback enforcement) — deterministic either
        // way. Stderr must name the offender, not just the count.
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--deny", "MIT");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("express", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("policy gate violations", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("offender(s) found", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_PolicyGate_Warn_When_AllowDenyWithoutStrict()
    {
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--deny", "Unknown");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Warning: --allow/--deny without --strict", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    [Fact]
    public void Should_PolicyGate_ReportOffenderDetails_InStderr()
    {
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--strict", "--deny", "Unknown");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("this-package-definitely-does-not-exist-olaf-xyz", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("-> Unknown", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("1 offender(s) found", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    [Fact]
    public void Should_PolicyGate_TrimAndCaseInsensitiveCsv()
    {
        // " mit , Unknown " must parse as {MIT, Unknown} -> Unknown allowed -> 0.
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--strict", "--allow", " mit , Unknown ");

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    [Fact]
    public void Should_PolicyGate_EmptyAllow_FallsBackToStrictGate()
    {
        // Empty --allow carries no entries, so the default strict message/contract applies.
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--strict", "--allow", string.Empty);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Strict mode: unknown licenses found.", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    [Fact]
    public void Should_Exit2_When_FormatUnsupported()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "toml");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Should_Help_DocumentsAllFlagsAndExamples()
    {
        var result = CliTestHelpers.RunCli("--help");

        Assert.Equal(0, result.ExitCode);
        var combined = result.Stdout + result.Stderr;
        Assert.Contains("--input", combined, StringComparison.Ordinal);
        Assert.Contains("--format", combined, StringComparison.Ordinal);
        Assert.Contains("--out", combined, StringComparison.Ordinal);
        Assert.Contains("--force", combined, StringComparison.Ordinal);
        Assert.Contains("--strict", combined, StringComparison.Ordinal);
        Assert.Contains("--allow", combined, StringComparison.Ordinal);
        Assert.Contains("--deny", combined, StringComparison.Ordinal);
        Assert.Contains("--ecosystem", combined, StringComparison.Ordinal);
        Assert.Contains("--verbose", combined, StringComparison.Ordinal);
        Assert.Contains("--quiet", combined, StringComparison.Ordinal);
        Assert.Contains("--version", combined, StringComparison.Ordinal);
        Assert.Contains("Examples:", combined, StringComparison.Ordinal);
        Assert.Contains("olaf --input", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Version_PrintsVersionAndExitsZero()
    {
        var result = CliTestHelpers.RunCli("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        Assert.Contains(".", result.Stdout.Trim(), StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(result.Stderr));
    }

    [Fact]
    public void Should_Exit2_When_EcosystemUnsupported()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--ecosystem", "maven");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Supported:", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_CreateParentDirs_When_OutNestedMissing()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var input = CliTestHelpers.FixturePath("npm", "package.json");
            var outFile = Path.Combine(tempDir, "a", "b", "out.json");

            var result = CliTestHelpers.RunCli("--input", input, "--out", outFile);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(outFile));
            var content = File.ReadAllText(outFile);
            using var doc = JsonDocument.Parse(content);
            Assert.True(string.IsNullOrWhiteSpace(result.Stdout));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }
}
