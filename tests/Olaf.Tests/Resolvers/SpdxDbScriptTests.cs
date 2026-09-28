using System.Security.Cryptography;
using Olaf.Tests.Cli;

namespace Olaf.Tests.Resolvers;

// Issue #77 (D2/D3) runbook + hash: the maintainer refresh script and its
// sha256 pin. Static reads only — the suite NEVER executes the script
// (MAINTAINER-NETWORK: it fetches the license-list-data tarball).
public class SpdxDbScriptTests
{
    private static string RepoPath(params string[] segments)
    {
        return Path.Combine(new[] { CliTestHelpers.RepoRoot() }.Concat(segments).ToArray());
    }

    // Script row 1/2 — update script exists, pins v3.29.0, and carries the
    // deterministic-build flags (LC_ALL=C sort + jq -S) plus the 5MB gate.
    [Fact]
    public void UpdateScript_PinsVersionAndDeterministicFlags()
    {
        var script = RepoPath("tools", "update-spdx-db.sh");
        Assert.True(File.Exists(script), $"missing maintainer script: {script}");

        var contents = File.ReadAllText(script);
        Assert.Contains("v3.29.0", contents, StringComparison.Ordinal);
        Assert.Contains("LC_ALL=C", contents, StringComparison.Ordinal);
        Assert.Contains("jq -S", contents, StringComparison.Ordinal);
        Assert.Contains("sha256sum", contents, StringComparison.Ordinal);
        Assert.Contains("5242880", contents, StringComparison.Ordinal);
        Assert.StartsWith("#!/usr/bin/env bash", contents, StringComparison.Ordinal);
    }

    // Script row 2/2 — sha256 pin matches the checked-in DB bytes
    // (recomputed locally; no network).
    [Fact]
    public void DbHash_MatchesCheckedInJson()
    {
        var jsonPath = RepoPath("src", "Olaf.Resolvers", "Data", "spdx-licenses.json");
        var hashPath = RepoPath("tools", "spdx-db.sha256");
        Assert.True(File.Exists(jsonPath), $"missing DB asset: {jsonPath}");
        Assert.True(File.Exists(hashPath), $"missing hash pin: {hashPath}");

        var bytes = File.ReadAllBytes(jsonPath);
        Assert.True(bytes.Length > 0 && bytes.Length < 5_242_880);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var pinned = File.ReadAllText(hashPath);
        Assert.StartsWith(actual, pinned, StringComparison.Ordinal);
    }
}
