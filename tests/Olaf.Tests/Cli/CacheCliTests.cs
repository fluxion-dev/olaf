using System.Text.Json;

namespace Olaf.Tests.Cli;

// Issue #78 (B6 rows C1–C3): `--cache-dir` / `--no-cache` / `--refresh-cache`
// CLI surface (root + `generate`). Subprocess e2e on temp dirs only — every
// run passes `--offline`, so no HTTP is ever issued (offline skips the
// ClearlyDefined fallback at the callsite; zero-HTTP is proven at unit level
// by CachedResolverTests R4 with CallCount == 0). The cache file is seeded
// directly in the lowercase on-disk schema (B1; see DiskLicenseCache
// ParseEntry), so "second run" reuse needs no live network.
//
// Flag-parity table (per-flag root arm + generate arm or why-inapplicable):
//   --cache-dir reuse exit 0 ........ root arm + generate arm (C1, stdout-equal)
//   --cache-dir + --cache-ttl-days 7  root arm (C1 tail: valid TTL accepted, hit stays exit 0)
//   --no-cache bypass exit 1 ........ root arm + generate arm (C2, strict-on-miss)
//   --refresh-cache skip-read exit 0  root arm + generate arm (C3 + control arm)
//   --out file-bytes ................ why-inapplicable: --out path is flag-orthogonal (covered by
//                                     existing CliTests --out arms); cache changes resolution only.
//   empty-input fork (exit 2) ....... why-inapplicable: cache flags never alter usage/config arms;
//                                     covered by the existing CliTests usage-error matrix.
//   invalid --cache-ttl-days exit 2 . root arm (C3 tail: malformed TTL is a usage error).
//   --no-cache + --refresh-cache .... why-inapplicable offline: refresh-wins (B3) is
//                                     observationally identical to bypass here (offline misses never
//                                     persist per B2, so neither arm writes); online needs network.
//   --refresh-cache force-write ..... why-inapplicable offline: offline misses never persist (B2
//                                     predicate); the write path is pinned by unit Store tests (U1/R1).
//   input-precedence rule = B3 ..... flag arm asserted here (C1–C3 pass --cache-dir);
//                                     env/XDG arms pinned at unit level (U4).
//   recursive/monorepo naming ....... why-inapplicable: single-file temp fixture, no recursive scan.
//   policy-file-probe ............... why-inapplicable: strict-only asserts, no --allow/--deny file.
// Theory-count note: 0 Theories in this file; 3 Facts, no arm expansion —
// the +3 in the 5+4+3=12 sum-check counts Facts 1:1.
public class CacheCliTests
{
    private const string SeededPkg = "olaf-seeded-pkg-78";
    private const string SeededVersion = "1.2.3";

