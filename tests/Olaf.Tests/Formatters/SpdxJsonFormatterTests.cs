using System.Text.Json;
using Olaf.Core;
using Olaf.Formatters;
using Olaf.Tests.Cli;

namespace Olaf.Tests.Formatters;

/// <summary>
/// Issue #69: SPDX JSON export (`spdx-json`, SPDX-2.3). Offline unit style —
/// ScanResult constructed directly, no network (DirectFieldFormatterTests
/// pattern). Bindings: positional SPDXIDs + DOCUMENT pinned (B1); Concluded
/// passes single-token ids/expressions else NOASSERTION, Declared mirrors
/// Concluded (B2); copyrightText/supplier/downloadLocation are literal
/// NOASSERTION (B3/B4, SourceUrl never copied); DESCRIBES+CONTAINS per
/// package with referential closure, self-DESCRIBES when empty (B5); counts
/// in `comment` (B6); envelope pins + UUID namespace + ISO-8601 created +
/// unconditional purls (B7/B8); `spdx-json` registration only (B9).
/// Regex-never-golden (created/namespace); no hardcoded resolver totals —
/// CLI asserts envelope keys + a filtered-subset relation only. The shared
/// spdx-b4 fixture is for probes, NOT unit tests (no live totals asserted).
/// </summary>
public sealed class SpdxJsonFormatterTests
{
    private const string SpdxIdPattern = @"^SPDXRef-[A-Za-z0-9.\-]+$";

    private static readonly string[] ExpectedChildOrder =
    {
        "SPDXID", "name", "versionInfo", "supplier", "downloadLocation",
        "filesAnalyzed", "licenseConcluded", "licenseDeclared",
        "copyrightText", "externalRefs",
    };

    private static JsonElement ParseRoot(string output)
    {
        using var doc = JsonDocument.Parse(output);
        // Clone before dispose so callers can enumerate outside the using.
        return doc.RootElement.Clone();
    }

    private static List<JsonElement> PackageList(JsonElement root)
    {
        return root.GetProperty("packages").EnumerateArray().ToList();
    }

    private static List<JsonElement> RelationshipList(JsonElement root)
    {
        return root.GetProperty("relationships").EnumerateArray().ToList();
    }

