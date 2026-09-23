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

    [Fact]
    public void Should_Exit2_When_FormatUnsupported()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "toml");

        Assert.Equal(2, result.ExitCode);
    }
}
