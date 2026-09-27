using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Xml;
using Olaf.Core;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Shared helpers for output formatter tests.
/// Resolution is via reflection against Olaf.Formatters.
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
    /// Throws NotImplementedException when the formatter is missing.
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

    /// <summary>
    /// Unsorted variant of <see cref="SampleScanResult"/> (mystery-pkg first).
    /// Pins stable sort (ecosystem, name, version); asserted by sort-stability test.
    /// </summary>
    internal static ScanResult UnsortedSampleScanResult() => new(new List<ResolvedLicense>
    {
        new(
            new Dependency("npm", "mystery-pkg", "1.0.0", false),
            null,
            null,
            null,
            "Unknown",
            "not-found: no license for 'mystery-pkg 1.0.0'."),
        new(
            new Dependency("npm", "express", "4.18.2", false),
            "MIT",
            "MIT License\nPermission is hereby granted, free of charge,",
            "https://example.com/express/LICENSE",
            "Resolved",
            null),
    });

    // Consistent report contract (8 fields) — wired into every mixed test.
    internal static void AssertEightFieldsPresent(string output, string format)
    {
        Assert.NotNull(output);
        switch (format.ToLowerInvariant())
        {
            case "json":
            {
                using var doc = JsonDocument.Parse(output);
                var first = doc.RootElement.GetProperty("licenses").EnumerateArray().First();
                foreach (var key in new[] { "ecosystem", "name", "version", "spdx", "licenseText", "sourceUrl", "status", "reason" })
                {
                    Assert.True(first.TryGetProperty(key, out _), $"JSON licenses[0] missing key '{key}'.");
                }

                break;
            }

            case "yaml":
                foreach (var marker in new[] { "ecosystem:", "name:", "version:", "spdx:", "licenseText:", "sourceUrl:", "status:", "reason:" })
                {
                    Assert.Contains(marker, output, StringComparison.Ordinal);
                }

                break;
            case "xml":
            {
                var doc = new XmlDocument();
                doc.LoadXml(output);
                foreach (var name in new[] { "ecosystem", "name", "version", "spdx", "licenseText", "sourceUrl", "status", "reason" })
                {
                    var nodes = doc.SelectNodes($"//{name}");
                    Assert.NotNull(nodes);
                    Assert.True(nodes.Count > 0, $"XML missing element '<{name}>'.");
                }

                break;
            }

            case "html":
                foreach (var header in new[] { "<th>Ecosystem</th>", "<th>Name</th>", "<th>Version</th>", "<th>SPDX</th>", "<th>License</th>", "<th>Source</th>", "<th>Status</th>", "<th>Reason</th>" })
                {
                    Assert.Contains(header, output, StringComparison.Ordinal);
                }

                break;
            case "md":
            case "markdown":
                foreach (var header in new[] { "| Ecosystem |", "| Name |", "| Version |", "| SPDX |", "| License |", "| Source |", "| Status |", "| Reason |" })
                {
                    Assert.Contains(header, output, StringComparison.Ordinal);
                }

                break;
            default:
                Assert.Fail($"Unknown format '{format}' in AssertEightFieldsPresent.");
                break;
        }
    }

    // Direct-field contract (issue #66) — additive `direct` surface on every
    // format, appended LAST. Wired into every direct-field test; the legacy
    // AssertEightFieldsPresent above stays subset-safe and untouched.
    internal static void AssertDirectFieldPresent(string output, string format)
    {
        Assert.NotNull(output);
        switch (format.ToLowerInvariant())
        {
            case "json":
            {
                using var doc = JsonDocument.Parse(output);
                var first = doc.RootElement.GetProperty("licenses").EnumerateArray().First();
                Assert.True(first.TryGetProperty("direct", out var direct), "JSON licenses[0] missing key 'direct'.");
                Assert.True(
                    direct.ValueKind == JsonValueKind.True || direct.ValueKind == JsonValueKind.False,
                    "JSON licenses[0]['direct'] must be a boolean.");
                break;
            }

            case "yaml":
                Assert.Contains("direct:", output, StringComparison.Ordinal);
                break;
            case "xml":
            {
                var doc = new XmlDocument();
                doc.LoadXml(output);
                var nodes = doc.SelectNodes("//direct");
                Assert.NotNull(nodes);
                Assert.True(nodes.Count > 0, "XML missing element '<direct>'.");
                break;
            }

            case "html":
                Assert.Contains("<th>Direct</th>", output, StringComparison.Ordinal);
                break;
            case "txt":
                Assert.Contains("direct=", output, StringComparison.Ordinal);
                break;
            case "md":
            case "markdown":
                Assert.Contains("| Direct |", output, StringComparison.Ordinal);
                break;
            default:
                Assert.Fail($"Unknown format '{format}' in AssertDirectFieldPresent.");
                break;
        }
    }

    // Summary counts — wired into every mixed/empty test. Format is
    // detected from the output shape (json object vs xml vs html vs yaml).
    internal static void AssertSummaryCounts(string output, int total = 2, int resolved = 1, int unknown = 1)
    {
        Assert.NotNull(output);
        var trimmed = output.TrimStart();
        if (trimmed.StartsWith('#'))
        {
            Assert.Contains($"Total: {total}", output, StringComparison.Ordinal);
            Assert.Contains($"Resolved: {resolved}", output, StringComparison.Ordinal);
            Assert.Contains($"Unknown: {unknown}", output, StringComparison.Ordinal);
            return;
        }

        if (trimmed.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(output);
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(total, summary.GetProperty("total").GetInt32());
            Assert.Equal(resolved, summary.GetProperty("resolved").GetInt32());
            Assert.Equal(unknown, summary.GetProperty("unknown").GetInt32());
        }
        else if (trimmed.StartsWith('<'))
        {
            // HTML envelope first: summary rendered as paragraph text (not XML).
            if (output.Contains("<html", StringComparison.OrdinalIgnoreCase)
                || output.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || output.Contains("<table", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains($"Total: {total}", output, StringComparison.Ordinal);
                Assert.Contains($"Resolved: {resolved}", output, StringComparison.Ordinal);
                Assert.Contains($"Unknown: {unknown}", output, StringComparison.Ordinal);
                return;
            }

            var doc = new XmlDocument();
            doc.LoadXml(output);
            var summary = doc.SelectSingleNode("/report/summary");
            Assert.NotNull(summary);
            Assert.Equal(total.ToString(), summary.Attributes!["total"]!.Value);
            Assert.Equal(resolved.ToString(), summary.Attributes!["resolved"]!.Value);
            Assert.Equal(unknown.ToString(), summary.Attributes!["unknown"]!.Value);
        }
        else
        {
            // YAML envelope: summary mapping with total:/resolved:/unknown:.
            Assert.Contains("summary:", output, StringComparison.Ordinal);
            Assert.Contains($"total: {total}", output, StringComparison.Ordinal);
            Assert.Contains($"resolved: {resolved}", output, StringComparison.Ordinal);
            Assert.Contains($"unknown: {unknown}", output, StringComparison.Ordinal);
        }
    }

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
/// JsonFormatter: no live network — ScanResult is constructed directly.
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

        // Envelope (summary + licenses) with 8-column rows.
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("resolved").GetInt32());
        Assert.Equal(1, summary.GetProperty("unknown").GetInt32());
        var licenses = doc.RootElement.GetProperty("licenses").EnumerateArray().ToList();
        Assert.Equal(2, licenses.Count);
        var first = licenses[0];
        foreach (var key in new[] { "ecosystem", "name", "version", "spdx", "licenseText", "sourceUrl", "status", "reason" })
        {
            Assert.True(first.TryGetProperty(key, out _), $"licenses[0] missing key '{key}'.");
        }

        Assert.Equal("npm", first.GetProperty("ecosystem").GetString());
        Assert.Equal("https://example.com/express/LICENSE", first.GetProperty("sourceUrl").GetString());
        Assert.Equal("not-found: no license for 'mystery-pkg 1.0.0'.", licenses[1].GetProperty("reason").GetString());

        FormatterTestHelpers.AssertEightFieldsPresent(output, "json");
        FormatterTestHelpers.AssertSummaryCounts(output);
    }

    [Fact]
    public void Should_ProduceEmptyArray_When_ScanResultEmpty()
    {
        // Empty result is an envelope with zero summary counts and empty
        // licenses, NOT a root array.
        var formatter = FormatterTestHelpers.ResolveFormatter("json");

        var output = formatter.FormatResult(ScanResult.Empty);

        using var doc = JsonDocument.Parse(output); // throws if not valid JSON
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("total").GetInt32());
        Assert.Equal(0, summary.GetProperty("resolved").GetInt32());
        Assert.Equal(0, summary.GetProperty("unknown").GetInt32());
        var licenses = doc.RootElement.GetProperty("licenses");
        Assert.Equal(JsonValueKind.Array, licenses.ValueKind);
        Assert.Equal(0, licenses.GetArrayLength());

        FormatterTestHelpers.AssertSummaryCounts(output, total: 0, resolved: 0, unknown: 0);
    }
}