    private static string CreateCacheFixtureDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "olaf-cachecli-78-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "package.json"),
            "{\"name\":\"olaf-cache-fixture-78\",\"version\":\"1.0.0\","
                + "\"dependencies\":{\"" + SeededPkg + "\":\"" + SeededVersion + "\"}}");
        return dir;
    }

    private static string CreateCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "olaf-cachedir-78-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    // Seed cache.json in the lowercase on-disk schema (B1 store shape).
    private static void SeedResolved(string cacheDir, string spdx = "MIT", string status = "Resolved")
    {
        var key = "npm:" + SeededPkg + "@" + SeededVersion;
        var stamp = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var json = "{\"entries\":{\"" + key + "\":{\"spdx\":\"" + spdx + "\""
            + ",\"licenseText\":null,\"sourceUrl\":null"
            + ",\"status\":\"" + status + "\""
            + ",\"reason\":\"registry:npm\""
            + ",\"fetchedAt\":\"" + stamp + "\""
            + ",\"etag\":null}}}";
        File.WriteAllText(Path.Combine(cacheDir, "cache.json"), json);
    }

    private static (int Total, int Resolved, int Unknown) ReadSummary(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        var summary = doc.RootElement.GetProperty("summary");
        return (
            summary.GetProperty("total").GetInt32(),
            summary.GetProperty("resolved").GetInt32(),
            summary.GetProperty("unknown").GetInt32());
    }

    private static string SpdxOf(string stdout, string name)
    {
        using var doc = JsonDocument.Parse(stdout);
        foreach (var item in doc.RootElement.GetProperty("licenses").EnumerateArray())
        {
            if (item.GetProperty("name").GetString() == name)
            {
                return item.GetProperty("spdx").GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    // CLI row 1/3 — second-run reuse: two offline runs over the same
    // --cache-dir serve byte-identical Resolved output (resolved == 1).
    // Tail: valid --cache-ttl-days arm (exit 0, hit preserved) + generate
    // parity (exit + stdout equality) in the same Fact.
    [Fact]
    public void Should_ReuseDiskCacheAcrossRuns_When_OfflineWithCacheDir()
    {
        var fixtureDir = CreateCacheFixtureDir();
        var cacheDir = CreateCacheDir();
        try
        {
            SeedResolved(cacheDir);

            string[] BaseArgs() => new[] { "--input", fixtureDir, "--format", "json", "--offline", "--cache-dir", cacheDir };
            var first = CliTestHelpers.RunCli(BaseArgs());
            var second = CliTestHelpers.RunCli(BaseArgs());

            Assert.Equal(0, first.ExitCode);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(first.Stdout, second.Stdout);
            Assert.Equal((1, 1, 0), ReadSummary(first.Stdout));
            Assert.Equal("MIT", SpdxOf(first.Stdout, SeededPkg));

            var withTtl = CliTestHelpers.RunCli(BaseArgs().Concat(new[] { "--cache-ttl-days", "7" }).ToArray());
            Assert.Equal(0, withTtl.ExitCode);
            Assert.Equal((1, 1, 0), ReadSummary(withTtl.Stdout));

            var generate = CliTestHelpers.RunCli(new[] { "generate", fixtureDir, "--format", "json", "--offline", "--cache-dir", cacheDir });
            Assert.Equal(first.ExitCode, generate.ExitCode);
            Assert.Equal(first.Stdout, generate.Stdout);
        }
        finally
        {
            DeleteTempDir(fixtureDir);
            DeleteTempDir(cacheDir);
        }
    }

    // CLI row 2/3 — --no-cache bypasses reads AND writes: the seeded MIT is
    // ignored (Unknown trips --strict to exit 1) and the cache file bytes are
    // untouched. Tail: generate parity (exit equality) in the same Fact.
    [Fact]
    public void Should_BypassDiskCache_When_NoCacheOffline()
    {
        var fixtureDir = CreateCacheFixtureDir();
        var cacheDir = CreateCacheDir();
        try
        {
            SeedResolved(cacheDir);
            var before = File.ReadAllBytes(Path.Combine(cacheDir, "cache.json"));

            var result = CliTestHelpers.RunCli(
                "--input", fixtureDir, "--format", "json", "--offline",
                "--cache-dir", cacheDir, "--no-cache", "--strict");

            Assert.Equal(1, result.ExitCode);
            Assert.Equal((1, 0, 1), ReadSummary(result.Stdout));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(cacheDir, "cache.json")));

            var generate = CliTestHelpers.RunCli(
                "generate", fixtureDir, "--format", "json", "--offline",
                "--cache-dir", cacheDir, "--no-cache", "--strict");
            Assert.Equal(result.ExitCode, generate.ExitCode);
        }
        finally
        {
            DeleteTempDir(fixtureDir);
            DeleteTempDir(cacheDir);
        }
    }

    // CLI row 3/3 — --refresh-cache skips disk reads: the seeded MIT is
    // ignored (Unknown, exit 0) while the no-flag control run hits (MIT).
    // Tail: invalid --cache-ttl-days arm (exit 2) + generate parity in the
    // same Fact.
    [Fact]
    public void Should_SkipDiskRead_When_RefreshCacheOffline()
    {
        var fixtureDir = CreateCacheFixtureDir();
        var cacheDir = CreateCacheDir();
        try
        {
            SeedResolved(cacheDir);

            var refreshed = CliTestHelpers.RunCli(
                "--input", fixtureDir, "--format", "json", "--offline",
                "--cache-dir", cacheDir, "--refresh-cache");

            Assert.Equal(0, refreshed.ExitCode);
            Assert.Equal((1, 0, 1), ReadSummary(refreshed.Stdout));

            var control = CliTestHelpers.RunCli(
                "--input", fixtureDir, "--format", "json", "--offline",
                "--cache-dir", cacheDir);

            Assert.Equal(0, control.ExitCode);
            Assert.Equal((1, 1, 0), ReadSummary(control.Stdout));
            Assert.NotEqual(control.Stdout, refreshed.Stdout);

            var badTtl = CliTestHelpers.RunCli(
                "--input", fixtureDir, "--format", "json",
                "--cache-dir", cacheDir, "--cache-ttl-days", "bogus");
            Assert.Equal(2, badTtl.ExitCode);

            var generate = CliTestHelpers.RunCli(
                "generate", fixtureDir, "--format", "json", "--offline",
                "--cache-dir", cacheDir, "--refresh-cache");
            Assert.Equal(refreshed.ExitCode, generate.ExitCode);
            Assert.Equal(refreshed.Stdout, generate.Stdout);
        }
        finally
        {
            DeleteTempDir(fixtureDir);
            DeleteTempDir(cacheDir);
        }
    }
}
