using Olaf.Core;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #72 R4 (#123: md is the kept attribution format — txt/html
/// retired): holders-only <c>Copyright:</c> line per package in md, omitted
/// (never empty) when no holders and with no other enrichment dragged in.
/// Offline unit style — ScanResult constructed directly, no network; inline
/// strings only.
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
    public void Should_EmitOrOmitCopyright_When_Markdown()
    {
        var md = FormatterTestHelpers.ResolveFormatter("md").FormatResult(HoldersScanResult());
        Assert.Contains("- Copyright: Acme Corp; Jane Doe", md, StringComparison.Ordinal);
        Assert.DoesNotContain("pkg:npm/acme@1.0.0", md, StringComparison.Ordinal);

        var plainMd = FormatterTestHelpers.ResolveFormatter("md").FormatResult(UnenrichedScanResult());
        Assert.DoesNotContain("Copyright:", plainMd, StringComparison.Ordinal);
    }
}
