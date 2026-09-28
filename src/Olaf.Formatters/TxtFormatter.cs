using System.Text;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class TxtFormatter : ILicenseFormatter
{
    public string Format => "txt";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
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
}
