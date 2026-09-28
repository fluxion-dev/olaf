using Olaf.Core;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #73 Step 3: byte-parity snapshots pinning the hand-coded txt/md/html
/// defaults UNCHANGED (plan DECISION 3 binding — built-ins stay hand-coded C#;
/// any byte diff = FAIL). Expected strings were captured live once from
/// TxtFormatter/MarkdownFormatter/HtmlFormatter on this worktree (net10.0,
/// scratch parity-dump run over empty + holders + special-chars inputs) and
/// are pinned here as exact-equality snapshots. Special-chars covers | &lt; &gt;
/// &amp; " (pipe exercises md cell-escaping, angle/amp/quote exercise
/// html-encoding, txt passes through raw). No live network — ScanResults are
/// constructed inline.
/// </summary>
public sealed class TemplateParityTests
{
    private static ScanResult HoldersResult() => new(
    [
        new ResolvedLicense(
            new Dependency("npm", "express", "4.18.2", false),
            "MIT",
            "MIT License",
            "https://example.com/express/LICENSE",
            "Resolved",
            null,
            new Enrichment(
                "pkg:npm/express@4.18.2",
                ["sha512:abc"],
                "Example Supplier",
                "https://example.com/express.tgz",
                ["Express Authors", "TJ Holowaychuk"])),
    ]);

    private static ScanResult SpecialCharsResult() => new(
    [
        new ResolvedLicense(
            new Dependency("npm", "evil|<script>&\"pkg", "1.0.0 <", false),
            "MIT",
            "text with <b>markup</b> & \"quotes\" | pipe",
            "https://example.com/?a=1&b=2",
            "Resolved",
            null),
    ]);

    [Fact]
    public void Should_MatchSnapshot_When_TxtDefaultUnchanged()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("txt");

        Assert.Equal(
            "Third-Party Attribution\nTotal: 0, Resolved: 0, Unknown: 0\n",
            formatter.FormatResult(ScanResult.Empty));
        Assert.Equal(
            "Third-Party Attribution\nTotal: 1, Resolved: 1, Unknown: 0\n\nexpress@4.18.2 (npm) direct=true\n  SPDX: MIT\n  Copyright: Express Authors; TJ Holowaychuk\n  Source: https://example.com/express/LICENSE\n  Status: Resolved\n",
            formatter.FormatResult(HoldersResult()));

        // Special chars pass through raw (no escaping in txt); | < > & " pinned byte-identical.
        var special = formatter.FormatResult(SpecialCharsResult());
        Assert.Equal(
            "Third-Party Attribution\nTotal: 1, Resolved: 1, Unknown: 0\n\nevil|<script>&\"pkg@1.0.0 < (npm) direct=true\n  SPDX: MIT\n  Source: https://example.com/?a=1&b=2\n  Status: Resolved\n",
            special);
        Assert.Contains("evil|<script>&\"pkg@1.0.0 <", special, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_MatchSnapshot_When_MdDefaultUnchanged()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("md");

        Assert.Equal(
            "# Third-Party Attribution\n\nTotal: 0, Resolved: 0, Unknown: 0\n",
            formatter.FormatResult(ScanResult.Empty));
        Assert.Equal(
            "# Third-Party Attribution\n\nTotal: 1, Resolved: 1, Unknown: 0\n\n| Ecosystem | Name | Version | SPDX | License | Source | Status | Reason | Direct |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- |\n| npm | express | 4.18.2 | MIT | MIT License | https://example.com/express/LICENSE | Resolved |  | true |\n\n## express@4.18.2 (npm)\n\n- Ecosystem: npm\n- Name: express\n- Version: 4.18.2\n- SPDX: MIT\n- Copyright: Express Authors; TJ Holowaychuk\n- License: MIT License\n- Source: https://example.com/express/LICENSE\n- Status: Resolved\n- Reason: \n- Direct: true\n",
            formatter.FormatResult(HoldersResult()));

        // Pipes are cell-escaped (\|); angle/amp/quote pass through raw in md.
        var special = formatter.FormatResult(SpecialCharsResult());
        Assert.Equal(
            "# Third-Party Attribution\n\nTotal: 1, Resolved: 1, Unknown: 0\n\n| Ecosystem | Name | Version | SPDX | License | Source | Status | Reason | Direct |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- |\n| npm | evil\\|<script>&\"pkg | 1.0.0 < | MIT | text with <b>markup</b> & \"quotes\" \\| pipe | https://example.com/?a=1&b=2 | Resolved |  | true |\n\n## evil|<script>&\"pkg@1.0.0 < (npm)\n\n- Ecosystem: npm\n- Name: evil\\|<script>&\"pkg\n- Version: 1.0.0 <\n- SPDX: MIT\n- License: text with <b>markup</b> & \"quotes\" \\| pipe\n- Source: https://example.com/?a=1&b=2\n- Status: Resolved\n- Reason: \n- Direct: true\n",
            special);
        Assert.Contains("evil\\|<script>&\"pkg", special, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_MatchSnapshot_When_HtmlDefaultUnchanged()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("html");

        Assert.Equal(
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Olaf License Report</title></head><body><p>Total: 0 · Resolved: 0 · Unknown: 0</p><table><thead><tr><th>Ecosystem</th><th>Name</th><th>Version</th><th>SPDX</th><th>License</th><th>Source</th><th>Status</th><th>Reason</th><th>Direct</th></tr></thead><tbody></tbody></table></body></html>",
            formatter.FormatResult(ScanResult.Empty));
        Assert.Equal(
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Olaf License Report</title></head><body><p>Total: 1 · Resolved: 1 · Unknown: 0</p><table><thead><tr><th>Ecosystem</th><th>Name</th><th>Version</th><th>SPDX</th><th>License</th><th>Source</th><th>Status</th><th>Reason</th><th>Direct</th></tr></thead><tbody><tr><td>npm</td><td>express</td><td>4.18.2</td><td>MIT</td><td>MIT License</td><td>https://example.com/express/LICENSE</td><td>Resolved</td><td></td><td>true</td></tr></tbody></table><p>Copyright: express@4.18.2: Express Authors; TJ Holowaychuk</p></body></html>",
            formatter.FormatResult(HoldersResult()));

        // < > & " are HtmlEncoded; | passes through raw in html.
        var special = formatter.FormatResult(SpecialCharsResult());
        Assert.Equal(
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Olaf License Report</title></head><body><p>Total: 1 · Resolved: 1 · Unknown: 0</p><table><thead><tr><th>Ecosystem</th><th>Name</th><th>Version</th><th>SPDX</th><th>License</th><th>Source</th><th>Status</th><th>Reason</th><th>Direct</th></tr></thead><tbody><tr><td>npm</td><td>evil|&lt;script&gt;&amp;&quot;pkg</td><td>1.0.0 &lt;</td><td>MIT</td><td>text with &lt;b&gt;markup&lt;/b&gt; &amp; &quot;quotes&quot; | pipe</td><td>https://example.com/?a=1&amp;b=2</td><td>Resolved</td><td></td><td>true</td></tr></tbody></table></body></html>",
            special);
        Assert.DoesNotContain("<script>", special, StringComparison.Ordinal);
        Assert.Contains("evil|&lt;script&gt;&amp;&quot;pkg", special, StringComparison.Ordinal);
    }
}