/// <summary>
/// YamlFormatter: structural assertions only (no extra YAML dep):
/// output must be non-empty multi-line text with mapping markers carrying
/// every field of the mixed fixture.
/// </summary>
public sealed class YamlFormatterTests
{
    [Fact]
    public void Should_ProduceValidYaml_When_ScanResultHasMixedLicenses()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("yaml");

        var output = formatter.FormatResult(FormatterTestHelpers.SampleScanResult());

        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.Contains('\n', output);
        Assert.Contains(':', output);
        Assert.Contains("express", output, StringComparison.Ordinal);
        Assert.Contains("4.18.2", output, StringComparison.Ordinal);
        Assert.Contains("MIT", output, StringComparison.Ordinal);
        Assert.Contains("mystery-pkg", output, StringComparison.Ordinal);
        Assert.Contains("Unknown", output, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted", output, StringComparison.Ordinal);

        // Envelope summary + 8-column row markers.
        Assert.Contains("summary:", output, StringComparison.Ordinal);
        Assert.Contains("total: 2", output, StringComparison.Ordinal);
        Assert.Contains("resolved: 1", output, StringComparison.Ordinal);
        Assert.Contains("unknown: 1", output, StringComparison.Ordinal);
        Assert.Contains("ecosystem:", output, StringComparison.Ordinal);
        Assert.Contains("sourceUrl:", output, StringComparison.Ordinal);
        Assert.Contains("reason:", output, StringComparison.Ordinal);

        FormatterTestHelpers.AssertEightFieldsPresent(output, "yaml");
        FormatterTestHelpers.AssertSummaryCounts(output);
    }