    private static ScanResult MixedSpdxScanResult() => new(new List<ResolvedLicense>
    {
        // Deliberately unsorted: pip first, then unknown npm, then direct npm.
        new(
            new Dependency("pip", "requests", "2.31.0", false),
            "MIT",
            "MIT License",
            "https://example.com/requests/LICENSE",
            "Resolved",
            null),
        new(
            new Dependency("npm", "mystery-pkg", "1.0.0", false),
            null,
            null,
            "https://example.com/mystery",
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

    [Fact]
    public void Should_MatchSpdxIdRegex_When_PackagesEmitted()
    {
        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(MixedSpdxScanResult());

        var root = ParseRoot(output);
        Assert.Matches(SpdxIdPattern, root.GetProperty("SPDXID").GetString() ?? string.Empty);
        var packages = PackageList(root);
        Assert.Equal(3, packages.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var id = package.GetProperty("SPDXID").GetString() ?? string.Empty;
            Assert.Matches(SpdxIdPattern, id);
            Assert.True(ids.Add(id), $"Duplicate SPDXID '{id}'.");
        }
    }

    [Fact]
    public void Should_OrderPositionalIds_When_SortedByEcosystemNameVersion()
    {
        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(MixedSpdxScanResult());

        var root = ParseRoot(output);
        Assert.Equal("SPDXRef-DOCUMENT", root.GetProperty("SPDXID").GetString());
        var packages = PackageList(root);
        Assert.Equal(
            new[] { "express", "mystery-pkg", "requests" },
            packages.Select(p => p.GetProperty("name").GetString()).ToArray());
        Assert.Equal(
            new[] { "SPDXRef-Package-1", "SPDXRef-Package-2", "SPDXRef-Package-3" },
            packages.Select(p => p.GetProperty("SPDXID").GetString()).ToArray());
    }

    [Fact]
    public void Should_PassThrough_When_ValidIdOrExpression()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("npm", "single-pkg", "1.0.0", false), "MIT", null, null, "Resolved", null),
            // Parens are part of the passthrough: the expression arm splits on them.
            new(new Dependency("npm", "expr-pkg", "2.0.0", false), "(OFL-1.1 AND MIT)", null, null, "Resolved", null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(scan);

        var packages = PackageList(ParseRoot(output));
        Assert.Equal("MIT", FormatterTestHelpers.FindByName(packages, p => p.GetProperty("name").GetString(), "SPDX packages[]", "single-pkg").GetProperty("licenseConcluded").GetString());
        Assert.Equal("(OFL-1.1 AND MIT)", FormatterTestHelpers.FindByName(packages, p => p.GetProperty("name").GetString(), "SPDX packages[]", "expr-pkg").GetProperty("licenseConcluded").GetString());
    }

    [Fact]
    public void Should_EmitNoassertion_When_MultiWordLicense()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("npm", "custom-pkg", "1.0.0", false), "My Custom License", "Full custom text", null, "Resolved", null),
        });

        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(scan);

        var package = PackageList(ParseRoot(output)).Single();
        Assert.Equal("NOASSERTION", package.GetProperty("licenseConcluded").GetString());
        Assert.Equal("NOASSERTION", package.GetProperty("licenseDeclared").GetString());
    }

    [Fact]
    public void Should_EmitNoassertion_When_LicenseUnknownNullOrEmpty()
    {
        var cases = new (string? Spdx, string Label)[]
        {
            (null, "null"), (string.Empty, "empty"), ("   ", "whitespace"),
            ("Unknown", "Unknown"), ("unknown", "lowercase-unknown"),
            ("NONE", "NONE"), ("NOASSERTION", "NOASSERTION"),
        };
        foreach (var (spdx, label) in cases)
        {
            var scan = new ScanResult(new List<ResolvedLicense>
            {
                new(new Dependency("npm", $"pkg-{label}", "1.0.0", false), spdx, null, null, "Unknown", "not-found."),
            });

            var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(scan);

            var package = PackageList(ParseRoot(output)).Single();
            Assert.Equal("NOASSERTION", package.GetProperty("licenseConcluded").GetString());
            Assert.Equal("NOASSERTION", package.GetProperty("licenseDeclared").GetString());
        }

        // Never NONE, never "", never omitted, never an "Unknown" string leak.
        var leaked = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("npm", "leak-check", "1.0.0", false), null, null, null, "Unknown", "not-found."),
        }));
        Assert.DoesNotContain("\"NONE\"", leaked, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown", leaked, StringComparison.Ordinal);
        Assert.DoesNotContain("\"licenseConcluded\":\"\"", leaked, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_MirrorDeclared_When_ConcludedResolved()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("npm", "id-pkg", "1.0.0", false), "Apache-2.0", null, null, "Resolved", null),
            new(new Dependency("npm", "expr-pkg", "1.0.0", false), "(OFL-1.1 AND MIT)", null, null, "Resolved", null),
            new(new Dependency("npm", "custom-pkg", "1.0.0", false), "My Custom License", null, null, "Resolved", null),
            new(new Dependency("npm", "unknown-pkg", "1.0.0", false), null, null, null, "Unknown", "not-found."),
        });

        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(scan);

        var packages = PackageList(ParseRoot(output));
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id-pkg"] = "Apache-2.0",
            ["expr-pkg"] = "(OFL-1.1 AND MIT)",
            ["custom-pkg"] = "NOASSERTION",
            ["unknown-pkg"] = "NOASSERTION",
        };
        foreach (var (name, concluded) in expected)
        {
            var package = FormatterTestHelpers.FindByName(packages, p => p.GetProperty("name").GetString(), "SPDX packages[]", name);
            Assert.Equal(concluded, package.GetProperty("licenseConcluded").GetString());
            Assert.Equal(
                package.GetProperty("licenseConcluded").GetString(),
                package.GetProperty("licenseDeclared").GetString());
        }
    }

    [Fact]
    public void Should_EmitNoassertionCopyright_When_Pre72()
    {
        // Pre-#72: no copyright scraping; literal NOASSERTION on every package.
        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(MixedSpdxScanResult());

        var packages = PackageList(ParseRoot(output));
        Assert.NotEmpty(packages);
        foreach (var package in packages)
        {
            Assert.Equal("NOASSERTION", package.GetProperty("copyrightText").GetString());
        }
    }

    [Fact]
    public void Should_EmitNoassertionSupplierAndDownload_When_SourceUrlPresent()
    {
        // Registry page != download URI: SourceUrl is never copied.
        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(MixedSpdxScanResult());

        var packages = PackageList(ParseRoot(output));
        foreach (var package in packages)
        {
            Assert.Equal("NOASSERTION", package.GetProperty("supplier").GetString());
            Assert.Equal("NOASSERTION", package.GetProperty("downloadLocation").GetString());
        }

        Assert.DoesNotContain("example.com/express/LICENSE", output, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com/mystery", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_CloseRelationships_When_PackagesEmitted()
    {
        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(MixedSpdxScanResult());

        var root = ParseRoot(output);
        var packages = PackageList(root);
        var ids = new HashSet<string>(packages.Select(p => p.GetProperty("SPDXID").GetString()!), StringComparer.Ordinal)
        {
            "SPDXRef-DOCUMENT",
        };
        var relationships = RelationshipList(root);
        Assert.Equal(packages.Count * 2, relationships.Count);
        foreach (var package in packages)
        {
            var id = package.GetProperty("SPDXID").GetString()!;
            var kinds = relationships
                .Where(r => r.GetProperty("relatedSpdxElement").GetString() == id)
                .Select(r => r.GetProperty("relationshipType").GetString())
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "CONTAINS", "DESCRIBES" }, kinds);
        }

        // Referential closure: every endpoint resolves to a known ID.
        foreach (var relationship in relationships)
        {
            Assert.Equal("SPDXRef-DOCUMENT", relationship.GetProperty("spdxElementId").GetString());
            Assert.Contains(relationship.GetProperty("relatedSpdxElement").GetString()!, ids);
        }
    }

    [Fact]
    public void Should_PinEnvelope_When_MixedScanResult()
    {
        var scan = MixedSpdxScanResult();
        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(scan);

        var root = ParseRoot(output);
        Assert.Equal("SPDX-2.3", root.GetProperty("spdxVersion").GetString());
        Assert.Equal("CC0-1.0", root.GetProperty("dataLicense").GetString());
        Assert.Equal("SPDXRef-DOCUMENT", root.GetProperty("SPDXID").GetString());
        Assert.Equal("olaf-scan", root.GetProperty("name").GetString());

        // Namespace shape (uniqueness pinned by M1, not golden-matched here).
        var ns = root.GetProperty("documentNamespace").GetString() ?? string.Empty;
        FormatterTestHelpers.AssertSpdxNamespace(ns);

        // Created parses (exact instant pinned by M2, never golden-matched).
        var created = root.GetProperty("creationInfo").GetProperty("created").GetString() ?? string.Empty;
        FormatterTestHelpers.AssertIso8601(created);
        var creators = root.GetProperty("creationInfo").GetProperty("creators").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Single(creators);
        Assert.StartsWith("Tool: olaf ", creators[0], StringComparison.Ordinal);

        // Counts comment mirrors ScanResult (exact equality pinned by P1).
        Assert.Equal(
            $"olaf:total={scan.TotalCount}/resolved={scan.ResolvedCount}/unknown={scan.UnknownCount}",
            root.GetProperty("comment").GetString());

        var packages = PackageList(root);
        Assert.Equal(3, packages.Count);
        foreach (var package in packages)
        {
            // Per-package child order pinned.
            Assert.Equal(ExpectedChildOrder, package.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.False(package.GetProperty("filesAnalyzed").GetBoolean());

            // Purl unconditional: every package (even Unknown) carries one.
            var refs = package.GetProperty("externalRefs").EnumerateArray().ToList();
            Assert.Single(refs);
            Assert.Equal("PACKAGE-MANAGER", refs[0].GetProperty("referenceCategory").GetString());
            Assert.Equal("purl", refs[0].GetProperty("referenceType").GetString());
            Assert.False(string.IsNullOrWhiteSpace(refs[0].GetProperty("referenceLocator").GetString()));
        }

        Assert.Equal("pkg:npm/express@4.18.2", FormatterTestHelpers.FindByName(packages, p => p.GetProperty("name").GetString(), "SPDX packages[]", "express").GetProperty("externalRefs").EnumerateArray().First().GetProperty("referenceLocator").GetString());
        Assert.Equal("pkg:pypi/requests@2.31.0", FormatterTestHelpers.FindByName(packages, p => p.GetProperty("name").GetString(), "SPDX packages[]", "requests").GetProperty("externalRefs").EnumerateArray().First().GetProperty("referenceLocator").GetString());
    }

    [Fact]
    public void Should_ResolveAndList_When_FormatSpdxJson()
    {
        var formatter = FormatterTestHelpers.GetFormatterViaRegistry("spdx-json");

        Assert.NotNull(formatter);
        Assert.Equal("spdx-json", formatter.Format, StringComparer.OrdinalIgnoreCase);

        // No `spdx` alias: only the full token resolves (implementer bound B9).
        Assert.Throws<ArgumentException>(() => new FormatterRegistry().GetFormatter("spdx"));

        // toml still throws; the message names the new format.
        var thrown = Assert.Throws<ArgumentException>(() => new FormatterRegistry().GetFormatter("toml"));
        Assert.Contains("spdx-json", thrown.Message, StringComparison.Ordinal);

        var help = CliTestHelpers.RunCli("--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("spdx-json", help.Stdout + help.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ExitZeroWithSpdx_When_FormatSpdxJson()
    {
        var input = CliTestHelpers.FixturePath("npm", "package.json");

        var result = CliTestHelpers.RunCli("--input", input, "--format", "spdx-json");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        var root = ParseRoot(result.Stdout);
        Assert.Equal("SPDX-2.3", root.GetProperty("spdxVersion").GetString());
        Assert.Equal("CC0-1.0", root.GetProperty("dataLicense").GetString());
        Assert.Equal("SPDXRef-DOCUMENT", root.GetProperty("SPDXID").GetString());
        Assert.NotEmpty(PackageList(root));
        Assert.NotEmpty(RelationshipList(root));

        // --direct-only flows the filtered set through (subset relation, no
        // hardcoded totals — counts depend on resolver output, not parsing).
        var mixed = CliTestHelpers.CreateMixedNpmFixtureDir();
        try
        {
            var all = CliTestHelpers.RunCli("--input", mixed, "--format", "spdx-json");
            var directOnly = CliTestHelpers.RunCli("--input", mixed, "--format", "spdx-json", "--direct-only");

            Assert.Equal(0, all.ExitCode);
            Assert.Equal(0, directOnly.ExitCode);
            var allCount = PackageList(ParseRoot(all.Stdout)).Count;
            var directCount = PackageList(ParseRoot(directOnly.Stdout)).Count;
            Assert.True(allCount > 0, "Expected packages without the filter.");
            Assert.True(directCount > 0, "Expected packages with --direct-only.");
            Assert.True(directCount <= allCount, $"Expected --direct-only ({directCount}) to be a subset of unfiltered ({allCount}).");
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(mixed);
        }

        // Bad-format exit-2 contract preserved.
        var badFormat = CliTestHelpers.RunCli("--input", input, "--format", "toml");
        Assert.Equal(2, badFormat.ExitCode);
    }

    [Fact]
    public void Should_CarryCountsComment_When_ScanResultCounted()
    {
        var scan = new ScanResult(new List<ResolvedLicense>
        {
            new(new Dependency("npm", "a-pkg", "1.0.0", false), "MIT", null, null, "Resolved", null),
            new(new Dependency("npm", "b-pkg", "2.0.0", false), "(OFL-1.1 AND MIT)", null, null, "Resolved", null),
            new(new Dependency("npm", "c-pkg", "3.0.0", false), null, null, null, "Unknown", "not-found."),
        });
        Assert.Equal(3, scan.TotalCount);
        Assert.Equal(2, scan.ResolvedCount);
        Assert.Equal(1, scan.UnknownCount);

        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(scan);

        Assert.Equal("olaf:total=3/resolved=2/unknown=1", ParseRoot(output).GetProperty("comment").GetString());
    }

    [Fact]
    public void Should_EmitSelfDescribesOnly_When_ScanResultEmpty()
    {
        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(ScanResult.Empty);

        var root = ParseRoot(output);
        Assert.Equal("SPDX-2.3", root.GetProperty("spdxVersion").GetString());
        Assert.Empty(PackageList(root));
        var relationships = RelationshipList(root);
        var single = Assert.Single(relationships);
        Assert.Equal("SPDXRef-DOCUMENT", single.GetProperty("spdxElementId").GetString());
        Assert.Equal("SPDXRef-DOCUMENT", single.GetProperty("relatedSpdxElement").GetString());
        Assert.Equal("DESCRIBES", single.GetProperty("relationshipType").GetString());
        Assert.Equal(new[] { "SPDXRef-DOCUMENT" }, root.GetProperty("documentDescribes").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("olaf:total=0/resolved=0/unknown=0", root.GetProperty("comment").GetString());
    }

    [Fact]
    public void Should_UniquelyAssignNamespace_When_FormatCalledTwice()
    {
        var formatter = FormatterTestHelpers.ResolveFormatter("spdx-json");
        var scan = MixedSpdxScanResult();

        var first = ParseRoot(formatter.FormatResult(scan)).GetProperty("documentNamespace").GetString() ?? string.Empty;
        var second = ParseRoot(formatter.FormatResult(scan)).GetProperty("documentNamespace").GetString() ?? string.Empty;

        FormatterTestHelpers.AssertSpdxNamespace(first);
        FormatterTestHelpers.AssertSpdxNamespace(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Should_EmitParsableTimestamp_When_CreatedRequested()
    {
        var output = FormatterTestHelpers.ResolveFormatter("spdx-json").FormatResult(MixedSpdxScanResult());

        var created = ParseRoot(output).GetProperty("creationInfo").GetProperty("created").GetString() ?? string.Empty;

        FormatterTestHelpers.AssertIso8601(created);
        Assert.True(DateTimeOffset.TryParse(created, out var parsed), "creationInfo.created must be a parseable ISO-8601 timestamp.");
        Assert.True(parsed.Year >= 2026, "creationInfo.created must not be a stale golden value.");
    }
}
