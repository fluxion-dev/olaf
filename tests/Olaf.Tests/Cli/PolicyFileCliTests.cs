using System.Text.Json;
using Olaf.Cli;
using Olaf.Core;

namespace Olaf.Tests.Cli;

/// <summary>
/// Issue #75 policy-file (.sbom-rules.yaml) CLI e2e (subprocess, mirrors
/// CliTests.cs style). Every run uses a temp dir with an exact
/// `package.json` (NpmParser.CanHandle name) holding a single phantom
/// dependency (`this-package-definitely-does-not-exist-olaf-xyz`, same shape
/// as CliTests strict fixture) whose effective SPDX is always "Unknown"
/// (404 online, cache-miss offline — no live network required) with a reason
/// carrying a pinned unresolved prefix (`not-found:` et al.). Totals below
/// are fixture-construction-derived (1 dep in, 0/1 out), not live-resolution
/// pins (policy-gate-probe.sh 0.1.0 phantom pattern; frozen expiry dates
/// 2099-01-01 valid / 2000-01-01 expired, no wall-clock).
/// Exit codes: 0 pass, 1 policy violation, 2 usage/schema.
/// </summary>
public sealed class PolicyFileCliTests
{
    private const string PhantomName = "this-package-definitely-does-not-exist-olaf-xyz";
    private const string PhantomVersion = "9.9.9";

