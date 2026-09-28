using System.Globalization;
using Olaf.Core;
using Olaf.Formatters;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Olaf.Cli;

// Issue #75: policy rules file (.sbom-rules.yaml) loader (CLI concern).
// Load path uses YamlDotNet's node model (YamlStream, same YamlDotNet 16.3.0
// stack as the DeserializerBuilder serializer path — offline-safe) instead
// of a DTO deserializer so validator errors can carry best-effort
// mapping-node Start lines (B4); a DTO round-trip cannot supply node lines.
// Line fallback when a node Start is unavailable: 1:1 (documented).
public sealed class RulesExceptionRow
{
    public string? Purl { get; set; }

    public string? Name { get; set; }

    public string? License { get; set; }

    public string? Reason { get; set; }

    public DateOnly? Expires { get; set; }
}

public sealed class RulesFile
{
    public HashSet<string> Allow { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Deny { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ExcludeEcosystems { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<RulesExceptionRow> Exceptions { get; } = new();

    public bool FailOnUnknown { get; set; }

    public bool FailOnUnresolved { get; set; }
}

public static class RulesLoader
{
    // B1: unresolved is the Unknown subset whose Reason carries one of these
    // exact prefixes (case-insensitive prefix match, pinned list).
    private static readonly string[] UnresolvedReasonPrefixes =
    [
        "transport",
        "timeout",
        "not-found",
        "resolver-error",
    ];

    // Injectable clock; default is UTC today (frozen dates in tests only).
    public static Func<DateOnly> UtcToday { get; set; } = () => DateOnly.FromDateTime(DateTime.UtcNow);

    // Pure loader: returns (RulesFile?, errors-with-lines). Syntax failures
    // throw YamlDotNet.Core.YamlException; Program.cs maps those to
    // {file}:{line}:{col} + exit 2.
    public static (RulesFile? Rules, IReadOnlyList<string> Errors) TryParse(string yamlText, string displayPath)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        ArgumentNullException.ThrowIfNull(displayPath);

        var errors = new List<string>();
        var stream = new YamlStream();
        stream.Load(new StringReader(yamlText));

        if (stream.Documents.Count == 0)
        {
            return (new RulesFile(), errors);
        }

        var root = stream.Documents[0].RootNode;
        if (root is YamlScalarNode scalar && string.IsNullOrWhiteSpace(scalar.Value))
        {
            return (new RulesFile(), errors);
        }

        if (root is not YamlMappingNode mapping)
        {
            errors.Add($"{At(displayPath, root.Start)}: top-level mapping expected.");
            return (null, errors);
        }

        var rules = new RulesFile();
        foreach (var entry in mapping.Children)
        {
            var key = (entry.Key as YamlScalarNode)?.Value?.Trim();
            var loc = At(displayPath, entry.Key.Start);
            switch (key)
            {
                case "allow":
                    ReadStringList(entry.Value, rules.Allow, "allow", loc, displayPath, errors);
                    break;
                case "deny":
                    ReadStringList(entry.Value, rules.Deny, "deny", loc, displayPath, errors);
                    break;
                case "excludeEcosystems":
                    ReadStringList(entry.Value, rules.ExcludeEcosystems, "excludeEcosystems", loc, displayPath, errors);
                    break;
                case "failOnUnknown":
                    rules.FailOnUnknown = ReadBool(entry.Value, "failOnUnknown", loc, errors);
                    break;
                case "failOnUnresolved":
                    rules.FailOnUnresolved = ReadBool(entry.Value, "failOnUnresolved", loc, errors);
                    break;
                case "exceptions":
                    ReadExceptions(entry.Value, rules.Exceptions, loc, displayPath, errors);
                    break;
                default:
                    errors.Add($"{loc}: unknown key '{key ?? "?"}'.");
                    break;
            }
        }

        return errors.Count > 0 ? (null, errors) : (rules, errors);
    }

    // B5: input-adjacent resolution only — file input uses its directory, dir
    // input uses itself. Probes .sbom-rules.yaml THEN .olaf-rules.yaml. No
    // cwd search, no walk-up. Dual presence: primary wins, shadowed=true.
    public static string? FindAdjacentRulesFile(string inputPath, out bool shadowedSecondary)
    {
        shadowedSecondary = false;
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return null;
        }

        string dir;
        if (File.Exists(inputPath))
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty;
        }
        else if (Directory.Exists(inputPath))
        {
            dir = inputPath;
        }
        else
        {
            return null;
        }

        if (dir.Length == 0)
        {
            return null;
        }

        var primary = Path.Combine(dir, ".sbom-rules.yaml");
        var secondary = Path.Combine(dir, ".olaf-rules.yaml");
        if (File.Exists(primary))
        {
            shadowedSecondary = File.Exists(secondary);
            return primary;
        }

        if (File.Exists(secondary))
        {
            return secondary;
        }

        return null;
    }

