using System.Text.Json;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for cross-ecosystem consistency (issue #43). Subprocess e2e
/// via CliTestHelpers; fixture is a local temp dir only (no live network).
/// Phantom npm/pip fixtures guarantee Unknown licenses (404 or offline
/// cache-miss), so assertions stay tolerant on resolved/unknown counts and
/// never pin SPDX values.
/// </summary>
public sealed class CrossEcosystemCliTests
{
    private static string? GetEcosystem(JsonElement entry) => entry.GetProperty("ecosystem").GetString();

    private static string? GetName(JsonElement entry) => entry.GetProperty("name").GetString();

    private static string? GetVersion(JsonElement entry) => entry.GetProperty("version").GetString();

    private static int CompareByEcosystemNameVersion(JsonElement prev, JsonElement curr)
    {
        var cmp = string.Compare(GetEcosystem(prev), GetEcosystem(curr), StringComparison.Ordinal);
        if (cmp == 0)
        {
            cmp = string.Compare(GetName(prev), GetName(curr), StringComparison.Ordinal);
        }

        if (cmp == 0)
        {
            cmp = string.Compare(GetVersion(prev), GetVersion(curr), StringComparison.Ordinal);
        }

        return cmp;
    }

    [Fact]
    public void Should_IdentifySortAndCount_When_MixedNpmPipDir()
    {
        var fixtureDir = CliTestHelpers.CreateMixedNpmPipFixtureDir(
            """{"name":"olaf-mixed-fixture","version":"1.0.0","dependencies":{"aaa-npm-phantom-olaf-xyz":"9.9.9","mmm-npm-phantom-olaf-xyz":"9.9.8"}}""",
            "aaa-pip-phantom-olaf-xyz==9.9.7\nmmm-pip-phantom-olaf-xyz==9.9.6\n");
        try
        {
            var result = CliTestHelpers.RunCli("--input", fixtureDir, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
            using var doc = JsonDocument.Parse(result.Stdout);

            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(4, summary.GetProperty("total").GetInt32());
            Assert.Equal(
                summary.GetProperty("total").GetInt32(),
                summary.GetProperty("resolved").GetInt32() + summary.GetProperty("unknown").GetInt32());

            var licenses = doc.RootElement.GetProperty("licenses");
            Assert.Equal(4, licenses.GetArrayLength());

            var entries = licenses.EnumerateArray().ToList();

            var npmEntries = entries.Where(e => GetEcosystem(e) == "npm").ToList();
            var pipEntries = entries.Where(e => GetEcosystem(e) == "pip").ToList();
            Assert.Equal(2, npmEntries.Count);
            Assert.Equal(2, pipEntries.Count);

            var tuples = entries
                .Select(e => (
                    ecosystem: GetEcosystem(e),
                    name: GetName(e),
                    version: GetVersion(e)))
                .ToHashSet();
            Assert.Contains(("npm", "aaa-npm-phantom-olaf-xyz", "9.9.9"), tuples);
            Assert.Contains(("npm", "mmm-npm-phantom-olaf-xyz", "9.9.8"), tuples);
            Assert.Contains(("pip", "aaa-pip-phantom-olaf-xyz", "9.9.7"), tuples);
            Assert.Contains(("pip", "mmm-pip-phantom-olaf-xyz", "9.9.6"), tuples);
            Assert.Equal(4, tuples.Count);

            // No cross-contamination: each ecosystem owns exactly its phantom names.
            var npmNames = npmEntries.Select(GetName).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("aaa-npm-phantom-olaf-xyz", npmNames);
            Assert.Contains("mmm-npm-phantom-olaf-xyz", npmNames);
            Assert.DoesNotContain("aaa-pip-phantom-olaf-xyz", npmNames);
            Assert.DoesNotContain("mmm-pip-phantom-olaf-xyz", npmNames);
            var pipNames = pipEntries.Select(GetName).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("aaa-pip-phantom-olaf-xyz", pipNames);
            Assert.Contains("mmm-pip-phantom-olaf-xyz", pipNames);
            Assert.DoesNotContain("aaa-npm-phantom-olaf-xyz", pipNames);
            Assert.DoesNotContain("mmm-npm-phantom-olaf-xyz", pipNames);

            // Sorted consistently (ecosystem, name, version) with Ordinal comparison:
            // npm < pip, aaa < mmm within each ecosystem.
            Assert.Equal("npm", GetEcosystem(entries[0]));
            Assert.Equal("aaa-npm-phantom-olaf-xyz", GetName(entries[0]));
            Assert.Equal("npm", GetEcosystem(entries[1]));
            Assert.Equal("mmm-npm-phantom-olaf-xyz", GetName(entries[1]));
            Assert.Equal("pip", GetEcosystem(entries[2]));
            Assert.Equal("aaa-pip-phantom-olaf-xyz", GetName(entries[2]));
            Assert.Equal("pip", GetEcosystem(entries[3]));
            Assert.Equal("mmm-pip-phantom-olaf-xyz", GetName(entries[3]));
            for (var i = 1; i < entries.Count; i++)
            {
                var cmp = CompareByEcosystemNameVersion(entries[i - 1], entries[i]);

                Assert.True(cmp < 0, $"Expected licenses[{i - 1}] < licenses[{i}] (Ordinal by ecosystem, name, version).");
            }

            int StdoutIndexOf(string name) => result.Stdout.IndexOf(name, StringComparison.Ordinal);
            var aaaNpm = StdoutIndexOf("aaa-npm-phantom-olaf-xyz");
            var mmmNpm = StdoutIndexOf("mmm-npm-phantom-olaf-xyz");
            var aaaPip = StdoutIndexOf("aaa-pip-phantom-olaf-xyz");
            var mmmPip = StdoutIndexOf("mmm-pip-phantom-olaf-xyz");
            Assert.True(aaaNpm >= 0, "JSON output missing 'aaa-npm-phantom-olaf-xyz'.");
            Assert.True(mmmNpm >= 0, "JSON output missing 'mmm-npm-phantom-olaf-xyz'.");
            Assert.True(aaaPip >= 0, "JSON output missing 'aaa-pip-phantom-olaf-xyz'.");
            Assert.True(mmmPip >= 0, "JSON output missing 'mmm-pip-phantom-olaf-xyz'.");
            Assert.True(aaaNpm < mmmNpm, "Expected 'aaa-npm-phantom-olaf-xyz' before 'mmm-npm-phantom-olaf-xyz'.");
            Assert.True(mmmNpm < aaaPip, "Expected 'mmm-npm-phantom-olaf-xyz' before 'aaa-pip-phantom-olaf-xyz' (npm before pip).");
            Assert.True(aaaPip < mmmPip, "Expected 'aaa-pip-phantom-olaf-xyz' before 'mmm-pip-phantom-olaf-xyz'.");
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(fixtureDir);
        }
    }
}