    private static string CreatePhantomDir()
    {
        var dir = CliTestHelpers.CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "package.json"),
            $"{{\"name\":\"olaf-policy-fixture\",\"version\":\"1.0.0\",\"dependencies\":{{\"{PhantomName}\":\"{PhantomVersion}\"}}}}");
        return dir;
    }

    private static string WriteRules(string dir, string fileName, string yaml)
    {
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public void L4_ExplicitMissingRulesFile_Exits2()
    {
        var dir = CreatePhantomDir();
        try
        {
            var missing = Path.Combine(dir, "does-not-exist-rules.yaml");

            var result = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", missing);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("Rules file not found", result.Stderr, StringComparison.Ordinal);
            Assert.Contains(missing, result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void L5_DualRulesFilename_PrimaryWinsWithVerboseNote()
    {
        // Secondary alone would allow Unknown (exit 0); primary denies it.
        var dir = CreatePhantomDir();
        try
        {
            WriteRules(dir, ".olaf-rules.yaml", "allow: [Unknown]\n");
            WriteRules(dir, ".sbom-rules.yaml", "deny: [Unknown]\n");

            var result = CliTestHelpers.RunCli("--input", dir, "--format", "json");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(PhantomName, result.Stderr, StringComparison.Ordinal);

            var verbose = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--verbose");

            Assert.Equal(1, verbose.ExitCode);
            Assert.Contains(
                "Using '.sbom-rules.yaml'; ignoring '.olaf-rules.yaml'.",
                verbose.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void M1_FileOnlyDeny_Exits1WithOffenders()
    {
        var dir = CreatePhantomDir();
        try
        {
            var rules = WriteRules(dir, "rules.yaml", "deny: [Unknown]\n");

            var result = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", rules);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(PhantomName, result.Stderr, StringComparison.Ordinal);
            Assert.Contains("-> Unknown", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("1 offender(s) found", result.Stderr, StringComparison.Ordinal);
            // Report-write-first: the JSON report is still written on exit 1.
            Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal(1, doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void M2_FlagReplacesFile_BothDirections()
    {
        // NOTE: replacing the deny list cannot silence an Unknown offender
        // while ANY gate stays active (existing Unknown-fallback: allow/deny
        // presence enforces, CliTests Should_PolicyGate_When_AllowDeny pins
        // "deny misses, Unknown-fallback still fails"). REPLACE is therefore
        // pinned via gate on/off transitions, not via offender silence.
        var dir = CreatePhantomDir();
        try
        {
            // Deny key: file deny Unknown gates (exit 1 per M1); an empty
            // --deny "" still counts as present (token-presence) and replaces
            // the file list with empty -> no gate at all -> exit 0. A UNION
            // implementation would keep Unknown and exit 1.
            var denyUnknown = WriteRules(dir, "deny-unknown.yaml", "deny: [Unknown]\n");
            var emptied = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", denyUnknown, "--deny", string.Empty);
            Assert.Equal(0, emptied.ExitCode);

            // Allow key, file->flag: file allow Unknown passes, but
            // --allow MIT replaces the rescue -> allow-miss offender.
            var allowUnknown = WriteRules(dir, "allow-unknown.yaml", "allow: [Unknown]\n");
            var unrescued = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", allowUnknown, "--allow", "MIT");
            Assert.Equal(1, unrescued.ExitCode);
            Assert.Contains(PhantomName, unrescued.Stderr, StringComparison.Ordinal);

            // Allow key, flag->file direction: file allow MIT offends Unknown,
            // --allow Unknown replaces -> rescued.
            var allowMit = WriteRules(dir, "allow-mit.yaml", "allow: [MIT]\n");
            var rescued = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", allowMit, "--allow", "Unknown");
            Assert.Equal(0, rescued.ExitCode);

            // Per-key independence: file deny MIT alone leaves the fallback
            // gate active (exit 1); adding --allow Unknown replaces only the
            // allow key and rescues -> exit 0 while the deny key stands.
            var denyMit = WriteRules(dir, "deny-mit.yaml", "deny: [MIT]\n");
            var fallback = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", denyMit);
            Assert.Equal(1, fallback.ExitCode);
            var independent = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", denyMit, "--allow", "Unknown");
            Assert.Equal(0, independent.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void M3_AllowRescuesDeny_SameLicense()
    {
        var dir = CreatePhantomDir();
        try
        {
            // Same license on both lists + mixed-case allow -> explicit allow
            // rescues (exit 0), pinning allow-first order + case-insensitivity.
            var both = WriteRules(dir, "both.yaml", "allow: [uNkNoWn]\ndeny: [Unknown]\n");
            var rescued = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", both);
            Assert.Equal(0, rescued.ExitCode);

            // Allow-miss offender: Unknown is in neither spirit of allow MIT.
            var miss = WriteRules(dir, "miss.yaml", "allow: [MIT]\ndeny: [MIT]\n");
            var offender = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", miss);
            Assert.Equal(1, offender.ExitCode);
            Assert.Contains(PhantomName, offender.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void M4_FileFailOnUnknown_Exits1WithoutFlagWarning()
    {
        var dir = CreatePhantomDir();
        try
        {
            var rules = WriteRules(dir, "rules.yaml", "failOnUnknown: true\n");

            var result = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", rules);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(PhantomName, result.Stderr, StringComparison.Ordinal);
            // File-driven gates declare intent in version control: no
            // --allow/--deny flag warning text.
            Assert.DoesNotContain("Warning: --allow/--deny", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void E1_ValidException_SuppressesWithReason()
    {
        var dir = CreatePhantomDir();
        try
        {
            var bounded = WriteRules(
                dir,
                "bounded.yaml",
                $"exceptions:\n  - name: {PhantomName}\n    license: Unknown\n    reason: auditor approved\n    expires: 2099-01-01\n");

            var result = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", bounded);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Suppressed", result.Stderr, StringComparison.Ordinal);
            Assert.Contains(PhantomName, result.Stderr, StringComparison.Ordinal);
            Assert.Contains("auditor approved", result.Stderr, StringComparison.Ordinal);

            // Missing expires = perpetual suppression (same asserts).
            var perpetual = WriteRules(
                dir,
                "perpetual.yaml",
                $"exceptions:\n  - name: {PhantomName}\n    license: Unknown\n    reason: forever deal\n");
            var again = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", perpetual);

            Assert.Equal(0, again.ExitCode);
            Assert.Contains("Suppressed", again.Stderr, StringComparison.Ordinal);
            Assert.Contains("forever deal", again.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void E2_ExpiredException_ViolationResumesWithMarker()
    {
        var dir = CreatePhantomDir();
        try
        {
            var expired = WriteRules(
                dir,
                "expired.yaml",
                $"exceptions:\n  - name: {PhantomName}\n    license: Unknown\n    reason: auditor approved\n    expires: 2000-01-01\n");

            var result = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", expired);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("(exception expired:", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("auditor approved", result.Stderr, StringComparison.Ordinal);

            // License scope tail: same package, different license -> the
            // exception does not match, so the gate (not expiry) fires with
            // no expired marker.
            var scoped = WriteRules(
                dir,
                "scoped.yaml",
                $"failOnUnknown: true\nexceptions:\n  - name: {PhantomName}\n    license: MIT\n    reason: wrong license\n    expires: 2099-01-01\n");
            var again = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", scoped);

            Assert.Equal(1, again.ExitCode);
            Assert.DoesNotContain("(exception expired:", again.Stderr, StringComparison.Ordinal);
            Assert.Contains(PhantomName, again.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void E3_EmptyReason_SchemaErrorExit2()
    {
        var dir = CreatePhantomDir();
        try
        {
            var rules = WriteRules(
                dir,
                "rules.yaml",
                $"exceptions:\n  - name: {PhantomName}\n    license: Unknown\n    reason: \"\"\n");

            var result = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", rules);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("reason", result.Stderr, StringComparison.OrdinalIgnoreCase);
            // Schema errors exit before any report write (no partial report).
            Assert.True(string.IsNullOrWhiteSpace(result.Stdout));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void X1_MalformedYaml_Exit2WithFileLineCol()
    {
        var dir = CreatePhantomDir();
        try
        {
            var rules = WriteRules(dir, "bad.yaml", "deny:\n - MIT\n  - BADINDENT\n   : : :\n");
            var outPath = Path.Combine(dir, "out.json");

            var result = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", rules, "--out", outPath);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains(rules, result.Stderr, StringComparison.Ordinal);
            Assert.Matches(@":\d+:\d+", result.Stderr);
            // Schema errors exit BEFORE any report write: no --out file.
            Assert.False(File.Exists(outPath));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void X2_WrongTypeSchema_Exit2WithFileLine()
    {
        var dir = CreatePhantomDir();
        try
        {
            var rules = WriteRules(dir, "rules.yaml", "deny: \"MIT\"\n");

            var result = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", rules);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains(rules, result.Stderr, StringComparison.Ordinal);
            Assert.Matches(@":\d+:\d+", result.Stderr);
            Assert.Contains("must be a list of strings", result.Stderr, StringComparison.Ordinal);
            Assert.True(string.IsNullOrWhiteSpace(result.Stdout));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void X3_FailOnUnresolved_PerPrefixGate()
    {
        // Unit half: the 4 pinned unresolved prefixes (B1, case-insensitive)
        // gate Unknown; other reasons, empty reasons, and Resolved status do not.
        foreach (var reason in new[] { "transport-error: x", "Timeout: x", "NOT-FOUND: x", "resolver-error: x" })
        {
            var unresolved = new ResolvedLicense(
                new Dependency("npm", "pkg", "1.0.0", false), null, null, null, "Unknown", reason);
            Assert.True(RulesLoader.IsUnresolved(unresolved));
        }

        Assert.False(
            RulesLoader.IsUnresolved(
                new ResolvedLicense(
                    new Dependency("npm", "pkg", "1.0.0", false), null, null, null, "Unknown", "other-reason: x")));
        Assert.False(
            RulesLoader.IsUnresolved(
                new ResolvedLicense(
                    new Dependency("npm", "pkg", "1.0.0", false), null, null, null, "Unknown", null)));
        Assert.False(
            RulesLoader.IsUnresolved(
                new ResolvedLicense(
                    new Dependency("npm", "pkg", "1.0.0", false), "MIT", null, null, "Resolved", "transport-error: x")));

        // CLI tail: the phantom's Unknown always carries a pinned prefix
        // (every resolver failure reason does, online or offline), so file
        // failOnUnresolved:true fires (exit 1) and explicit false stays silent.
        var dir = CreatePhantomDir();
        try
        {
            var on = WriteRules(dir, "on.yaml", "failOnUnresolved: true\n");
            var fired = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", on);
            Assert.Equal(1, fired.ExitCode);
            Assert.Contains(PhantomName, fired.Stderr, StringComparison.Ordinal);

            var off = WriteRules(dir, "off.yaml", "failOnUnresolved: false\n");
            var silent = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", off);
            Assert.Equal(0, silent.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void C1_ExcludeEcosystems_FiltersBeforeGateAndReport()
    {
        var dir = CreatePhantomDir();
        try
        {
            var rules = WriteRules(dir, "rules.yaml", "excludeEcosystems: [npm]\n");

            var result = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", rules);

            // The single npm dep is excluded pre-gate AND pre-report: empty
            // set, exit 0, total 0 in the written report.
            Assert.Equal(0, result.ExitCode);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal(0, doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("licenses").GetArrayLength());

            // Intersect tail: --ecosystem npm + file exclude npm -> empty set.
            var scoped = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", rules, "--ecosystem", "npm");
            Assert.Equal(0, scoped.ExitCode);
            using var scopedDoc = JsonDocument.Parse(scoped.Stdout);
            Assert.Equal(0, scopedDoc.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void C2_StrictFlag_ForcesFailOnUnknownOverFileFalse()
    {
        var dir = CreatePhantomDir();
        try
        {
            var rules = WriteRules(dir, "rules.yaml", "failOnUnknown: false\n");

            var result = CliTestHelpers.RunCli("--input", dir, "--format", "json", "--rules", rules, "--strict");

            Assert.Equal(1, result.ExitCode);
            // Existing --strict default-gate contract: the strict message is
            // printed WITHOUT per-offender lines (CliTests pins exit code
            // only), so assert the message, not the package name.
            Assert.Contains("Strict mode: unknown licenses found.", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void C3_AutoDiscovery_AdjacentFilesAndHelp()
    {
        // Adjacent .sbom-rules.yaml (no --rules flag) is discovered.
        var primary = CreatePhantomDir();
        try
        {
            WriteRules(primary, ".sbom-rules.yaml", "deny: [Unknown]\n");
            var found = CliTestHelpers.RunCli("--input", primary, "--format", "json");
            Assert.Equal(1, found.ExitCode);
            Assert.Contains(PhantomName, found.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(primary);
        }

        // .olaf-rules.yaml fallback when the primary is absent.
        var fallback = CreatePhantomDir();
        try
        {
            WriteRules(fallback, ".olaf-rules.yaml", "deny: [Unknown]\n");
            var found = CliTestHelpers.RunCli("--input", fallback, "--format", "json");
            Assert.Equal(1, found.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fallback);
        }

        // File input uses its own directory (not cwd): rules adjacent to
        // package.json fire when --input points at the file.
        var fileInput = CreatePhantomDir();
        try
        {
            WriteRules(fileInput, ".sbom-rules.yaml", "deny: [Unknown]\n");
            var found = CliTestHelpers.RunCli("--input", Path.Combine(fileInput, "package.json"), "--format", "json");
            Assert.Equal(1, found.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fileInput);
        }

        // No walk-up: rules in the parent of the input dir are ignored.
        var parent = CliTestHelpers.CreateTempDir();
        try
        {
            WriteRules(parent, ".sbom-rules.yaml", "deny: [Unknown]\n");
            var child = Path.Combine(parent, "child");
            Directory.CreateDirectory(child);
            File.WriteAllText(
                Path.Combine(child, "package.json"),
                $"{{\"name\":\"olaf-policy-fixture\",\"version\":\"1.0.0\",\"dependencies\":{{\"{PhantomName}\":\"{PhantomVersion}\"}}}}");
            var silent = CliTestHelpers.RunCli("--input", child, "--format", "json");
            Assert.Equal(0, silent.ExitCode);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(parent);
        }

        // --help documents the --rules flag.
        var help = CliTestHelpers.RunCli("--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("--rules", help.Stdout + help.Stderr, StringComparison.Ordinal);
    }
}
