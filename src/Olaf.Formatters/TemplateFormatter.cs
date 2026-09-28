using Olaf.Core;

namespace Olaf.Formatters;

/// <summary>
/// Issue #73: renders a <see cref="ScanResult"/> through a user-supplied
/// template string. Format value is "template".
/// </summary>
public sealed class TemplateFormatter : ILicenseFormatter
{
    private readonly string _template;

    public TemplateFormatter(string template)
    {
        _template = template;
    }

    public string Format => "template";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return TemplateEngine.Render(_template, TemplateModelBuilder.Build(result));
    }
}
