using Olaf.Core;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #72 R4: attribution outputs — holders-only <c>Copyright:</c> line
/// per package in txt/md/html, omitted (never empty) when no holders and
/// with no other enrichment dragged in. Offline unit style — ScanResult
/// constructed directly, no network; inline strings only.
/// </summary>
public sealed class CopyrightAttributionTests
{
    private static ScanResult HoldersScanResult() => new(new List<ResolvedLicense>
    {
        new(
            new Dependency("npm", "acme", "1.0.0", false),
            "MIT",
            "MIT License",
            "https://example.com/acme",
            "Resolved",
            null,
            new Enrichment(
                "pkg:npm/acme@1.0.0",
                new[] { "sha512:abc==" },
                "Acme Corp",
                "https://example.com/acme-1.0.0.tgz",
                new[] { "Acme Corp", "Jane Doe" })),
    });

    private static ScanResult UnenrichedScanResult() => new(new List<ResolvedLicense>
    {
        new(new Dependency("npm", "acme", "1.0.0", false), "MIT", null, null, "Resolved", null),
    });

    [Fact]
    public void Should_EmitOrOmitCopyright_When_TxtAndMarkdown()
    {
        var txt = FormatterTestHelpers.ResolveFormatter("txt").FormatResult(HoldersScanResult());
        Assert.Contains("  Copyright: Acme Corp; Jane Doe", txt, StringComparison.Ordinal);
        // Holders only: no other enrichment leaks into the human formats.
        Assert.DoesNotContain("pkg:npm/acme@1.0.0", txt, StringComparison.Ordinal);
        Assert.DoesNotContain("sha512", txt, StringComparison.Ordinal);
        Assert.DoesNotContain("acme-1.0.0.tgz", txt, StringComparison.Ordinal);

        var md = FormatterTestHelpers.ResolveFormatter("md").FormatResult(HoldersScanResult());
        Assert.Contains("- Copyright: Acme Corp; Jane Doe", md, StringComparison.Ordinal);
        Assert.DoesNotContain("pkg:npm/acme@1.0.0", md, StringComparison.Ordinal);

        var plainTxt = FormatterTestHelpers.ResolveFormatter("txt").FormatResult(UnenrichedScanResult());
        Assert.DoesNotContain("Copyright:", plainTxt, StringComparison.Ordinal);

        var plainMd = FormatterTestHelpers.ResolveFormatter("md").FormatResult(UnenrichedScanResult());
        Assert.DoesNotContain("Copyright:", plainMd, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_EmitOrOmitCopyright_When_Html()
    {
        var html = FormatterTestHelpers.ResolveFormatter("html").FormatResult(HoldersScanResult());
        Assert.Contains("<p>Copyright: acme@1.0.0: Acme Corp; Jane Doe</p>", html, StringComparison.Ordinal);
        // Holders only: no other enrichment leaks into the human format.
        Assert.DoesNotContain("pkg:npm/acme@1.0.0", html, StringComparison.Ordinal);
        Assert.DoesNotContain("sha512", html, StringComparison.Ordinal);

        var plainHtml = FormatterTestHelpers.ResolveFormatter("html").FormatResult(UnenrichedScanResult());
        Assert.DoesNotContain("Copyright:", plainHtml, StringComparison.Ordinal);
    }
}
