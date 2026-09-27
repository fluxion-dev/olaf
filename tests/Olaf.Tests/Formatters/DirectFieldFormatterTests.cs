using System.Text.Json;
using System.Xml;
using Olaf.Core;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #66: `direct` field surfaced by all six formatters (offline unit
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

    private static JsonElement FindByName(JsonElement licenses, string name)
    {
        foreach (var entry in licenses.EnumerateArray())
        {
            if (entry.GetProperty("name").GetString() == name)
            {
                return entry;
            }
        }

        throw new Xunit.Sdk.XunitException($"JSON licenses[] missing entry '{name}'.");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public void Should_SurfaceDirectBoolLast_When_FormatJson()
    {
        var output = FormatterTestHelpers.ResolveFormatter("json").FormatResult(MixedDirectTransitiveScanResult());

        using var doc = JsonDocument.Parse(output);
        var licenses = doc.RootElement.GetProperty("licenses");
        Assert.True(FindByName(licenses, "express").GetProperty("direct").GetBoolean());
        Assert.False(FindByName(licenses, "shadow-dep").GetProperty("direct").GetBoolean());
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
    public void Should_SurfaceDirectColumn_When_FormatHtml()
    {
        var output = FormatterTestHelpers.ResolveFormatter("html").FormatResult(MixedDirectTransitiveScanResult());

        Assert.Contains("<th>Direct</th>", output, StringComparison.Ordinal);
        // Header Direct is the LAST column.
        var reasonTh = output.IndexOf("<th>Reason</th>", StringComparison.Ordinal);
        var directTh = output.IndexOf("<th>Direct</th>", StringComparison.Ordinal);
        Assert.True(reasonTh >= 0 && directTh > reasonTh, "Expected '<th>Direct</th>' after '<th>Reason</th>'.");
        // 2 body rows x 9 columns = 18 td cells; per-row values pinned.
        var tbody = output.Substring(output.IndexOf("<tbody>", StringComparison.Ordinal));
        Assert.Equal(18, CountOccurrences(tbody, "<td>"));
        var expressAt = tbody.IndexOf("express", StringComparison.Ordinal);
        var expressRow = tbody.Substring(expressAt, Math.Min(600, tbody.Length - expressAt));
        Assert.Contains("<td>true</td>", expressRow, StringComparison.Ordinal);
        var shadowAt = tbody.IndexOf("shadow-dep", StringComparison.Ordinal);
        var shadowRow = tbody.Substring(shadowAt, Math.Min(600, tbody.Length - shadowAt));
        Assert.Contains("<td>false</td>", shadowRow, StringComparison.Ordinal);

        FormatterTestHelpers.AssertDirectFieldPresent(output, "html");
    }

    [Fact]
    public void Should_SurfaceDirectTrailingToken_When_FormatTxt()
    {
        var output = FormatterTestHelpers.ResolveFormatter("txt").FormatResult(MixedDirectTransitiveScanResult());

        // Additive trailing token; existing header shape untouched.
        Assert.Contains("express@4.18.2 (npm) direct=true", output, StringComparison.Ordinal);
        Assert.Contains("shadow-dep@1.0.0 (npm) direct=false", output, StringComparison.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Contains('@') && trimmed.Contains("(npm)"))
            {
                Assert.True(
                    trimmed.EndsWith("direct=true", StringComparison.Ordinal) || trimmed.EndsWith("direct=false", StringComparison.Ordinal),
                    $"Expected trailing direct token, got '{trimmed}'.");
            }
        }

        FormatterTestHelpers.AssertDirectFieldPresent(output, "txt");
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
        Assert.Equal(10, CountOccurrences(pipeLines[2], "|"));
        Assert.Equal(10, CountOccurrences(pipeLines[3], "|"));
        Assert.Contains("| true |", output, StringComparison.Ordinal);
        Assert.Contains("| false |", output, StringComparison.Ordinal);
        var expressSection = output.Substring(output.IndexOf("## express@", StringComparison.Ordinal));
        Assert.Contains("- Direct: true", expressSection, StringComparison.Ordinal);
        var shadowSection = output.Substring(output.IndexOf("## shadow-dep@", StringComparison.Ordinal));
        Assert.Contains("- Direct: false", shadowSection, StringComparison.Ordinal);

        FormatterTestHelpers.AssertDirectFieldPresent(output, "md");
    }
}
