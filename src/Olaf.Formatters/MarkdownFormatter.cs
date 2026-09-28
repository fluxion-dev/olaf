using System.Text;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class MarkdownFormatter : ILicenseFormatter
{
    private readonly bool _groupByLicense;

    // Issue #74: constructor flag (default false) — default path byte-identical (B7).
    // Explicit parameterless overload: reflection-based resolution
    // (Activator.CreateInstance) requires a zero-parameter constructor.
    public MarkdownFormatter()
        : this(false)
    {
    }

    public MarkdownFormatter(bool groupByLicense)
    {
        _groupByLicense = groupByLicense;
    }

    public string Format => "md";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_groupByLicense)
        {
            return FormatGrouped(result);
        }
        // Issue #70: enrichment (purl/hashes/supplier/download) is consciously
        // omitted here — fixed-column human table; SBOM/structured formats
        // carry enrichment. Issue #72 exception: holders-only Copyright bullet
        // per detail section (omit-when-empty; the table columns are fixed).
        var sb = new StringBuilder();
        sb.AppendLine("# Third-Party Attribution");
        sb.AppendLine();
        sb.AppendLine($"Total: {result.TotalCount}, Resolved: {result.ResolvedCount}, Unknown: {result.UnknownCount}");
        var sorted = FormatterSort.ByEcosystemNameVersion(result.Licenses).ToList();
        if (sorted.Count == 0)
        {
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine("| Ecosystem | Name | Version | SPDX | License | Source | Status | Reason | Direct |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var l in sorted)
        {
            sb.Append("| ").Append(Cell(l.Dependency.Ecosystem))
                .Append(" | ").Append(Cell(l.Dependency.Name))
                .Append(" | ").Append(Cell(l.Dependency.Version))
                .Append(" | ").Append(Cell(LicenseDisplay.EffectiveSpdx(l)))
                .Append(" | ").Append(Cell(l.LicenseText))
                .Append(" | ").Append(Cell(l.SourceUrl))
                .Append(" | ").Append(Cell(l.Status))
                .Append(" | ").Append(Cell(l.Reason))
                .Append(" | ").Append(l.Dependency.Direct ? "true" : "false")
                .AppendLine(" |");
        }

        foreach (var l in sorted)
        {
            sb.AppendLine();
            sb.AppendLine($"## {l.Dependency.Name}@{l.Dependency.Version} ({l.Dependency.Ecosystem})");
            sb.AppendLine();
            sb.AppendLine($"- Ecosystem: {Cell(l.Dependency.Ecosystem)}");
            sb.AppendLine($"- Name: {Cell(l.Dependency.Name)}");
            sb.AppendLine($"- Version: {Cell(l.Dependency.Version)}");
            sb.AppendLine($"- SPDX: {Cell(LicenseDisplay.EffectiveSpdx(l))}");
            if (CopyrightHoldersFormat.Join(l.Enrichment?.CopyrightHolders) is { } copyright)
            {
                sb.AppendLine($"- Copyright: {Cell(copyright)}");
            }

            sb.AppendLine($"- License: {Cell(l.LicenseText)}");
            sb.AppendLine($"- Source: {Cell(l.SourceUrl)}");
            sb.AppendLine($"- Status: {Cell(l.Status)}");
            sb.AppendLine($"- Reason: {Cell(l.Reason)}");
            sb.AppendLine($"- Direct: {(l.Dependency.Direct ? "true" : "false")}");
        }

        return sb.ToString();
    }

    // Issue #74 grouped branch: separate method so the default branch above
    // stays byte-identical (B7). Header "X packages under Y licenses" (B4,
    // Y includes Unknown); bullets never vanish (B3).
    private static string FormatGrouped(ScanResult result)
    {
        var sb = new StringBuilder();
        var groups = LicenseGrouper.Group(result.Licenses);
        sb.AppendLine("# Third-Party Attribution");
        sb.AppendLine();
        sb.AppendLine($"{result.TotalCount} packages under {groups.Count} licenses");
        sb.AppendLine();
        sb.AppendLine($"Total: {result.TotalCount}, Resolved: {result.ResolvedCount}, Unknown: {result.UnknownCount}");
        foreach (var group in groups)
        {
            sb.AppendLine();
            sb.AppendLine($"## {Cell(group.Spdx)} ({group.Packages.Count} packages)");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(group.LicenseText))
            {
                sb.AppendLine(Cell(group.LicenseText));
                sb.AppendLine();
            }

            foreach (var l in group.Packages)
            {
                var bullet = $"- {Cell(l.Dependency.Name)}@{Cell(l.Dependency.Version)} ({Cell(l.Dependency.Ecosystem)})";
                if (LicenseGrouper.IsUnknown(group.Spdx))
                {
                    bullet += $": {Cell(l.Reason ?? string.Empty)}";
                    bullet = bullet.TrimEnd();
                }

                if (CopyrightHoldersFormat.Join(l.Enrichment?.CopyrightHolders) is { } copyright)
                {
                    bullet += $"; {Cell(copyright)}";
                }

                sb.AppendLine(bullet);
            }
        }

        return sb.ToString();
    }

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace("|", "\\|");
    }
}
