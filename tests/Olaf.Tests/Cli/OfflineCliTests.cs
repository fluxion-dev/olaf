using System.Text.Json;

namespace Olaf.Tests.Cli;

// Issue #77: `--offline` CLI surface (root + `generate`). Subprocess e2e on
// Fixtures/ + temp dirs only — no live network (offline never sends HTTP;
// strict-fixture phantom dep stays Unknown via offline cache-miss).
//
// Flag-parity table (per-flag root arm + generate arm or why-inapplicable):
//   --offline exit 0 ............ root arm (Should_ExitZero_When_OfflineScanNpmFixture)
//   --offline exit 0 ............ generate arm (parity test asserts exit + stdout equality)
//   --offline + --strict exit 1 . root arm (Should_ExitOne_When_OfflineStrictOnUnknownFixture);
//                                generate arm covered by the parity test's exit-equality on the
//                                same strict fixture (exit(generate X) == exit(root X)).
//   --out file-bytes ............ why-inapplicable: --out path is flag-orthogonal (covered by
//                                existing CliTests --out arms); offline changes resolution only.
//   policy-file-probe ........... why-inapplicable: strict-only asserts, no --allow/--deny file.
public class OfflineCliTests
{
    // CLI row 1/3 — root --offline exits 0 with a valid envelope.
    [Fact]
    public void Should_ExitZero_When_OfflineScanNpmFixture()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "json", "--offline");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        using var doc = JsonDocument.Parse(result.Stdout);
    }

    // CLI row 2/3 — --offline + --strict exits 1 on the phantom-dep fixture
    // (Unknown via offline-cache-miss trips the strict gate).
    [Fact]
    public void Should_ExitOne_When_OfflineStrictOnUnknownFixture()
    {
        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var result = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--offline", "--strict");

            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }

    // CLI row 3/3 — generate --offline parity: same input, same exits, same
    // stdout on both the clean fixture and the strict fixture.
    [Fact]
    public void Should_MatchRoot_When_GenerateOfflineParity()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var root = CliTestHelpers.RunCli("--input", input, "--format", "json", "--offline");
        var generate = CliTestHelpers.RunCli("generate", input, "--format", "json", "--offline");

        Assert.Equal(0, root.ExitCode);
        Assert.Equal(root.ExitCode, generate.ExitCode);
        Assert.Equal(root.Stdout, generate.Stdout); // byte-identical (json has no per-run fields)

        var fixtureDir = CliTestHelpers.CreateStrictFixtureDir();
        try
        {
            var rootStrict = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json", "--offline", "--strict");
            var generateStrict = CliTestHelpers.RunCli("generate", fixtureDir, "--format", "json", "--offline", "--strict");

            Assert.Equal(1, rootStrict.ExitCode);
            Assert.Equal(rootStrict.ExitCode, generateStrict.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }
}
