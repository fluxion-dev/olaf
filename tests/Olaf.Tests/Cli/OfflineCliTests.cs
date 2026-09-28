using System.Text.Json;

namespace Olaf.Tests.Cli;

// Issue #123: `--offline` generate-only surface. Subprocess e2e on temp
// fixture DIRS only (generate takes a directory) — every run passes
// `--offline`, so no HTTP is ever issued (offline skips the ClearlyDefined
// fallback at the callsite; zero-HTTP is proven at unit level by the
// throwing-handler resolver tests with CallCount == 0). The strict-fixture
// phantom dep stays Unknown via offline cache-miss.
public class OfflineCliTests
{
    private static string CreateNpmFixtureDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.Copy(CliTestHelpers.FixturePath("npm", "package.json"), Path.Combine(dir, "package.json"));
        return dir;
    }

    // CLI row 1/4 — generate --offline exits 0 with a valid envelope.
    [Fact]
    public void Should_ExitZero_When_OfflineGenerateNpmFixture()
    {
        var dir = CreateNpmFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", dir, "--format", "json", "--offline");

            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    // CLI row 2/4 — generate --offline + --strict exits 1 on the phantom-dep
    // fixture (Unknown via offline-cache-miss trips the strict gate).
    [Fact]
    public void Should_ExitOne_When_OfflineStrictOnUnknownFixture()
    {
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", fixtureDir, "--format", "json", "--offline", "--strict");

            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    // CLI row 3/4 — generate --offline on a manifest-less dir exits 0 with a
    // valid empty envelope (no HTTP, nothing to resolve).
    [Fact]
    public void Should_ExitZeroWithEmptyEnvelope_When_OfflineEmptyDir()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
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

    // CLI row 4/4 — the strict gate is vacuous on an empty report: no
    // offenders means exit 0 even with --strict.
    [Fact]
    public void Should_ExitZero_When_OfflineStrictOnEmptyDir()
    {
        var tempDir = CliTestHelpers.CreateTempDir();
        try
        {
            var result = CliTestHelpers.RunCli("generate", tempDir, "--format", "json", "--offline", "--strict");

            Assert.Equal(0, result.ExitCode);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal(0, CliTestHelpers.JsonTotal(result.Stdout));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(tempDir);
        }
    }
}
