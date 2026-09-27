using System.Text.Json;
using Olaf.Core;
using Olaf.Formatters;
using Olaf.Tests.Cli;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #67: CycloneDX JSON export (`cyclonedx-json`, alias `cyclonedx`).
/// Offline unit style — ScanResult constructed directly, no network
/// (DirectFieldFormatterTests pattern). Envelope: bomFormat/specVersion 1.5/
/// version 1/serialNumber regex + uniqueness/timestamp regex (NEVER
/// golden-match); metadata tools/component/properties counts == ScanResult;
/// components sorted (ecosystem,name,version); scope required/optional.
/// Licenses: single-token→id, multi-word→name, Unknown→[] + olaf:* props.
/// Purls: npm scoped %40, pip→pypi, go→golang, maven group split + bare
/// fallback, gradle→maven, unmapped→pkg:generic (never throw).
/// CLI notes: no hardcoded component totals asserted (counts depend on live
/// resolver output for the npm fixture); only exit codes + envelope keys,
/// so no probe-derived count citation is required (Wave 5b rule).
/// </summary>
public sealed class CycloneDxFormatterTests
{
    private static ScanResult MixedCycloneDxScanResult() => new(new List<ResolvedLicense>
    {
        // Deliberately unsorted: pip first, then transitive npm, then direct npm.
        new(
            new Dependency("pip", "requests", "2.31.0", false),
            "MIT",
            "MIT License",
            "https://example.com/requests/LICENSE",
            "Resolved",
            null),
        new(
            new Dependency("npm", "shadow-dep", "1.0.0", true),
            null,
            null,
            null,
            "Unknown",
            "not-found: no license for 'shadow-dep 1.0.0'."),
        new(
            new Dependency("npm", "express", "4.18.2", false),
            "MIT",
            "MIT License\nPermission is hereby granted, free of charge,",
            "https://example.com/express/LICENSE",
            "Resolved",
            null),
    });

    private static JsonElement RootComponents(string output)
    {
        using var doc = JsonDocument.Parse(output);
        // Clone before dispose so callers can enumerate outside the using.
        return doc.RootElement.Clone();
    }

    private static List<JsonElement> ComponentList(JsonElement root)
    {
        return root.GetProperty("components").EnumerateArray().ToList();
    }

    private static string? PropertyValue(JsonElement component, string propertyName)
    {
        foreach (var property in component.GetProperty("properties").EnumerateArray())
        {
            if (property.GetProperty("name").GetString() == propertyName)
            {
                return property.GetProperty("value").GetString();
            }
        }

        return null;
    }

