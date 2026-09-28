using System.Text.Json;
using System.Xml;
using Olaf.Core;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #66/#123: `direct` field surfaced by the kept human formats (offline unit
/// style — ScanResult constructed directly, no network). Mixed fixture: one
/// direct (IsTransitive:false) + one transitive (IsTransitive:true) entry so
/// both boolean surfaces are pinned. Every test also calls the shared
/// AssertDirectFieldPresent helper.
/// </summary>
public sealed class DirectFieldFormatterTests
{
    private static ScanResult MixedDirectTransitiveScanResult() => new(new List<ResolvedLicense>
    {
        new(
            new Dependency("npm", "express", "4.18.2", false),
            "MIT",
            "MIT License\nPermission is hereby granted, free of charge,",
            "https://example.com/express/LICENSE",
            "Resolved",
            null),
        new(
            new Dependency("npm", "shadow-dep", "1.0.0", true),
            null,
            null,
            null,
            "Unknown",
            "not-found: no license for 'shadow-dep 1.0.0'."),
    });

    [Fact]
    public void Should_SurfaceDirectBoolLast_When_FormatJson()
    {
        var output = FormatterTestHelpers.ResolveFormatter("json").FormatResult(MixedDirectTransitiveScanResult());

        using var doc = JsonDocument.Parse(output);
        var licenses = doc.RootElement.GetProperty("licenses");
        Assert.True(FormatterTestHelpers.FindByName(licenses.EnumerateArray(), e => e.GetProperty("name").GetString(), "JSON licenses[]", "express").GetProperty("direct").GetBoolean());
        Assert.False(FormatterTestHelpers.FindByName(licenses.EnumerateArray(), e => e.GetProperty("name").GetString(), "JSON licenses[]", "shadow-dep").GetProperty("direct").GetBoolean());
        // Key order: direct appended LAST per entry.
        foreach (var entry in licenses.EnumerateArray())
        {
            Assert.Equal("direct", entry.EnumerateObject().Last().Name);
        }

        FormatterTestHelpers.AssertDirectFieldPresent(output, "json");
    }

    [Fact]
    public void Should_SurfaceDirectMapping_When_FormatYaml()
    {
        var output = FormatterTestHelpers.ResolveFormatter("yaml").FormatResult(MixedDirectTransitiveScanResult());

        Assert.Contains("direct:", output, StringComparison.Ordinal);
        // Per-entry values: express direct true, shadow-dep direct false.
        var expressIndex = output.IndexOf("express", StringComparison.Ordinal);
        var shadowIndex = output.IndexOf("shadow-dep", StringComparison.Ordinal);
        Assert.True(expressIndex >= 0, "YAML output missing 'express'.");
        Assert.True(shadowIndex >= 0, "YAML output missing 'shadow-dep'.");
        var expressBlock = output.Substring(expressIndex, Math.Max(0, shadowIndex - expressIndex));
        Assert.Contains("true", expressBlock, StringComparison.Ordinal);
        var shadowBlock = output.Substring(shadowIndex);
        Assert.Contains("false", shadowBlock, StringComparison.Ordinal);

        FormatterTestHelpers.AssertDirectFieldPresent(output, "yaml");
    }

    [Fact]
    public void Should_SurfaceDirectElementLast_When_FormatXml()
    {
        var output = FormatterTestHelpers.ResolveFormatter("xml").FormatResult(MixedDirectTransitiveScanResult());

        var doc = new XmlDocument();
        doc.LoadXml(output);
        var nodes = doc.SelectNodes("//license")!;
        Assert.Equal(2, nodes.Count);
        foreach (XmlNode node in nodes)
        {
            var direct = node.SelectSingleNode("direct");
            Assert.NotNull(direct);
            // Element order: direct appended LAST per entry.
            Assert.Equal("direct", node.ChildNodes[^1]!.Name);
        }

        var byName = nodes.Cast<XmlNode>().ToDictionary(n => n.SelectSingleNode("name")!.InnerText);
        Assert.Equal("true", byName["express"].SelectSingleNode("direct")!.InnerText);
        Assert.Equal("false", byName["shadow-dep"].SelectSingleNode("direct")!.InnerText);

        FormatterTestHelpers.AssertDirectFieldPresent(output, "xml");
    }

    [Fact]
    public void Should_SurfaceDirectColumnAndBullets_When_FormatMarkdown()
    {
        var output = FormatterTestHelpers.ResolveFormatter("md").FormatResult(MixedDirectTransitiveScanResult());

        Assert.Contains("| Ecosystem | Name | Version | SPDX | License | Source | Status | Reason | Direct |", output, StringComparison.Ordinal);
        Assert.Contains("| --- | --- | --- | --- | --- | --- | --- | --- | --- |", output, StringComparison.Ordinal);
        // 2 data rows x 10 pipes each; per-package sections carry - Direct:.
        var pipeLines = output.Split('\n').Where(line => line.TrimStart().StartsWith("| ", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, pipeLines.Count); // header + separator + 2 rows
        Assert.Equal(10, FormatterTestHelpers.CountOccurrences(pipeLines[2], "|"));
        Assert.Equal(10, FormatterTestHelpers.CountOccurrences(pipeLines[3], "|"));
        Assert.Contains("| true |", output, StringComparison.Ordinal);
        Assert.Contains("| false |", output, StringComparison.Ordinal);
        var expressSection = output.Substring(output.IndexOf("## express@", StringComparison.Ordinal));
        Assert.Contains("- Direct: true", expressSection, StringComparison.Ordinal);
        var shadowSection = output.Substring(output.IndexOf("## shadow-dep@", StringComparison.Ordinal));
        Assert.Contains("- Direct: false", shadowSection, StringComparison.Ordinal);

        FormatterTestHelpers.AssertDirectFieldPresent(output, "md");
    }
}
