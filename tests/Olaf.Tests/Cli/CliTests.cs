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

    // Params-only overload preserves every existing call site unchanged: a
    // single string argument never converts to string[] and longer argument
    // lists exceed the workDir overload's arity, so none of those calls can
    // bind to it. Directory-scoped runs pass args as an array (named).
    internal static CliResult RunCli(params string[] args) => RunCliCore(null, args);

    internal static CliResult RunCli(string? workDir, string[] args) => RunCliCore(workDir, args);

    private static CliResult RunCliCore(string? workDir, string[] args)
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
        if (workDir is not null)
        {
            psi.WorkingDirectory = workDir;
        }

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

    /// <summary>
    /// Mixed npm+pip monorepo fixture shared by the generate CLI tests and
    /// the cross-ecosystem CLI test: svc-a/package.json + svc-b/requirements.txt
    /// (exact CanHandle filenames). Null contents copy the committed fixtures
    /// (offline); callers may pass phantom contents for deterministic
    /// Unknown-tolerant asserts. Caller owns cleanup via DeleteTempDir
    /// (try/finally).
    /// </summary>
    internal static string CreateMixedNpmPipFixtureDir(string? packageJsonContent = null, string? requirementsContent = null)
    {
        var root = CreateTempDir();
        var svcA = Path.Combine(root, "svc-a");
        var svcB = Path.Combine(root, "svc-b");
        Directory.CreateDirectory(svcA);
        Directory.CreateDirectory(svcB);
        if (packageJsonContent is null)
        {
            File.Copy(FixturePath("npm", "package.json"), Path.Combine(svcA, "package.json"));
        }
        else
        {
            File.WriteAllText(Path.Combine(svcA, "package.json"), packageJsonContent);
        }

        if (requirementsContent is null)
        {
            File.Copy(FixturePath("pip", "requirements.txt"), Path.Combine(svcB, "requirements.txt"));
        }
        else
        {
            File.WriteAllText(Path.Combine(svcB, "requirements.txt"), requirementsContent);
        }

        return root;
    }

    internal static int JsonTotal(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32();
    }
}
public sealed class CliEndToEndTests
{
    // Issue #123: generate-only contract. Subprocess e2e on fixture DIRS +
    // temp dirs only — generate takes a project directory (single-file input
    // is exit 2, pinned below). No live network: strict arms use phantom
    // fixtures whose deps stay Unknown via 404 or offline cache-miss; exit-0
    // arms pin names from parsing, never resolved licenses.
    // Exit-code contract: 0 success (including manifest-less empty report),
    // 1 --strict gate on Unknown, 2 usage/scan/format/write errors.
    private static string CreateNpmFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.Copy(CliTestHelpers.FixturePath("npm", "package.json"), Path.Combine(dir, "package.json"));
        return dir;
    }

    [Fact]
    public void Should_OutputJsonToStdout_When_GenerateDirectory()
    {
        var dir = CreateNpmFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
            using var doc = JsonDocument.Parse(result.Stdout); // stdout must be JSON
            Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_OutputMarkdownToStdout_When_GenerateMd()
    {
        var dir = CreateNpmFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "md");

            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
            Assert.Contains("# Third-Party Attribution", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("express", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_WriteOutputFile_When_GenerateOutSpecified()
    {
        var dir = CreateNpmFixtureDir();
        try
        {
            var outFile = Path.Combine(dir, "out.json");

            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json", "--out", outFile);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(outFile));
            var content = File.ReadAllText(outFile);
            using var doc = JsonDocument.Parse(content);
            Assert.Contains("express", content, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_Exit2_When_GenerateOutExistsWithoutForce()
    {
        var dir = CreateNpmFixtureDir();
        try
        {
            var outFile = Path.Combine(dir, "out.json");
            File.WriteAllText(outFile, "sentinel");

            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json", "--out", outFile);

            Assert.Equal(2, result.ExitCode);
            Assert.Equal("sentinel", File.ReadAllText(outFile)); // must not overwrite
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_Overwrite_When_GenerateForceSpecified()
    {
        var dir = CreateNpmFixtureDir();
        try
        {
            var outFile = Path.Combine(dir, "out.json");
            File.WriteAllText(outFile, "sentinel");

            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json", "--out", outFile, "--force");

            Assert.Equal(0, result.ExitCode);
            var content = File.ReadAllText(outFile);
            Assert.NotEqual("sentinel", content);
            Assert.Contains("express", content, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_Exit1_When_GenerateStrictAndUnknownLicenses()
    {
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", fixtureDir, "--format", "json", "--strict");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Strict mode: unknown licenses found.", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    [Fact]
    public void Should_Exit0_When_GenerateNotStrictAndUnknownLicenses()
    {
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", fixtureDir, "--format", "json");

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
    public void Should_Exit2_When_GeneratePathMissing()
    {
        var missing = Path.Combine(CliTestHelpers.CreateTempDir(), "does-not-exist");

        var result = CliTestHelpers.RunCli("generate", missing, "--format", "json");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Should_Exit2WithSupportedList_When_GenerateFormatUnsupported()
    {
        var dir = CreateNpmFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "toml");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("Supported:", result.Stderr, StringComparison.Ordinal);
            foreach (var format in new[] { "json", "yaml", "xml", "md", "cyclonedx-json", "cyclonedx-xml", "spdx-json" })
            {
                Assert.Contains(format, result.Stderr, StringComparison.Ordinal);
            }
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Theory]
    [InlineData("txt")]
    [InlineData("html")]
    [InlineData("markdown")]
    [InlineData("cyclonedx")]
    public void Should_Exit2WithSupportedList_When_GenerateDeletedFormat(string format)
    {
        // Issue #123: txt/html retired, markdown + cyclonedx aliases removed —
        // all four are usage errors naming the 7-format canonical list.
        var dir = CreateNpmFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", format);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("Supported:", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_ShowGenerateHelp_When_GenerateHelp()
    {
        var generate = CliTestHelpers.RunCli("generate", "--help");

        Assert.Equal(0, generate.ExitCode);
        Assert.Contains("--format", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("--out", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("--force", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("--strict", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("--offline", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("json|yaml|xml|md|cyclonedx-json|cyclonedx-xml|spdx-json", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("olaf generate .", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("olaf generate ./svc --format cyclonedx-json", generate.Stdout, StringComparison.Ordinal);
        Assert.Contains("olaf generate . --out sbom", generate.Stdout, StringComparison.Ordinal);

        var root = CliTestHelpers.RunCli("--help");

        Assert.Equal(0, root.ExitCode);
        Assert.Contains("generate", root.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Exit2WithUsage_When_BareRootWithoutSubcommand()
    {
        // A positional path with no subcommand is not routed to generate —
        // the parser rejects it (exit 2). Users must spell `generate .`.
        var positional = CliTestHelpers.RunCli(".");

        Assert.Equal(2, positional.ExitCode);
        Assert.Contains("Unrecognized command", positional.Stderr, StringComparison.Ordinal);

        var bare = CliTestHelpers.RunCli();

        Assert.Equal(2, bare.ExitCode);
        Assert.Contains("generate", bare.Stderr, StringComparison.Ordinal);
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
    public void Should_CreateParentDirs_When_GenerateOutNestedMissing()
    {
        var dir = CreateNpmFixtureDir();
        try
        {
            var outFile = Path.Combine(dir, "a", "b", "out.json");

            var result = CliTestHelpers.RunCli("generate", dir, "--out", outFile);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(outFile));
            var content = File.ReadAllText(outFile);
            using var doc = JsonDocument.Parse(content);
            Assert.True(string.IsNullOrWhiteSpace(result.Stdout));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }
}