    [Fact]
    public void Should_ProduceValidYaml_When_ScanResultEmpty()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("yaml");

        var output = formatter.FormatResult(ScanResult.Empty);

        Assert.NotNull(output);
        Assert.DoesNotContain("express", output, StringComparison.Ordinal);
        // Empty envelope parses (summary zeros, no license rows).
        Assert.Contains("summary:", output, StringComparison.Ordinal);
        Assert.Contains("total: 0", output, StringComparison.Ordinal);
        Assert.Contains("resolved: 0", output, StringComparison.Ordinal);
        Assert.Contains("unknown: 0", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ecosystem:", output, StringComparison.Ordinal);

        FormatterTestHelpers.AssertSummaryCounts(output, total: 0, resolved: 0, unknown: 0);
    }
}

/// <summary>
/// XmlFormatter: well-formedness is verified with XmlDocument (in-box).
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

        // <summary> counts + 8 element names via SelectNodes.
        Assert.Contains("<summary", output, StringComparison.Ordinal);
        var summary = doc.SelectSingleNode("/report/summary");
        Assert.NotNull(summary);
        Assert.Equal("2", summary.Attributes!["total"]!.Value);
        Assert.Equal("1", summary.Attributes!["resolved"]!.Value);
        Assert.Equal("1", summary.Attributes!["unknown"]!.Value);
        foreach (var name in new[] { "ecosystem", "name", "version", "spdx", "licenseText", "sourceUrl", "status", "reason" })
        {
            var nodes = doc.SelectNodes($"//{name}");
            Assert.NotNull(nodes);
            Assert.True(nodes.Count > 0, $"Missing element '<{name}>'.");
        }

        Assert.Equal(2, doc.SelectNodes("//license")!.Count);

        FormatterTestHelpers.AssertEightFieldsPresent(output, "xml");
        FormatterTestHelpers.AssertSummaryCounts(output);
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
        // Empty envelope well-formed with zero counts, no rows.
        var summary = doc.SelectSingleNode("/report/summary");
        Assert.NotNull(summary);
        Assert.Equal("0", summary.Attributes!["total"]!.Value);
        Assert.Equal("0", summary.Attributes!["resolved"]!.Value);
        Assert.Equal("0", summary.Attributes!["unknown"]!.Value);
        Assert.Equal(0, doc.SelectNodes("//license")!.Count);

        FormatterTestHelpers.AssertSummaryCounts(output, total: 0, resolved: 0, unknown: 0);
    }
}

