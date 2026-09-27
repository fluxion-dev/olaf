using Olaf.Core;
using YamlDotNet.Serialization;

namespace Olaf.Formatters;

public sealed class YamlFormatter : ILicenseFormatter
{
    public string Format => "yaml";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var items = FormatterSort.ByEcosystemNameVersion(result.Licenses)
            .Select(l => new Dictionary<string, string?>
            {
                ["ecosystem"] = l.Dependency.Ecosystem,
                ["name"] = l.Dependency.Name,
                ["version"] = l.Dependency.Version,
                ["spdx"] = l.SpdxId,
                ["licenseText"] = l.LicenseText,
                ["sourceUrl"] = l.SourceUrl,
                ["status"] = l.Status,
                ["reason"] = l.Reason,
                ["direct"] = l.Dependency.Direct ? "true" : "false",
            }).ToList();
        var doc = new Dictionary<string, object?>
        {
            ["summary"] = new Dictionary<string, object?>
            {
                ["total"] = result.TotalCount,
                ["resolved"] = result.ResolvedCount,
                ["unknown"] = result.UnknownCount,
            },
            ["licenses"] = items,
        };
        var serializer = new SerializerBuilder().Build();
        return serializer.Serialize(doc);
    }
}
