using System.Text;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class MarkdownFormatter : ILicenseFormatter
{
    public string Format => "md";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
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
            sb.AppendLine($"- License: {Cell(l.LicenseText)}");
            sb.AppendLine($"- Source: {Cell(l.SourceUrl)}");
            sb.AppendLine($"- Status: {Cell(l.Status)}");
            sb.AppendLine($"- Reason: {Cell(l.Reason)}");
            sb.AppendLine($"- Direct: {(l.Dependency.Direct ? "true" : "false")}");
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
