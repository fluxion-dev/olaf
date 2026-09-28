using Olaf.Core;

namespace Olaf.Formatters;

/// <summary>
/// Issue #73: precomputed template model. Exact field names are a plan
/// binding (docs + tests pin them); additive-only, no other shape changes.
/// </summary>
public sealed record TemplateLicenseEntry(
    string Name,
    string Version,
    string Ecosystem,
    bool Direct,
    string Spdx,
    string Status,
    string Reason,
    string SourceUrl,
    string Purl,
    string Supplier,
    string DownloadUrl,
    string Hashes,
    string Copyright);

public sealed record TemplateGroup(
    string Spdx,
    int Count,
    IReadOnlyList<TemplateLicenseEntry> Items);

public sealed record TemplateModel(
    int Total,
    int Resolved,
    int Unknown,
    string GeneratedAt,
    string ToolVersion,
    IReadOnlyList<TemplateLicenseEntry> Licenses,
    IReadOnlyList<TemplateGroup> Groups);

public static class TemplateModelBuilder
{
    /// <summary>
    /// Mirrors Olaf.Cli.csproj Version; const by plan binding (no reflection).
    /// </summary>
    public const string ToolVersionValue = "0.1.0-preview.1";

    public static TemplateModel Build(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var licenses = FormatterSort.ByEcosystemNameVersion(result.Licenses)
            .Select(ToEntry)
            .ToList();

        var groups = licenses
            .GroupBy(l => l.Spdx, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TemplateGroup(g.Key, g.Count(), g.ToList()))
            .ToList();

        return new TemplateModel(
            result.TotalCount,
            result.ResolvedCount,
            result.UnknownCount,
            DateTime.UtcNow.ToString("o"),
            ToolVersionValue,
            licenses,
            groups);
    }

    private static TemplateLicenseEntry ToEntry(ResolvedLicense l)
    {
        return new TemplateLicenseEntry(
            l.Dependency.Name,
            l.Dependency.Version,
            l.Dependency.Ecosystem,
            l.Dependency.Direct,
            LicenseDisplay.EffectiveSpdx(l),
            l.Status,
            l.Reason ?? string.Empty,
            l.SourceUrl ?? string.Empty,
            l.Enrichment?.Purl ?? string.Empty,
            l.Enrichment?.Supplier ?? string.Empty,
            l.Enrichment?.DownloadUrl ?? string.Empty,
            l.Enrichment?.Hashes is { Length: > 0 } hashes ? string.Join(";", hashes) : string.Empty,
            CopyrightHoldersFormat.Join(l.Enrichment?.CopyrightHolders) ?? string.Empty);
    }
}
