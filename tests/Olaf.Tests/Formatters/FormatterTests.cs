using System.Reflection;
using System.Text.Json;
using System.Xml;
using Olaf.Core;

namespace Olaf.Tests.Formatters;

/// <summary>
/// TDD Red helpers for output formatters.
/// The test assembly compiles while Olaf.Formatters is still empty:
/// resolution is via reflection, missing impl throws NotImplementedException
/// (the *right* Red failure, not a compile error). No formatter logic lives here.
/// Contract under test: ILicenseFormatter.FormatResult(ScanResult) -> string.
/// </summary>
internal static class FormatterTestHelpers
{
    internal static void EnsureFormattersLoaded()
    {
        try
        {
            AppDomain.CurrentDomain.Load("Olaf.Formatters");
        }
        catch
        {
        }
    }

    /// <summary>
    /// Resolve an ILicenseFormatter for the given format. Matches on the
    /// <see cref="ILicenseFormatter.Format"/> property (case-insensitive),
    /// falling back to type-name convention (*JsonFormatter*, ...).
    /// Throws NotImplementedException when the formatter is missing (Red).
    /// </summary>
    internal static ILicenseFormatter ResolveFormatter(string format)
    {
        EnsureFormattersLoaded();
        var candidates = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => typeof(ILicenseFormatter).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .ToList();

        foreach (var type in candidates)
        {
            var instance = TryCreate(type);
            if (instance is not null && string.Equals(instance.Format, format, StringComparison.OrdinalIgnoreCase))
            {
                return instance;
            }
        }

        var byName = candidates.FirstOrDefault(t =>
            t.Name.Contains(format, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            var instance = TryCreate(byName);
            if (instance is not null)
            {
                return instance;
            }
        }

        throw new NotImplementedException($"No ILicenseFormatter for '{format}' (missing formatter).");
    }

    internal static Type RequireRegistryType()
    {
        EnsureFormattersLoaded();
        var registry = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => string.Equals(t.Name, "FormatterRegistry", StringComparison.Ordinal));

        if (registry is null)
        {
            throw new NotImplementedException("FormatterRegistry not implemented (missing --format selection).");
        }

        return registry;
    }

