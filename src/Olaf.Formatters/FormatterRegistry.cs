using Olaf.Core;

namespace Olaf.Formatters;

// Issue #123: canonical formats (7, zero aliases).
public sealed class FormatterRegistry
{
    public const string SupportedFormats = "json|yaml|xml|md|cyclonedx-json|cyclonedx-xml|spdx-json";

    public ILicenseFormatter GetFormatter(string format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        return format.ToLowerInvariant() switch
        {
            "json" => new JsonFormatter(),
            "yaml" => new YamlFormatter(),
            "xml" => new XmlFormatter(),
            "md" => new MarkdownFormatter(),
            "cyclonedx-json" => new CycloneDxFormatter(),
            "cyclonedx-xml" => new CycloneDxXmlFormatter(),
            "spdx-json" => new SpdxJsonFormatter(),
            _ => throw new ArgumentException($"Unsupported format '{format}'. Supported: {SupportedFormats}.", nameof(format)),
        };
    }
}