/// <summary>
/// HtmlFormatter.
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

        // Full 8-column headers + summary + encoded sourceUrl/reason.
        Assert.Contains("<th>Ecosystem</th>", output, StringComparison.Ordinal);
        Assert.Contains("<th>Reason</th>", output, StringComparison.Ordinal);
        Assert.Contains("Total: 2", output, StringComparison.Ordinal);
        Assert.Contains("Resolved: 1", output, StringComparison.Ordinal);
        Assert.Contains("Unknown: 1", output, StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode("https://example.com/express/LICENSE"), output, StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode("not-found: no license for 'mystery-pkg 1.0.0'."), output, StringComparison.Ordinal);

        FormatterTestHelpers.AssertEightFieldsPresent(output, "html");
        FormatterTestHelpers.AssertSummaryCounts(output);
    }

    [Fact]
    public void Should_EscapeHtml_When_FieldsContainMarkup()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("html");

        var output = formatter.FormatResult(FormatterTestHelpers.EscapingScanResult());

        Assert.DoesNotContain("<script>", output, StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode("evil<script>&\"pkg"), output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_FallbackToUnknown_When_SpdxIsWhitespace()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("html");

        var result = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "ws-pkg", "1.0.0", false),
                "   ",
                null,
                null,
                "Resolved",
                null),
        });
        var output = formatter.FormatResult(result);

        Assert.Contains(">Unknown<", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ProduceValidHtml_When_ScanResultEmpty()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("html");

        var output = formatter.FormatResult(ScanResult.Empty);

        Assert.Contains("<table", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("express", output, StringComparison.Ordinal);
        // Empty envelope keeps table with zero-count summary.
        Assert.Contains("Total: 0", output, StringComparison.Ordinal);

        FormatterTestHelpers.AssertSummaryCounts(output, total: 0, resolved: 0, unknown: 0);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("xml")]
    [InlineData("html")]
    [InlineData("txt")]
    [InlineData("md")]
    [InlineData("markdown")]
    public void Should_SortByEcosystemNameVersion_When_InputUnsorted(string format)
    {
        // Stable sort (ecosystem, name, version) — express before mystery-pkg in every format.
        var formatter = FormatterTestHelpers.ResolveFormatter(format);

        var output = formatter.FormatResult(FormatterTestHelpers.UnsortedSampleScanResult());

        var express = output.IndexOf("express", StringComparison.Ordinal);
        var mystery = output.IndexOf("mystery-pkg", StringComparison.Ordinal);
        Assert.True(express >= 0, $"[{format}] missing 'express'.");
        Assert.True(mystery >= 0, $"[{format}] missing 'mystery-pkg'.");
        Assert.True(express < mystery, $"[{format}] expected 'express' before 'mystery-pkg'.");
    }
}

/// <summary>
/// FormatterRegistry backs --format json|yaml|xml|html selection.
/// </summary>
public sealed class FormatterRegistryTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("xml")]
    [InlineData("html")]
    [InlineData("txt")]
    [InlineData("md")]
    public void Should_ResolveFormatter_When_FormatSupported(string format)
    {
        var formatter = FormatterTestHelpers.GetFormatterViaRegistry(format);

        Assert.NotNull(formatter);
        Assert.Equal(format, formatter.Format, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Should_ResolveMarkdownAlias_When_FormatIsMarkdown()
    {
        var formatter = FormatterTestHelpers.GetFormatterViaRegistry("markdown");

        Assert.NotNull(formatter);
        Assert.Equal("md", formatter.Format, StringComparer.OrdinalIgnoreCase);
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
            throw; // Registry missing — fail with NotImplemented, not a pass.
        }
        catch
        {
            return; // Expected: unsupported format rejected.
        }

        Assert.Fail("Expected error for unsupported format 'toml'.");
    }
}

/// <summary>
/// TxtFormatter: human-readable per-package blocks with header summary.
/// </summary>
public sealed class TxtFormatterTests
{
    [Fact]
    public void Should_ProduceBlocksWithSummary_When_ScanResultHasMixedLicenses()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("txt");

        var output = formatter.FormatResult(FormatterTestHelpers.SampleScanResult());