    /// <summary>
    /// Resolve a formatter through FormatterRegistry via reflection.
    /// Probes GetFormatter/Create/CreateFormatter/Resolve/Get(string) returning
    /// ILicenseFormatter, else a static method of the same shape. Throws
    /// NotImplementedException when the registry (or a usable selector) is missing.
    /// </summary>
    internal static ILicenseFormatter GetFormatterViaRegistry(string format)
    {
        var registryType = RequireRegistryType();

        var method = registryType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(m =>
                typeof(ILicenseFormatter).IsAssignableFrom(m.ReturnType)
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(string));

        if (method is null)
        {
            throw new NotImplementedException("FormatterRegistry has no (string)->ILicenseFormatter selector (missing --format selection).");
        }

        object? target = null;
        if (!method.IsStatic)
        {
            try
            {
                target = Activator.CreateInstance(registryType);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is NotImplementedException)
            {
                throw ex.InnerException;
            }

            if (target is null)
            {
                throw new NotImplementedException("FormatterRegistry has no parameterless constructor (missing --format selection).");
            }
        }

        try
        {
            return (ILicenseFormatter)method.Invoke(target, new object?[] { format })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is NotImplementedException)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>
    /// Canonical mixed fixture: 1 Resolved MIT package with license text +
    /// 1 Unknown package. All formatter tests share it so field coverage
    /// (name/version/spdx/licenseText/status) is asserted uniformly.
    /// </summary>
    internal static ScanResult SampleScanResult() => new(new List<ResolvedLicense>
    {
        new(
            new Dependency("npm", "express", "4.18.2", false),
            "MIT",
            "MIT License\nPermission is hereby granted, free of charge,",
            "https://example.com/express/LICENSE",
            "Resolved",
            null),
        new(
            new Dependency("npm", "mystery-pkg", "1.0.0", false),
            null,
            null,
            null,
            "Unknown",
            "not-found: no license for 'mystery-pkg 1.0.0'."),
    });

    internal static ScanResult EscapingScanResult() => new(new List<ResolvedLicense>
    {
        new(
            new Dependency("npm", "evil<script>&\"pkg", "1.0.0 <", false),
            "MIT",
            "text with <b>markup</b> & \"quotes\"",
            "https://example.com/?a=1&b=2",
            "Resolved",
            null),
    });

    private static ILicenseFormatter? TryCreate(Type type)
    {
        try
        {
            return Activator.CreateInstance(type) as ILicenseFormatter;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is NotImplementedException)
        {
            throw ex.InnerException;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
    }
}

/// <summary>
/// TDD Red: JsonFormatter. No live network — ScanResult is constructed directly.
/// </summary>
public sealed class JsonFormatterTests
{
    [Fact]
    public void Should_ProduceValidJson_When_ScanResultHasMixedLicenses()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("json");

        var output = formatter.FormatResult(FormatterTestHelpers.SampleScanResult());

        using var doc = JsonDocument.Parse(output); // throws if not valid JSON
        Assert.Contains("express", output, StringComparison.Ordinal);
        Assert.Contains("4.18.2", output, StringComparison.Ordinal);
        Assert.Contains("MIT", output, StringComparison.Ordinal);
        Assert.Contains("mystery-pkg", output, StringComparison.Ordinal);
        Assert.Contains("Unknown", output, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ProduceEmptyArray_When_ScanResultEmpty()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("json");

        var output = formatter.FormatResult(ScanResult.Empty);

        using var doc = JsonDocument.Parse(output); // throws if not valid JSON
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }
}

/// <summary>
/// TDD Red: YamlFormatter. Structural assertions only (no extra YAML dep):
/// output must be non-empty multi-line text with mapping markers carrying
/// every field of the mixed fixture. A real YAML parse check belongs to the
/// Green phase once a YAML library is referenced.
/// </summary>
public sealed class YamlFormatterTests
{
    [Fact]
    public void Should_ProduceValidYaml_When_ScanResultHasMixedLicenses()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("yaml");

        var output = formatter.FormatResult(FormatterTestHelpers.SampleScanResult());

        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.Contains('\n', output.ToString());
        Assert.Contains(':', output.ToString());
        Assert.Contains("express", output, StringComparison.Ordinal);
        Assert.Contains("4.18.2", output, StringComparison.Ordinal);
        Assert.Contains("MIT", output, StringComparison.Ordinal);
        Assert.Contains("mystery-pkg", output, StringComparison.Ordinal);
        Assert.Contains("Unknown", output, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ProduceValidYaml_When_ScanResultEmpty()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("yaml");

        var output = formatter.FormatResult(ScanResult.Empty);

        Assert.NotNull(output);
        Assert.DoesNotContain("express", output, StringComparison.Ordinal);
    }
}

/// <summary>
/// TDD Red: XmlFormatter. Well-formedness is verified with XmlDocument (in-box).
/// </summary>
public sealed class XmlFormatterTests
{
    [Fact]
    public void Should_ProduceWellFormedXml_When_ScanResultHasMixedLicenses()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("xml");

        var output = formatter.FormatResult(FormatterTestHelpers.SampleScanResult());

        var doc = new XmlDocument();
        doc.LoadXml(output); // throws if not well-formed XML
        Assert.NotNull(doc.DocumentElement);
        Assert.Contains("express", output, StringComparison.Ordinal);
        Assert.Contains("4.18.2", output, StringComparison.Ordinal);
        Assert.Contains("MIT", output, StringComparison.Ordinal);
        Assert.Contains("mystery-pkg", output, StringComparison.Ordinal);
        Assert.Contains("Unknown", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ProduceWellFormedXml_When_ScanResultEmpty()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("xml");

        var output = formatter.FormatResult(ScanResult.Empty);

        var doc = new XmlDocument();
        doc.LoadXml(output); // empty must still be well-formed XML
        Assert.NotNull(doc.DocumentElement);
        Assert.DoesNotContain("express", output, StringComparison.Ordinal);
    }
}

/// <summary>
/// TDD Red: HtmlFormatter.
/// </summary>
public sealed class HtmlFormatterTests
{
    [Fact]
    public void Should_ContainTableWithHeaders_When_ScanResultHasMixedLicenses()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("html");

        var output = formatter.FormatResult(FormatterTestHelpers.SampleScanResult());

        Assert.Contains("<table", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Name", output, StringComparison.Ordinal);
        Assert.Contains("Version", output, StringComparison.Ordinal);
        Assert.Contains("License", output, StringComparison.Ordinal);
        Assert.Contains("express", output, StringComparison.Ordinal);
        Assert.Contains("4.18.2", output, StringComparison.Ordinal);
        Assert.Contains("MIT", output, StringComparison.Ordinal);
        Assert.Contains("mystery-pkg", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_EscapeHtml_When_FieldsContainMarkup()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("html");

        var output = formatter.FormatResult(FormatterTestHelpers.EscapingScanResult());

        Assert.DoesNotContain("<script>", output, StringComparison.Ordinal);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("evil<script>&\"pkg"), output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ProduceValidHtml_When_ScanResultEmpty()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("html");

        var output = formatter.FormatResult(ScanResult.Empty);

        Assert.Contains("<table", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("express", output, StringComparison.Ordinal);
    }
}

/// <summary>
/// TDD Red: FormatterRegistry backs --format json|yaml|xml|html selection.
/// </summary>
public sealed class FormatterRegistryTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("xml")]
    [InlineData("html")]
    public void Should_ResolveFormatter_When_FormatSupported(string format)
    {
        var formatter = FormatterTestHelpers.GetFormatterViaRegistry(format);

        Assert.NotNull(formatter);
        Assert.Equal(format, formatter.Format, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Should_Throw_When_FormatUnsupported()
    {
        try
        {
            FormatterTestHelpers.GetFormatterViaRegistry("toml");
        }
        catch (NotImplementedException)
        {
            throw; // Red: registry missing — fail with NotImplemented, not a pass.
        }
        catch
        {
            return; // Expected: unsupported format rejected.
        }

        Assert.Fail("Expected error for unsupported format 'toml'.");
    }
}
