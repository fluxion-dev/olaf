using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

/// <summary>
/// Shared enrichment constructors (issue #70, B2). Enrichment is harvested
/// ONLY from already-fetched payloads — no new HTTP. Absent fields stay
/// null (never "" or fabricated values); when hashes, supplier, download
/// URL, AND copyright holders are ALL absent the whole Enrichment is null
/// (omit-null serialization downstream). Purl flows from PurlBuilder only
/// when at least one other field is present. SourceUrl is never copied to
/// download. CopyrightHolders (issue #72, 5th trailing optional slot) are
/// attached by resolvers from per-package texts with author fallback.
/// </summary>
internal static class EnrichmentHelpers
{
    internal static Enrichment? Create(
        Dependency dependency,
        string[]? hashes,
        string? supplier,
        string? downloadUrl,
        string[]? copyrightHolders = null)
    {
        var cleanHashes = (hashes is null || hashes.Length == 0) ? null : hashes;
        var cleanSupplier = string.IsNullOrWhiteSpace(supplier) ? null : supplier.Trim();
        var cleanDownload = NormalizeHttpUrl(downloadUrl);
        var cleanHolders = CleanHolders(copyrightHolders);
        if (cleanHashes is null && cleanSupplier is null && cleanDownload is null && cleanHolders is null)
        {
            return null;
        }

        return new Enrichment(
            PurlBuilder.Build(dependency.Ecosystem, dependency.Name, dependency.Version),
            cleanHashes,
            cleanSupplier,
            cleanDownload,
            cleanHolders);
    }

    internal static string[]? CleanHolders(string[]? holders)
    {
        if (holders is null || holders.Length == 0)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>(holders.Length);
        foreach (var holder in holders)
        {
            if (string.IsNullOrWhiteSpace(holder))
            {
                continue;
            }

            var trimmed = holder.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
            {
                continue;
            }

            list.Add(trimmed);
        }

        return list.Count == 0 ? null : list.ToArray();
    }

    internal static string? NormalizeHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    internal static string[]? HashList(params string?[] entries)
    {
        if (entries is null || entries.Length == 0)
        {
            return null;
        }

        var list = new List<string>(entries.Length);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                list.Add(entry.Trim());
            }
        }

        return list.Count == 0 ? null : list.ToArray();
    }

    internal static bool TryGetString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = prop.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }
}
