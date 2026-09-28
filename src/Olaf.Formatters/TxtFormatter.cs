using System.Text;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class TxtFormatter : ILicenseFormatter
{
    private readonly bool _groupByLicense;

    // Issue #74: constructor flag (default false) — default path byte-identical (B7).
    // Explicit parameterless overload: reflection-based resolution
    // (Activator.CreateInstance) requires a zero-parameter constructor.
    public TxtFormatter()
        : this(false)
    {
    }

    public TxtFormatter(bool groupByLicense)
    {
        _groupByLicense = groupByLicense;
    }

    public string Format => "txt";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_groupByLicense)
        {
            return FormatGrouped(result);
        }
        // Issue #70: enrichment (purl/hashes/supplier/download) is consciously
        // omitted here — fixed-line human attribution; SBOM/structured formats
        // carry enrichment. Issue #72 exception: holders-only Copyright line
        // per package (omit-when-empty, never an empty line).
        var sb = new StringBuilder();
        sb.AppendLine("Third-Party Attribution");
        sb.AppendLine($"Total: {result.TotalCount}, Resolved: {result.ResolvedCount}, Unknown: {result.UnknownCount}");
        foreach (var l in FormatterSort.ByEcosystemNameVersion(result.Licenses))
        {
            sb.AppendLine();
            sb.AppendLine($"{l.Dependency.Name}@{l.Dependency.Version} ({l.Dependency.Ecosystem}) direct={(l.Dependency.Direct ? "true" : "false")}");
            sb.AppendLine($"  SPDX: {LicenseDisplay.EffectiveSpdx(l)}");
            if (CopyrightHoldersFormat.Join(l.Enrichment?.CopyrightHolders) is { } copyright)
            {
                sb.AppendLine($"  Copyright: {copyright}");
            }

            if (!string.IsNullOrWhiteSpace(l.SourceUrl))
            {
                sb.AppendLine($"  Source: {l.SourceUrl}");
            }

            sb.AppendLine($"  Status: {l.Status}");
            if (!string.IsNullOrWhiteSpace(l.Reason))
            {
                sb.AppendLine($"  Reason: {l.Reason}");
            }
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
        sb.AppendLine("Third-Party Attribution");
        sb.AppendLine($"{result.TotalCount} packages under {groups.Count} licenses");
        sb.AppendLine($"Total: {result.TotalCount}, Resolved: {result.ResolvedCount}, Unknown: {result.UnknownCount}");
        foreach (var group in groups)
        {
            sb.AppendLine();
            sb.AppendLine($"## {group.Spdx} ({group.Packages.Count} packages)");
            if (!string.IsNullOrWhiteSpace(group.LicenseText))
            {
                sb.AppendLine($"License: {group.LicenseText}");
            }

            foreach (var l in group.Packages)
            {
                var bullet = $"- {l.Dependency.Name}@{l.Dependency.Version} ({l.Dependency.Ecosystem})";
                if (LicenseGrouper.IsUnknown(group.Spdx))
                {
                    bullet += $": {l.Reason ?? string.Empty}";
                    bullet = bullet.TrimEnd();
                }

                if (CopyrightHoldersFormat.Join(l.Enrichment?.CopyrightHolders) is { } copyright)
                {
                    bullet += $"; {copyright}";
                }

                sb.AppendLine(bullet);
            }
        }

        return sb.ToString();
    }
}