    public static bool IsUnresolved(ResolvedLicense license)
    {
        ArgumentNullException.ThrowIfNull(license);
        if (!license.Status.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var reason = license.Reason;
        if (string.IsNullOrEmpty(reason))
        {
            return false;
        }

        foreach (var prefix in UnresolvedReasonPrefixes)
        {
            if (reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // B3 match: (purl exact OR name exact, case-insensitive) AND license
    // exact (effective-SPDX, case-insensitive Ordinal).
    public static RulesExceptionRow? MatchException(ResolvedLicense license, IReadOnlyList<RulesExceptionRow> exceptions)
    {
        ArgumentNullException.ThrowIfNull(license);
        ArgumentNullException.ThrowIfNull(exceptions);
        var effective = LicenseDisplay.EffectiveSpdx(license);
        foreach (var row in exceptions)
        {
            if (!string.Equals(row.License?.Trim(), effective, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var purlHit = !string.IsNullOrWhiteSpace(row.Purl)
                && string.Equals(row.Purl.Trim(), license.Enrichment?.Purl, StringComparison.OrdinalIgnoreCase);
            var nameHit = !string.IsNullOrWhiteSpace(row.Name)
                && string.Equals(row.Name.Trim(), license.Dependency.Name, StringComparison.OrdinalIgnoreCase);
            if (purlHit || nameHit)
            {
                return row;
            }
        }

        return null;
    }

    // B3 expiry: ISO date-only UTC; date >= today = valid; missing = perpetual.
    public static bool IsExceptionActive(RulesExceptionRow row, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.Expires.HasValue || row.Expires.Value >= today;
    }

    private static string At(string displayPath, Mark mark)
    {
        return mark.Line <= 0 ? $"{displayPath}:1:1" : $"{displayPath}:{mark.Line}:{mark.Column}";
    }

    private static void ReadStringList(
        YamlNode node,
        HashSet<string> target,
        string key,
        string loc,
        string displayPath,
        List<string> errors)
    {
        if (node is not YamlSequenceNode sequence)
        {
            errors.Add($"{loc}: '{key}' must be a list of strings.");
            return;
        }

        foreach (var item in sequence.Children)
        {
            if (item is not YamlScalarNode scalar)
            {
                errors.Add($"{At(displayPath, item.Start)}: '{key}' entries must be strings.");
                continue;
            }

            var value = scalar.Value?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                target.Add(value);
            }
        }
    }

    private static bool ReadBool(YamlNode node, string key, string loc, List<string> errors)
    {
        if (node is YamlScalarNode scalar && bool.TryParse(scalar.Value?.Trim(), out var value))
        {
            return value;
        }

        errors.Add($"{loc}: '{key}' must be true or false.");
        return false;
    }

    private static void ReadExceptions(
        YamlNode node,
        List<RulesExceptionRow> target,
        string loc,
        string displayPath,
        List<string> errors)
    {
        if (node is not YamlSequenceNode sequence)
        {
            errors.Add($"{loc}: 'exceptions' must be a list of mappings.");
            return;
        }

        foreach (var item in sequence.Children)
        {
            if (item is not YamlMappingNode mapping)
            {
                errors.Add($"{At(displayPath, item.Start)}: 'exceptions' entries must be mappings.");
                continue;
            }

            var itemLoc = At(displayPath, item.Start);
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
            var ok = true;
            foreach (var entry in mapping.Children)
            {
                var field = (entry.Key as YamlScalarNode)?.Value?.Trim();
                var fieldLoc = At(displayPath, entry.Key.Start);
                switch (field)
                {
                    case "purl":
                    case "name":
                    case "license":
                    case "reason":
                    case "expires":
                        if (entry.Value is not YamlScalarNode scalar)
                        {
                            errors.Add($"{fieldLoc}: exception '{field}' must be a string.");
                            ok = false;
                        }
                        else
                        {
                            fields[field] = scalar.Value?.Trim();
                        }

                        break;
                    default:
                        errors.Add($"{fieldLoc}: unknown exception key '{field ?? "?"}'.");
                        ok = false;
                        break;
                }
            }

            if (!ok)
            {
                continue;
            }

            fields.TryGetValue("purl", out var purl);
            fields.TryGetValue("name", out var name);
            fields.TryGetValue("license", out var license);
            fields.TryGetValue("reason", out var reason);
            fields.TryGetValue("expires", out var expires);

            if (string.IsNullOrWhiteSpace(purl) && string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{itemLoc}: exception must specify 'purl' or 'name'.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(license))
            {
                errors.Add($"{itemLoc}: exception must specify 'license'.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                errors.Add($"{itemLoc}: exception must specify a non-empty 'reason'.");
                continue;
            }

            DateOnly? expiresDate = null;
            if (!string.IsNullOrWhiteSpace(expires))
            {
                if (!DateOnly.TryParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    errors.Add($"{itemLoc}: exception 'expires' must be an ISO date (YYYY-MM-DD).");
                    continue;
                }

                expiresDate = parsed;
            }

            target.Add(new RulesExceptionRow
            {
                Purl = string.IsNullOrWhiteSpace(purl) ? null : purl.Trim(),
                Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                License = license.Trim(),
                Reason = reason.Trim(),
                Expires = expiresDate,
            });
        }
    }
}