    [Fact]
    public void Should_PinEnvelopeKeys_When_MixedScanResult()
    {
        var scan = MixedCycloneDxScanResult();
        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(scan);

        var root = RootComponents(output);
        Assert.Equal("CycloneDX", root.GetProperty("bomFormat").GetString());
        Assert.Equal("1.5", root.GetProperty("specVersion").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Matches(@"^urn:uuid:[0-9a-fA-F-]{36}$", root.GetProperty("serialNumber").GetString() ?? string.Empty);

        // Metadata tools/component/properties counts == ScanResult.
        var metadata = root.GetProperty("metadata");
        FormatterTestHelpers.AssertIso8601(metadata.GetProperty("timestamp").GetString() ?? string.Empty);
        var tool = metadata.GetProperty("tools").EnumerateArray().Single();
        Assert.Equal("olaf", tool.GetProperty("vendor").GetString());
        Assert.Equal("olaf", tool.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("version").GetString()));
        var appComponent = metadata.GetProperty("component");
        Assert.Equal("application", appComponent.GetProperty("type").GetString());
        Assert.Equal("olaf-scan", appComponent.GetProperty("name").GetString());
        var metadataProps = metadata.GetProperty("properties").EnumerateArray().ToList();
        Assert.Equal(scan.TotalCount.ToString(), PropertyValue(metadata, "olaf:total"));
        Assert.Equal(scan.ResolvedCount.ToString(), PropertyValue(metadata, "olaf:resolved"));
        Assert.Equal(scan.UnknownCount.ToString(), PropertyValue(metadata, "olaf:unknown"));
        Assert.Equal(3, metadataProps.Count);

        // Components sorted (ecosystem,name,version) + scope mapping.
        var components = ComponentList(root);
        Assert.Equal(3, components.Count);
        Assert.Equal(
            new[] { "express", "shadow-dep", "requests" },
            components.Select(c => c.GetProperty("name").GetString()).ToArray());
        Assert.Equal(
            new[] { "npm", "npm", "pip" },
            components.Select(c => c.GetProperty("bom-ref").GetString()!.Split(':')[0]).ToArray());
        foreach (var component in components)
        {
            foreach (var key in new[] { "type", "name", "version", "purl", "bom-ref", "scope", "licenses", "properties" })
            {
                Assert.True(component.TryGetProperty(key, out _), $"components[] entry missing key '{key}'.");
            }

            Assert.Equal("library", component.GetProperty("type").GetString());
        }

        Assert.Equal("required", FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "express").GetProperty("scope").GetString());
        Assert.Equal("optional", FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "shadow-dep").GetProperty("scope").GetString());
        Assert.Equal("required", FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "requests").GetProperty("scope").GetString());
    }

    [Fact]
    public void Should_ReturnEmptyComponents_When_ScanResultEmpty()
    {
        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(ScanResult.Empty);

        var root = RootComponents(output);
        Assert.Equal("CycloneDX", root.GetProperty("bomFormat").GetString());
        Assert.Equal("1.5", root.GetProperty("specVersion").GetString());
        var components = ComponentList(root);
        Assert.Empty(components);
        var metadata = root.GetProperty("metadata");
        Assert.Equal("0", PropertyValue(metadata, "olaf:total"));
        Assert.Equal("0", PropertyValue(metadata, "olaf:resolved"));
        Assert.Equal("0", PropertyValue(metadata, "olaf:unknown"));
    }

    [Fact]
    public void Should_EmitLicenseId_When_SingleTokenSpdx()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "express", "4.18.2", false),
                "MIT",
                "MIT License",
                "https://example.com/express/LICENSE",
                "Resolved",
                null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(scan);

        var component = ComponentList(RootComponents(output)).Single();
        var license = component.GetProperty("licenses").EnumerateArray().Single().GetProperty("license");
        Assert.Equal("MIT", license.GetProperty("id").GetString());
        Assert.False(license.TryGetProperty("name", out _));
    }

    [Fact]
    public void Should_EmitLicenseName_When_MultiWordSpdx()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "custom-pkg", "1.0.0", false),
                "My Custom License",
                "Full custom text",
                null,
                "Resolved",
                null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(scan);

        var component = ComponentList(RootComponents(output)).Single();
        var license = component.GetProperty("licenses").EnumerateArray().Single().GetProperty("license");
        Assert.Equal("My Custom License", license.GetProperty("name").GetString());
        Assert.False(license.TryGetProperty("id", out _));
    }

    [Fact]
    public void Should_EmitStatusProperties_When_LicenseUnknown()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(
                new Dependency("npm", "mystery-pkg", "1.0.0", false),
                null,
                null,
                "https://example.com/mystery",
                "Unknown",
                "not-found: no license for 'mystery-pkg 1.0.0'."),
            new(
                new Dependency("npm", "no-source-pkg", "2.0.0", true),
                null,
                null,
                null,
                "Unknown",
                "not-found."),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(scan);

        var components = ComponentList(RootComponents(output));
        var mystery = FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "mystery-pkg");
        Assert.Equal(0, mystery.GetProperty("licenses").GetArrayLength());
        Assert.Equal("Unknown", PropertyValue(mystery, "olaf:status"));
        Assert.Equal("not-found: no license for 'mystery-pkg 1.0.0'.", PropertyValue(mystery, "olaf:reason"));
        Assert.Equal("https://example.com/mystery", PropertyValue(mystery, "olaf:sourceUrl"));

        // sourceUrl property present only when non-empty.
        var noSource = FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "no-source-pkg");
        Assert.Equal(0, noSource.GetProperty("licenses").GetArrayLength());
        Assert.Equal("Unknown", PropertyValue(noSource, "olaf:status"));
        Assert.Null(PropertyValue(noSource, "olaf:sourceUrl"));
    }

    [Fact]
    public void Should_UseFreshSerialAndTimestamp_When_FormatCalledTwice()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("cyclonedx-json");
        var scan = MixedCycloneDxScanResult();

        var first = RootComponents(formatter.FormatResult(scan));
        var second = RootComponents(formatter.FormatResult(scan));

        var firstSerial = first.GetProperty("serialNumber").GetString() ?? string.Empty;
        var secondSerial = second.GetProperty("serialNumber").GetString() ?? string.Empty;
        Assert.Matches(@"^urn:uuid:[0-9a-fA-F-]{36}$", firstSerial);
        Assert.Matches(@"^urn:uuid:[0-9a-fA-F-]{36}$", secondSerial);
        Assert.NotEqual(firstSerial, secondSerial);

        // Timestamps parseable (regex-equivalent: ISO-8601 round-trip shape), never golden-matched.
        FormatterTestHelpers.AssertIso8601(first.GetProperty("metadata").GetProperty("timestamp").GetString() ?? string.Empty);
        FormatterTestHelpers.AssertIso8601(second.GetProperty("metadata").GetProperty("timestamp").GetString() ?? string.Empty);
    }

    [Fact]
    public void Should_SuffixBomRef_When_DuplicateDependencies()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("npm", "dup", "1.0.0", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("npm", "dup", "1.0.0", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("npm", "dup", "1.0.0", false), "MIT", null, null, "Resolved", null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(scan);

        var refs = ComponentList(RootComponents(output))
            .Select(c => c.GetProperty("bom-ref").GetString())
            .ToList();
        Assert.Equal(3, refs.Distinct().Count());
        Assert.Equal(
            new[] { "npm:dup@1.0.0", "npm:dup@1.0.0-2", "npm:dup@1.0.0-3" },
            refs.OrderBy(r => r, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Should_MapPipAndGoPurls_When_BuildComponent()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("pip", "requests", "2.31.0", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("go", "github.com/foo/bar", "1.2.3", false), "MIT", null, null, "Resolved", null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(scan);

        var components = ComponentList(RootComponents(output));
        Assert.Equal("pkg:pypi/requests@2.31.0", FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "requests").GetProperty("purl").GetString());
        Assert.Equal(
            "pkg:golang/github.com/foo/bar@1.2.3",
            FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "github.com/foo/bar").GetProperty("purl").GetString());
    }

    [Fact]
    public void Should_EncodeScopedNpmPurl_When_ScopedName()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("npm", "@scope/name", "1.0.0", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("npm", "express", "4.18.2", false), "MIT", null, null, "Resolved", null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(scan);

        var components = ComponentList(RootComponents(output));
        Assert.Equal(
            "pkg:npm/%40scope/name@1.0.0",
            FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "@scope/name").GetProperty("purl").GetString());
        Assert.Equal("pkg:npm/express@4.18.2", FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "express").GetProperty("purl").GetString());
    }

    [Fact]
    public void Should_SplitMavenCoordinates_When_GroupColonArtifact()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("maven", "org.example:artifact", "2.0.0", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("gradle", "org.example:plugin", "3.0.0", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("maven", "bare-artifact", "1.0.0", false), "MIT", null, null, "Resolved", null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(scan);

        var components = ComponentList(RootComponents(output));
        var maven = FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "org.example:artifact");
        Assert.Equal("pkg:maven/org.example/artifact@2.0.0", maven.GetProperty("purl").GetString());
        Assert.Equal("org.example", maven.GetProperty("group").GetString());

        // Gradle reuses the maven purl shape for JVM group:artifact coordinates.
        var gradle = FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "org.example:plugin");
        Assert.Equal("pkg:maven/org.example/plugin@3.0.0", gradle.GetProperty("purl").GetString());
        Assert.Equal("org.example", gradle.GetProperty("group").GetString());

        // Missing colon falls back to the bare name (never throws).
        var bare = FormatterTestHelpers.FindByName(components, c => c.GetProperty("name").GetString(), "CycloneDX components[]", "bare-artifact");
        Assert.Equal("pkg:maven/bare-artifact@1.0.0", bare.GetProperty("purl").GetString());
    }

    [Fact]
    public void Should_FallbackToGenericPurl_When_EcosystemUnmapped()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("conan", "fmt", "10.2.1", false), "MIT", null, null, "Resolved", null),
        });

        // Must never throw for unmapped ecosystems.
        var output = FormatterTestHelpers.ResolveFormatter("cyclonedx-json").FormatResult(scan);

        var component = ComponentList(RootComponents(output)).Single();
        Assert.Equal("pkg:generic/fmt@10.2.1", component.GetProperty("purl").GetString());
        Assert.False(component.TryGetProperty("group", out _));
    }

    [Fact]
    public void Should_ResolveViaRegistry_When_FormatCycloneDxJson()
    {
        var formatter = FormatterTestHelpers.GetFormatterViaRegistry("cyclonedx-json");

        Assert.NotNull(formatter);
        Assert.Equal("cyclonedx-json", formatter.Format, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Should_ResolveSameType_When_AliasCycloneDx()
    {
        var canonical = FormatterTestHelpers.GetFormatterViaRegistry("cyclonedx-json");
        var alias = FormatterTestHelpers.GetFormatterViaRegistry("cyclonedx");

        Assert.NotNull(alias);
        Assert.Equal(canonical.GetType(), alias.GetType());
        Assert.Equal("cyclonedx-json", alias.Format, StringComparer.OrdinalIgnoreCase);

        // toml still throws; the message names the new format.
        var thrown = Assert.Throws<ArgumentException>(() => new FormatterRegistry().GetFormatter("toml"));
        Assert.Contains("cyclonedx-json", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ListCycloneDxJson_When_HelpFlag()
    {
        var result = CliTestHelpers.RunCli("--help");

        Assert.Equal(0, result.ExitCode);
        var combined = result.Stdout + result.Stderr;
        Assert.Contains("cyclonedx-json", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ExitZeroWithBom_When_FormatCycloneDxJson()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "cyclonedx-json");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        var root = RootComponents(result.Stdout);
        Assert.Equal("CycloneDX", root.GetProperty("bomFormat").GetString());
        Assert.Equal("1.5", root.GetProperty("specVersion").GetString());
        Assert.NotEmpty(ComponentList(root));

        // Bad-format exit-2 contract preserved.
        var badFormat = CliTestHelpers.RunCli("--input", input, "--format", "toml");
        Assert.Equal(2, badFormat.ExitCode);
    }
}
