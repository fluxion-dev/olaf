namespace Olaf.Core;

/// <summary>
/// Optional enrichment harvested from already-fetched registry payloads
/// (issue #70). No new HTTP is ever issued for enrichment: each resolver
/// reads these fields from the response body it already fetched for license
/// resolution. Absent fields stay null (never "" or fabricated values), and
/// SourceUrl is never copied into DownloadUrl.
/// Hashes entries are "algo:value" strings (e.g. "sha512:abc...").
/// Parser hashes (go.sum h1, npm-lock integrity) are deferred (null).
/// </summary>
public sealed record Enrichment(
    string? Purl,
    string[]? Hashes,
    string? Supplier,
    string? DownloadUrl);
