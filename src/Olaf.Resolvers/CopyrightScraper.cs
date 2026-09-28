using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Resolvers;

/// <summary>
/// Copyright-holder scraper (issue #72, B1/B4). Pure sync — no
/// <c>HttpClient</c>, no network: extracts holders from an in-memory
/// license text the caller already fetched. Runs ONLY on per-package
/// texts (tarball-extracted or licenseUrl-fetched; callers prove
/// provenance via <see cref="LicenseTextFetcher"/> and pass null for
/// SPDX-DB subset texts — NEVER scrape those: generic FSF/Apache/Perl
/// holders would false-attribute the package).
///
/// Year-anchored patterns only: <c>Copyright (c)</c> / <c>©</c> /
/// <c>(c)</c> / <c>Copyright ©</c> + year (or year range/list) + holder
/// to end of line. Template/placeholder holders are denied
/// defense-in-depth: Free Software Foundation, Apache Software
/// Foundation, AAL-attribution authors, bare contributors, year/name
/// placeholders (&lt;year&gt;/[year]/&lt;name&gt;/XXXX), Gnomovision,
/// Yoyodyne. Caps live HERE and nowhere else: max 5 per package,
/// deduped case-folded, hard-cut at 200 chars (no ellipsis —
/// byte-stability). Formatters do zero re-truncation. Null/empty input
/// yields null (omit-null downstream).
/// </summary>
internal static partial class CopyrightScraper
{
    internal const int MaxHolders = 5;
    internal const int MaxHolderLength = 200;

    // Marker (copyright word with optional (c)/© tail, bare ©, bare (c))
    // + year or year range/list + holder to end of line. Case-insensitive
    // so "Copyright", "COPYRIGHT", "(C)" all anchor.
    [GeneratedRegex(
        @"(?:copyright(?:\s*\(c\)|\s*©)?|©|\(c\))\s*(?<years>\d{4}(?:\s*[-,–—/]\s*\d{2,4})*)\s*[:;,.·\-–—]?\s*(?<holder>[^\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex HolderPattern();

    [GeneratedRegex(
        @"\s*\.?\s*all rights reserved\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RightsReservedTail();

    internal static string[]? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var holders = new List<string>(MaxHolders);
        foreach (var line in text.Split('\n'))
        {
            if (holders.Count >= MaxHolders)
            {
                break;
            }

            foreach (Match match in HolderPattern().Matches(line))
            {
                if (holders.Count >= MaxHolders)
                {
                    break;
                }

                var holder = CleanHolder(match.Groups["holder"].Value);
                if (holder is null || !seen.Add(holder))
                {
                    continue;
                }

                holders.Add(holder.Length > MaxHolderLength ? holder.Substring(0, MaxHolderLength) : holder);
            }
        }

        return holders.Count == 0 ? null : holders.ToArray();
    }

    internal static string? CleanHolder(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var holder = candidate.Trim();

        // Leading separators left over from the year ("2024: Acme", "2024 - Acme").
        holder = holder.TrimStart(':', ';', ',', '.', '-', '–', '—', '·').Trim();
        if (holder.Length == 0)
        {
            return null;
        }

        // "Copyright (c) 2024 by Acme Corp" -> "Acme Corp".
        if (holder.StartsWith("by ", StringComparison.OrdinalIgnoreCase))
        {
            holder = holder.Substring(3).Trim();
            if (holder.Length == 0)
            {
                return null;
            }
        }

        // "Acme Corp. All rights reserved." -> "Acme Corp".
        holder = RightsReservedTail().Replace(holder, string.Empty).Trim();
        if (holder.Length == 0)
        {
            return null;
        }

        return IsDenied(holder) ? null : holder;
    }

    internal static bool IsDenied(string holder)
    {
        // Template holders (defense-in-depth behind the B1 structural rule).
        if (holder.Contains("free software foundation", StringComparison.OrdinalIgnoreCase)
            || holder.Contains("apache software foundation", StringComparison.OrdinalIgnoreCase)
            || holder.Contains("aal-authors", StringComparison.OrdinalIgnoreCase)
            || holder.Contains("attribution assurance", StringComparison.OrdinalIgnoreCase)
            || holder.Contains("gnomovision", StringComparison.OrdinalIgnoreCase)
            || holder.Contains("yoyodyne", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Bare contributors ("contributors" / "the contributors" — a
        // catch-all, never a notice owner).
        var bare = holder.Trim().TrimEnd('.');
        if (bare.Equals("contributors", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("the contributors", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Year/name placeholders: any angle/square bracket remnant, or XXXX.
        if (holder.Contains('<') || holder.Contains('>') || holder.Contains('[') || holder.Contains(']'))
        {
            return true;
        }

        if (holder.Contains("xxxx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Precedence (issue #72, B2): scraped-text-first, metadata-author
    /// fallback ONLY when the per-package text yields zero. The caller
    /// passes null text for non-per-package (DB) sources — those are
    /// never scraped, only the fallback applies.
    /// </summary>
    internal static string[]? ResolveHolders(string? perPackageText, string? authorFallback)
    {
        var scraped = Extract(perPackageText);
        if (scraped is { Length: > 0 })
        {
            return scraped;
        }

        return FromAuthorFallback(authorFallback);
    }

    /// <summary>
    /// Metadata-author fallback (issue #72, B1c/B2): promotes the already-
    /// fetched registry author when text scraping yields zero. Email
    /// addresses are NEVER promoted without a display name ("Name
    /// &lt;mail@host&gt;" yields "Name"; bare "mail@host" yields null).
    /// </summary>
    internal static string[]? FromAuthorFallback(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            return null;
        }

        var text = author.Trim();
        var open = text.IndexOf('<');
        var close = text.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            text = text.Substring(0, open).Trim().Trim('"').Trim('\'').Trim();
        }
        else if (text.Contains('@'))
        {
            return null;
        }

        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > MaxHolderLength)
        {
            text = text.Substring(0, MaxHolderLength);
        }

        return new[] { text };
    }

    /// <summary>
    /// Attaches resolved holders to the resolver's enrichment: null
    /// enrichment + holders builds via <see cref="EnrichmentHelpers"/>
    /// (purl flows — another field is present); existing enrichment is
    /// extended with a record-<c>with</c>. Null holders return the
    /// enrichment untouched. Supplier is never rewritten.
    /// </summary>
    internal static Enrichment? AttachHolders(
        Dependency dependency,
        Enrichment? enrichment,
        string? perPackageText,
        string? authorFallback)
    {
        var holders = ResolveHolders(perPackageText, authorFallback);
        if (holders is null)
        {
            return enrichment;
        }

        return enrichment is null
            ? EnrichmentHelpers.Create(dependency, null, null, null, holders)
            : enrichment with { CopyrightHolders = holders };
    }
}
