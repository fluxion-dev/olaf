using Olaf.Core;
using YamlDotNet.Serialization;

namespace Olaf.Formatters;

public sealed class YamlFormatter : ILicenseFormatter
{
    public string Format => "yaml";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var items = result.Licenses.Select(l => new Dictionary<string, string?>
        {
            ["ecosystem"] = l.Dependency.Ecosystem,
            ["name"] = l.Dependency.Name,
            ["version"] = l.Dependency.Version,
            ["spdx"] = l.SpdxId,
            ["licenseText"] = l.LicenseText,
            ["sourceUrl"] = l.SourceUrl,
            ["status"] = l.Status,
            ["reason"] = l.Reason,
        }).ToList();
        var serializer = new SerializerBuilder().Build();
        return serializer.Serialize(items);
    }
}
