using Olaf.Core;
using Olaf.Formatters;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #73 Step 1: model 2 + render 6 (offline only — inline
/// strings + committed fixtures under Fixtures/templates, no HTTP).
/// </summary>
public sealed class TemplateEngineTests
{
    private static string FixtureText(string name)
    {
        var dir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(dir);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "tests", "Olaf.Tests", "Fixtures", "templates", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = current.Parent;
        }

        throw new NotImplementedException($"Template fixture '{name}' not found.");
    }

    private static ScanResult SampleResult()
    {
        return new ScanResult(
        [
            new ResolvedLicense(
                new Dependency("npm", "left-pad", "1.3.0", false),
                "MIT",
                "text",
                "https://example.com/left-pad",
                "Resolved",
                null,
                new Enrichment(
                    "pkg:npm/left-pad@1.3.0",
                    ["sha512:abc", "sha1:def"],
                    "Example Supplier",
                    "https://example.com/left-pad.tgz",
                    ["Left Pad Authors"])),
            new ResolvedLicense(
                new Dependency("pip", "mystery", "0.0.1", true),
                null,
                null,
                null,
                "Unknown",
                "no-mapping",
                null),
        ]);
    }

    [Fact]
    public void Should_ExposeEveryField_When_BuildingModel()
    {
        var model = TemplateModelBuilder.Build(SampleResult());

        Assert.Equal(2, model.Total);
        Assert.Equal(1, model.Resolved);
        Assert.Equal(1, model.Unknown);
        Assert.True(DateTime.TryParse(model.GeneratedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out _));
        Assert.Equal("0.1.0-preview.1", model.ToolVersion);

        // Sorted ByEcosystemNameVersion: npm/left-pad before pip/mystery.
        Assert.Equal(2, model.Licenses.Count);
        var first = model.Licenses[0];
        Assert.Equal("left-pad", first.Name);
        Assert.Equal("1.3.0", first.Version);
        Assert.Equal("npm", first.Ecosystem);
        Assert.True(first.Direct);
        Assert.Equal("MIT", first.Spdx);
        Assert.Equal("Resolved", first.Status);
        Assert.Equal(string.Empty, first.Reason);
        Assert.Equal("https://example.com/left-pad", first.SourceUrl);
        Assert.Equal("pkg:npm/left-pad@1.3.0", first.Purl);
        Assert.Equal("Example Supplier", first.Supplier);
        Assert.Equal("https://example.com/left-pad.tgz", first.DownloadUrl);
        Assert.Equal("sha512:abc;sha1:def", first.Hashes);
        Assert.Equal("Left Pad Authors", first.Copyright);

        var second = model.Licenses[1];
        Assert.Equal("mystery", second.Name);
        Assert.False(second.Direct);
        Assert.Equal("Unknown", second.Spdx);
        Assert.Equal("Unknown", second.Status);
        Assert.Equal("no-mapping", second.Reason);
        Assert.Equal(string.Empty, second.SourceUrl);
        Assert.Equal(string.Empty, second.Purl);
        Assert.Equal(string.Empty, second.Supplier);
        Assert.Equal(string.Empty, second.DownloadUrl);
        Assert.Equal(string.Empty, second.Hashes);
        Assert.Equal(string.Empty, second.Copyright);
    }

    [Fact]
    public void Should_GroupCountSort_When_BuildingGroups()
    {
        var result = new ScanResult(
        [
            new ResolvedLicense(new Dependency("pip", "b", "2.0", false), "MIT", null, null, "Resolved", null),
            new ResolvedLicense(new Dependency("npm", "a", "1.0", false), "Apache-2.0", null, null, "Resolved", null),
            new ResolvedLicense(new Dependency("npm", "c", "1.0", false), "MIT", null, null, "Resolved", null),
            new ResolvedLicense(new Dependency("go", "d", "3.0", false), null, null, null, "Unknown", null),
        ]);

        var model = TemplateModelBuilder.Build(result);

        // Groups sorted by spdx Ordinal: Apache-2.0 < MIT < Unknown.
        Assert.Equal(3, model.Groups.Count);
        Assert.Equal("Apache-2.0", model.Groups[0].Spdx);
        Assert.Equal(1, model.Groups[0].Count);
        Assert.Equal("MIT", model.Groups[1].Spdx);
        Assert.Equal(2, model.Groups[1].Count);
        Assert.Equal("Unknown", model.Groups[2].Spdx);
        Assert.Equal(1, model.Groups[2].Count);

        // Items sorted ByEcosystemNameVersion within the MIT group (npm/c before pip/b).
        Assert.Equal(["c", "b"], model.Groups[1].Items.Select(i => i.Name));

        // Groups fixture renders group headers + nested items.
        var output = TemplateEngine.Render(FixtureText("groups.scriban"), model);
        Assert.Contains("## MIT (2)", output, StringComparison.Ordinal);
        Assert.Contains("## Apache-2.0 (1)", output, StringComparison.Ordinal);
        Assert.Contains("- a@1.0 (npm)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_RenderEmpty_When_FieldIsUnknown()
    {
        var model = TemplateModelBuilder.Build(ScanResult.Empty);

        Assert.Equal(string.Empty, TemplateEngine.Render("{{nosuch}}", model));
        Assert.Equal(string.Empty, TemplateEngine.Render("{{license.nope}}", model));
        Assert.Equal("a--b", TemplateEngine.Render("a-{{missing.deep.path}}-b", model));
    }

    [Fact]
    public void Should_ReportLineNumber_When_SyntaxIsInvalid()
    {
        var model = TemplateModelBuilder.Build(ScanResult.Empty);

        var ex = Assert.Throws<TemplateSyntaxException>(
            () => TemplateEngine.Render("line1\nline2\n{{#each licenses}}\nno-close", model));
        Assert.Equal(3, ex.Line);

        var mismatched = Assert.Throws<TemplateSyntaxException>(
            () => TemplateEngine.Render("{{#if total}}x{{/each}}", model));
        Assert.Equal(1, mismatched.Line);
    }

    [Fact]
    public void Should_RenderHeaderFooterLoop_When_UsingCommittedFixture()
    {
        var model = TemplateModelBuilder.Build(SampleResult());

        var output = TemplateEngine.Render(FixtureText("header-footer-loop.scriban"), model);

        Assert.Contains("Third-Party Attribution", output, StringComparison.Ordinal);
        Assert.Contains("Total: 2, Resolved: 1, Unknown: 1", output, StringComparison.Ordinal);
        Assert.Contains("left-pad@1.3.0 (npm) SPDX: MIT", output, StringComparison.Ordinal);
        Assert.Contains("mystery@0.0.1 (pip) SPDX: Unknown", output, StringComparison.Ordinal);
        Assert.Contains("Generated by olaf 0.1.0-preview.1", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_OmitWhenEmpty_When_IfFieldIsEmpty()
    {
        var model = TemplateModelBuilder.Build(SampleResult());

        var output = TemplateEngine.Render(
            "{{#each licenses}}{{#if copyright}}C:{{copyright}};{{/if}}{{/each}}",
            model);

        Assert.Contains("C:Left Pad Authors;", output, StringComparison.Ordinal);
        Assert.DoesNotContain("C:;", output, StringComparison.Ordinal);
        Assert.Equal("shown", TemplateEngine.Render("{{#if total}}shown{{/if}}", model));
        Assert.Equal(string.Empty, TemplateEngine.Render("{{#if missing}}shown{{/if}}", model));
    }

    [Fact]
    public void Should_PassThrough_When_SpecialCharsPresent()
    {
        var result = new ScanResult(
        [
            new ResolvedLicense(
                new Dependency("npm", "héllo<wörld>&\"q\"", "1.0+tëst", false),
                "MIT",
                null,
                null,
                "Resolved",
                null,
                new Enrichment(null, null, null, null, ["Müller & Söhne <info@example.com>"])),
        ]);
        var model = TemplateModelBuilder.Build(result);

        var output = TemplateEngine.Render("{{#each licenses}}{{name}}|{{version}}|{{copyright}}|100% ✓{{/each}}", model);

        Assert.Contains("héllo<wörld>&\"q\"|1.0+tëst|Müller & Söhne <info@example.com>|100% ✓", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_EnforceCaps_When_LimitsExceeded()
    {
        var many = Enumerable.Range(0, 11_000)
            .Select(i => new ResolvedLicense(
                new Dependency("npm", $"pkg-{i}", "1.0", false), "MIT", null, null, "Resolved", null))
            .ToList();
        var big = TemplateModelBuilder.Build(new ScanResult(many));

        Assert.Throws<IOException>(
            () => TemplateEngine.Render("{{#each licenses}}x{{/each}}", big));
        Assert.Throws<IOException>(
            () => TemplateEngine.Render(new string('y', 2 * 1024 * 1024), TemplateModelBuilder.Build(ScanResult.Empty)));

        // 10_000 iterations (at the cap) with a wide body exceeds 1 MiB output.
        var wide = Enumerable.Range(0, 10_000)
            .Select(i => new ResolvedLicense(
                new Dependency("npm", new string('n', 200) + i, "1.0", false), "MIT", null, null, "Resolved", null))
            .ToList();
        var wideModel = TemplateModelBuilder.Build(new ScanResult(wide));
        Assert.Throws<IOException>(
            () => TemplateEngine.Render("{{#each licenses}}{{name}}{{/each}}", wideModel));
    }
}
