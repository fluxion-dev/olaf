using System.Text.Json;
using Olaf.Core;

namespace Olaf.Formatters;

public sealed class SpdxJsonFormatter : ILicenseFormatter
{
    public string Format => "spdx-json";

    public string FormatResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sorted = FormatterSort.ByEcosystemNameVersion(result.Licenses).ToList();

        // SPDXID is positional (SPDXRef-Package-1..N) in FormatterSort order:
        // bom-ref charset ("eco:name@ver") is illegal for SPDXID, and positional
        // IDs avoid sanitize collisions. DOCUMENT is the pinned literal.
        var packages = new List<Dictionary<string, object?>>(sorted.Count);
        var relationships = new List<Dictionary<string, object?>>(sorted.Count * 2);
        var describes = new List<string>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            var license = sorted[i];
            var dep = license.Dependency;
            var id = $"SPDXRef-Package-{i + 1}";
            var concluded = ToConcludedLicense(LicenseDisplay.EffectiveSpdx(license));
            // Issue #70: supplier/downloadLocation are enrichment-or-NOASSERTION.
            // Registry page != download URI, so SourceUrl is never copied —
            // only the resolver-harvested DownloadUrl flows here.
            var supplier = license.Enrichment?.Supplier is { } s && !string.IsNullOrWhiteSpace(s)
                ? $"Person: {s.Trim()}"
                : "NOASSERTION";
            var download = !string.IsNullOrWhiteSpace(license.Enrichment?.DownloadUrl)
                ? license.Enrichment!.DownloadUrl!
                : "NOASSERTION";
            var package = new Dictionary<string, object?>
            {
                ["SPDXID"] = id,
                ["name"] = dep.Name,
                ["versionInfo"] = dep.Version,
                ["supplier"] = supplier,
                ["downloadLocation"] = download,
                ["filesAnalyzed"] = false,
                ["licenseConcluded"] = concluded,
                // licenseDeclared mirrors licenseConcluded: the resolver raw string
                // no longer exists at the formatter layer (no model change).
                ["licenseDeclared"] = concluded,
                // Issue #72: copyrightText = "; "-joined holders, or
                // NOASSERTION when empty (omit-null has no meaning in SPDX —
                // the field is mandatory, so empty stays the literal).
                ["copyrightText"] = CopyrightHoldersFormat.Join(license.Enrichment?.CopyrightHolders)
                    ?? "NOASSERTION",
                // PURL is unconditional (always emitted).
                ["externalRefs"] = new[]
                {
                    new Dictionary<string, string?>
                    {
                        ["referenceCategory"] = "PACKAGE-MANAGER",
                        ["referenceType"] = "purl",
                        ["referenceLocator"] = CycloneDxPurl.Build(dep),
                    },
                },
            };
            // Issue #70: checksums[] = {algorithm, checksumValue} when Hashes
            // present (split "algo:value" on first colon; unparseable entries
            // dropped), else omitted entirely (unenriched output is stable).
            if (license.Enrichment?.Hashes is { Length: > 0 })
            {
                var checksums = new List<Dictionary<string, string?>>();
                foreach (var entry in license.Enrichment.Hashes)
                {
                    if (CycloneDxComponentMapper.TrySplitHash(entry, out var algo, out var content))
                    {
                        checksums.Add(new Dictionary<string, string?>
                        {
                            ["algorithm"] = CycloneDxComponentMapper.ToSpdxAlg(algo),
                            ["checksumValue"] = content,
                        });
                    }
                }

                if (checksums.Count > 0)
                {
                    package["checksums"] = checksums;
                }
            }

            packages.Add(package);
            describes.Add(id);
            // Flat-list honesty: no tree is inferred, so every package gets
            // BOTH DESCRIBES and CONTAINS from DOCUMENT.
            relationships.Add(Relationship("SPDXRef-DOCUMENT", id, "DESCRIBES"));
            relationships.Add(Relationship("SPDXRef-DOCUMENT", id, "CONTAINS"));
        }

        if (sorted.Count == 0)
        {
            relationships.Add(Relationship("SPDXRef-DOCUMENT", "SPDXRef-DOCUMENT", "DESCRIBES"));
            describes.Add("SPDXRef-DOCUMENT");
        }

        // SPDX 2.3 only: no 3.0 profile/context fields are emitted here.
        // A future SPDX-3.0 formatter is a separate format, not an extension.
        var toolVersion = CycloneDxComponentMapper.ToolVersion;
        var envelope = new Dictionary<string, object?>
        {
            ["spdxVersion"] = "SPDX-2.3",
            ["dataLicense"] = "CC0-1.0",
            ["SPDXID"] = "SPDXRef-DOCUMENT",
            ["name"] = "olaf-scan",
            ["documentNamespace"] = $"https://olaf.example/sbom/{Guid.NewGuid()}",
            ["creationInfo"] = new Dictionary<string, object?>
            {
                ["created"] = DateTime.UtcNow.ToString("o"),
                ["creators"] = new[] { $"Tool: olaf {toolVersion}" },
            },
            ["packages"] = packages,
            ["relationships"] = relationships,
            ["documentDescribes"] = describes,
            ["comment"] = $"olaf:total={result.TotalCount}/resolved={result.ResolvedCount}/unknown={result.UnknownCount}",
        };
        return JsonSerializer.Serialize(envelope);
    }

    private static Dictionary<string, object?> Relationship(string elementId, string relatedId, string type)
    {
        return new Dictionary<string, object?>
        {
            ["spdxElementId"] = elementId,
            ["relatedSpdxElement"] = relatedId,
            ["relationshipType"] = type,
        };
    }

    internal static string ToConcludedLicense(string effective)
    {
        if (string.IsNullOrWhiteSpace(effective))
        {
            return "NOASSERTION";
        }

        var trimmed = effective.Trim();
        if (trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("NONE", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("NOASSERTION", StringComparison.OrdinalIgnoreCase))
        {
            // Never NONE, never "", never an "Unknown" string leak.
            return "NOASSERTION";
        }

        if (IsSingleToken(trimmed) || IsSpdxExpression(trimmed))
        {
            return trimmed;
        }

        // Multi-word non-expression (e.g. "Foo License") collapses to
        // NOASSERTION (never a SPDX name-escape object).
        return "NOASSERTION";
    }

    private static bool IsSingleToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '+'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSpdxExpression(string value)
    {
        var tokens = value.Split(new[] { ' ', '\t', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3)
        {
            return false;
        }

        var hasOperator = false;
        foreach (var token in tokens)
        {
            if (token.Equals("AND", StringComparison.Ordinal)
                || token.Equals("OR", StringComparison.Ordinal)
                || token.Equals("WITH", StringComparison.Ordinal))
            {
                hasOperator = true;
                continue;
            }

            if (!IsSingleToken(token))
            {
                return false;
            }
        }

        return hasOperator;
    }
}