        Assert.False(string.IsNullOrWhiteSpace(output));
        // Header summary.
        Assert.Contains("Total: 2", output, StringComparison.Ordinal);
        Assert.Contains("Resolved: 1", output, StringComparison.Ordinal);
        Assert.Contains("Unknown: 1", output, StringComparison.Ordinal);
        // Per-package blocks: name@version (ecosystem).
        Assert.Contains("express@4.18.2 (npm)", output, StringComparison.Ordinal);
        Assert.Contains("mystery-pkg@1.0.0 (npm)", output, StringComparison.Ordinal);
        // SPDX + source + status/reason coverage (8-field contract in text form).
        Assert.Contains("MIT", output, StringComparison.Ordinal);
        Assert.Contains("https://example.com/express/LICENSE", output, StringComparison.Ordinal);
        Assert.Contains("Resolved", output, StringComparison.Ordinal);
        Assert.Contains("Unknown", output, StringComparison.Ordinal);
        Assert.Contains("not-found: no license for 'mystery-pkg 1.0.0'.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_FallbackToUnknown_When_SpdxIsNull()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("txt");

        var output = formatter.FormatResult(FormatterTestHelpers.SampleScanResult());

        // mystery-pkg has null SpdxId — the SPDX line must fall back to Unknown.
        var block = output.Substring(output.IndexOf("mystery-pkg@1.0.0", StringComparison.Ordinal));
        Assert.Contains("SPDX: Unknown", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ProduceHeaderOnly_When_ScanResultEmpty()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("txt");

        var output = formatter.FormatResult(ScanResult.Empty);

        Assert.Contains("Total: 0", output, StringComparison.Ordinal);
        Assert.Contains("Resolved: 0", output, StringComparison.Ordinal);
        Assert.Contains("Unknown: 0", output, StringComparison.Ordinal);
        Assert.DoesNotContain("express", output, StringComparison.Ordinal);
        Assert.DoesNotContain("@", output, StringComparison.Ordinal);
    }
}

/// <summary>
/// MarkdownFormatter: # Third-Party Attribution header, summary, table plus
/// per-package sections carrying all 8 fields.
/// </summary>
public sealed class MarkdownFormatterTests
{
    [Fact]
    public void Should_ProduceAttributionDoc_When_ScanResultHasMixedLicenses()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("md");

        var output = formatter.FormatResult(FormatterTestHelpers.SampleScanResult());

        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.Contains("# Third-Party Attribution", output, StringComparison.Ordinal);
        Assert.Contains("Total: 2", output, StringComparison.Ordinal);
        Assert.Contains("Resolved: 1", output, StringComparison.Ordinal);
        Assert.Contains("Unknown: 1", output, StringComparison.Ordinal);
        // Table headers cover the 8-field contract.
        foreach (var header in new[] { "Ecosystem", "Name", "Version", "SPDX", "License", "Source", "Status", "Reason" })
        {
            Assert.Contains(header, output, StringComparison.Ordinal);
        }

        // Per-package sections.
        Assert.Contains("## express@4.18.2 (npm)", output, StringComparison.Ordinal);
        Assert.Contains("## mystery-pkg@1.0.0 (npm)", output, StringComparison.Ordinal);
        Assert.Contains("MIT", output, StringComparison.Ordinal);
        Assert.Contains("https://example.com/express/LICENSE", output, StringComparison.Ordinal);
        Assert.Contains("not-found: no license for 'mystery-pkg 1.0.0'.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_FallbackToUnknown_When_SpdxIsNull()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("md");

        var output = formatter.FormatResult(FormatterTestHelpers.SampleScanResult());

        // mystery-pkg has null SpdxId — table row and section must show Unknown.
        Assert.Contains("Unknown", output, StringComparison.Ordinal);
        var section = output.Substring(output.IndexOf("## mystery-pkg@", StringComparison.Ordinal));
        Assert.Contains("SPDX: Unknown", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ProduceHeaderOnly_When_ScanResultEmpty()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("md");

        var output = formatter.FormatResult(ScanResult.Empty);

        Assert.Contains("# Third-Party Attribution", output, StringComparison.Ordinal);
        Assert.Contains("Total: 0", output, StringComparison.Ordinal);
        Assert.Contains("Resolved: 0", output, StringComparison.Ordinal);
        Assert.Contains("Unknown: 0", output, StringComparison.Ordinal);
        Assert.DoesNotContain("express", output, StringComparison.Ordinal);
        Assert.DoesNotContain("##", output, StringComparison.Ordinal);
        Assert.DoesNotContain("| npm |", output, StringComparison.Ordinal);
    }
}
