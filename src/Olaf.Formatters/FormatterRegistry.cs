using Olaf.Core;

namespace Olaf.Formatters;

public sealed class FormatterRegistry
{
    public ILicenseFormatter GetFormatter(string format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        return format.ToLowerInvariant() switch
        {
            "json" => new JsonFormatter(),
            "yaml" => new YamlFormatter(),
            "xml" => new XmlFormatter(),
            "html" => new HtmlFormatter(),
            "txt" => new TxtFormatter(),
            "md" => new MarkdownFormatter(),
            "markdown" => new MarkdownFormatter(),
            "cyclonedx-json" => new CycloneDxFormatter(),
            "cyclonedx" => new CycloneDxFormatter(),
            _ => throw new ArgumentException($"Unsupported format '{format}'. Supported: json|yaml|xml|html|txt|md|cyclonedx-json|cyclonedx.", nameof(format)),
        };
    }
}
