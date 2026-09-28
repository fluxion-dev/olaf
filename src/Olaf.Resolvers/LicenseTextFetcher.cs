using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace Olaf.Resolvers;

/// <summary>
/// Caps for license-text fetching (issue #71, B3). Consts, NOT CLI flags:
/// the CLI surface is intentionally unchanged (no new flags, no new exit
/// code). Failures surface as <c>Unknown</c> + reason, never throw.
/// </summary>
internal static class LicenseTextLimits
{
    /// <summary>Per-package fetch budget, enforced via a linked CTS.</summary>
    public const int PerPackageTimeoutSeconds = 5;

    /// <summary>Max download / entry bytes (1 MiB), via Content-Length gate + streaming truncation.</summary>
    public const int MaxBytes = 1_048_576;

    /// <summary>Max archive entries scanned for a license file.</summary>
    public const int MaxArchiveEntries = 512;

    /// <summary>Max bytes read from a single archive entry.</summary>
    public const int MaxEntryBytes = 1_048_576;
}

/// <summary>
/// Shared license-text fetcher (issue #71, B2): dedupes the npm/NuGet
/// <c>TryFetchLicenseTextAsync</c> dup. Used by the npm / PyPI / NuGet /
/// Cargo tarball paths (DownloadUrl from #70 Enrichment, i.e. already-fetched
/// registry bodies — NO new discovery endpoints) plus the <c>licenseUrl</c>
/// fallback. Tarball shapes: tgz / zip / nupkg / wheel / crate.
/// Filename preference <c>LICENSE* &gt; COPYING* &gt; NOTICE*</c>,
/// first-match-wins within a tier.
/// Never <c>ExtractToFile</c>: file-name-only match, stream-read of the
/// matched entry; symlinks and absolute / <c>..</c>-escaping names are
/// skipped. Reuses <see cref="ResolverHttpRetry"/> (single retry — never a
/// second retry layer, never raw retry loops).
///
/// Fallback chain (pinned, B5): embedded-file text &gt; licenseUrl fetch
/// &gt; embedded DB text &gt; null. Reason vocabulary (existing
/// <c>prefix: detail</c> style): <c>tarball-miss:&lt;detail&gt;</c> ·
/// <c>tarball-timeout</c> · <c>tarball-too-large:&lt;bytes&gt;</c> ·
/// <c>licenseurl-fetch-failed:&lt;status-or-transport&gt;</c> ·
/// <c>spdxdb-miss:&lt;id&gt;</c>. First-failure wins: the first stage that
/// fails pins the candidate reason; later stages are still attempted for
/// text, but the pinned reason is not overwritten — every null-text result
/// carries a reason (no silent null).
///
/// Extracted text fills <c>LicenseText</c> ONLY: the already-resolved SPDX
/// id stands (no re-resolution). Returns <c>(Text, FailureReason)</c> —
/// never an SPDX id. Caller-cancellation (<c>OperationCanceledException</c>
/// when the caller's token fires) is always rethrown, never swallowed.
/// </summary>
internal static class LicenseTextFetcher
{
    static LicenseTextFetcher()
    {
        // Windows-1252 fallback needs the code-pages provider on .NET Core.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<(string? Text, string? FailureReason)> TryFetchLicenseTextAsync(
        HttpClient http,
        string? tarballUrl,
        string? licenseUrl,
        string spdxId,
        CancellationToken cancellationToken,
        bool offline = false)
    {
        // Provenance-blind wrapper: existing callers keep the 2-tuple.
        // Copyright scraping (issue #72) uses the provenance overload.
        var (text, reason, _) = await TryFetchLicenseTextWithProvenanceAsync(
            http, tarballUrl, licenseUrl, spdxId, cancellationToken, offline).ConfigureAwait(false);
        return (text, reason);
    }

    /// <summary>
    /// Provenance overload (issue #72, B1): <c>IsPerPackage</c> is true
    /// ONLY for tarball-extracted or licenseUrl-fetched texts — the two
    /// per-package stages. DB-subset texts report false so callers NEVER
    /// scrape them for copyright holders (structural anti-FSF rule).
    /// </summary>
    public static async Task<(string? Text, string? FailureReason, bool IsPerPackage)> TryFetchLicenseTextWithProvenanceAsync(
        HttpClient http,
        string? tarballUrl,
        string? licenseUrl,
        string spdxId,
        CancellationToken cancellationToken,
        bool offline = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? firstFailure = null;

        // Issue #77: offline is DB-only — both network stages (tarball,
        // licenseUrl) are gated here so zero HTTP is sent.
        if (!offline && TryCreateHttpUri(tarballUrl, out var tarballUri) && tarballUri is not null)
        {
            var (tarballText, tarballReason) = await TryFetchFromTarballAsync(http, tarballUri, cancellationToken).ConfigureAwait(false);
            if (tarballText is not null)
            {
                return (tarballText, null, true);
            }

            firstFailure ??= tarballReason;
        }

        if (!offline && TryCreateHttpUri(licenseUrl, out var licenseUri) && licenseUri is not null)
        {
            var (urlText, urlReason) = await TryFetchFromLicenseUrlAsync(http, licenseUri, cancellationToken).ConfigureAwait(false);
            if (urlText is not null)
            {
                return (urlText, null, true);
            }

            firstFailure ??= urlReason;
        }

        // Issue #77: DB-first chain — embedded DB text, then the
        // SpdxLicenseTexts seed fallback, then null with spdxdb-miss.
        if (SpdxLicenseDb.TryGetText(spdxId, out var dbEntryText) && !string.IsNullOrWhiteSpace(dbEntryText))
        {
            return (dbEntryText, null, false);
        }

        if (SpdxLicenseTexts.TryGetText(spdxId, out var dbText) && !string.IsNullOrWhiteSpace(dbText))
        {
            return (dbText, null, false);
        }

        firstFailure ??= $"spdxdb-miss:{spdxId}";
        return (null, firstFailure, false);
    }

    internal static bool TryCreateHttpUri(string? value, out Uri? uri)
    {
        uri = null;
        // Single gate: EnrichmentHelpers owns the absolute-http check (no 2nd Uri.TryCreate+scheme copy here).
        var normalized = EnrichmentHelpers.NormalizeHttpUrl(value);
        if (normalized is null)
        {
            return false;
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out uri) && uri is not null;
    }

    private static async Task<(string? Text, string? FailureReason)> TryFetchFromTarballAsync(
        HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(LicenseTextLimits.PerPackageTimeoutSeconds));
        var timeoutToken = timeoutCts.Token;

        try
        {
            using var response = await ResolverHttpRetry.GetAsync(http, uri, timeoutToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, "tarball-miss:not-found");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"tarball-miss:status-{(int)response.StatusCode}");
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > LicenseTextLimits.MaxBytes)
            {
                return (null, $"tarball-too-large:{contentLength.Value}");
            }

            await using var downloadStream = await response.Content.ReadAsStreamAsync(timeoutToken).ConfigureAwait(false);
            // MaxBytes + 1 so an over-cap download is detectable (streaming
            // truncation: the tap is closed instead of buffering unbounded).
            var payload = await ReadUpToAsync(downloadStream, LicenseTextLimits.MaxBytes + 1, timeoutToken).ConfigureAwait(false);
            if (payload.Length > LicenseTextLimits.MaxBytes)
            {
                return (null, $"tarball-too-large:{payload.Length}");
            }

            var text = await ExtractLicenseTextAsync(payload, Path.GetExtension(uri.AbsolutePath), timeoutToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return (null, "tarball-miss:no-license-file");
            }

            return (text, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "tarball-timeout");
        }
        catch (HttpRequestException)
        {
            return (null, "tarball-miss:transport");
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return (null, "tarball-miss:archive-error");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "tarball-miss:archive-error");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return (null, "tarball-miss:archive-error");
        }
    }

    private static async Task<(string? Text, string? FailureReason)> TryFetchFromLicenseUrlAsync(
        HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(LicenseTextLimits.PerPackageTimeoutSeconds));
        var timeoutToken = timeoutCts.Token;

        try
        {
            using var response = await ResolverHttpRetry.GetAsync(http, uri, timeoutToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"licenseurl-fetch-failed:{(int)response.StatusCode}");
            }

            await using var bodyStream = await response.Content.ReadAsStreamAsync(timeoutToken).ConfigureAwait(false);
            // Single text file: truncate at the cap and decode what we have
            // (never fail oversized licenseUrl bodies, never throw).
            var bytes = await ReadUpToAsync(bodyStream, LicenseTextLimits.MaxBytes, timeoutToken).ConfigureAwait(false);
            var text = DecodeLicenseBytes(bytes);
            if (string.IsNullOrWhiteSpace(text))
            {
                return (null, "licenseurl-fetch-failed:empty");
            }

            return (text, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "licenseurl-fetch-failed:timeout");
        }
        catch (HttpRequestException)
        {
            return (null, "licenseurl-fetch-failed:transport");
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return (null, "licenseurl-fetch-failed:transport");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "licenseurl-fetch-failed:transport");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return (null, "licenseurl-fetch-failed:transport");
        }
    }

    internal static async Task<byte[]> ReadUpToAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var remaining = limit;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            remaining -= read;
        }

        return buffer.ToArray();
    }

    internal static async Task<string?> ExtractLicenseTextAsync(byte[] payload, string? extensionHint, CancellationToken cancellationToken)
    {
        try
        {
            if (payload is null || payload.Length == 0)
            {
                return null;
            }

            if (IsGzip(payload))
            {
                return await ExtractFromTarGzipAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            if (IsZip(payload))
            {
                return ExtractFromZip(payload);
            }

            // Magic-unknown: fall back to the URL extension hint
            // (.zip/.nupkg/.whl -> zip; .tgz/.tar.gz/.gz/.crate -> gzip+tar).
            var hint = extensionHint?.Trim().ToLowerInvariant() ?? string.Empty;
            if (hint.Equals(".zip", StringComparison.Ordinal)
                || hint.Equals(".nupkg", StringComparison.Ordinal)
                || hint.Equals(".whl", StringComparison.Ordinal))
            {
                return ExtractFromZip(payload);
            }

            if (hint.Equals(".tgz", StringComparison.Ordinal)
                || hint.Equals(".gz", StringComparison.Ordinal)
                || hint.Equals(".crate", StringComparison.Ordinal))
            {
                return await ExtractFromTarGzipAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsGzip(byte[] payload)
        => payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B;

    private static bool IsZip(byte[] payload)
        => payload.Length >= 4 && payload[0] == 0x50 && payload[1] == 0x4B
            && payload[2] == 0x03 && payload[3] == 0x04;

    private static async Task<string?> ExtractFromTarGzipAsync(byte[] payload, CancellationToken cancellationToken)
    {
        using var compressed = new MemoryStream(payload, writable: false);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: false);

        string? copying = null;
        string? notice = null;
        var seen = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Forward-only: an over-cap entry aborts the scan (candidates
            // found so far still win over a miss).
            if (++seen > LicenseTextLimits.MaxArchiveEntries)
            {
                break;
            }

            TarEntry? entry;
            try
            {
                entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return copying ?? notice;
            }

            if (entry is null)
            {
                break;
            }

            // Regular files only: skips directories, symlinks, hard links and
            // device nodes (never ExtractToFile; stream-read only).
            if (entry.EntryType != TarEntryType.RegularFile)
            {
                continue;
            }

            // Every regular entry is drained (capped) so the forward-only
            // reader stays positioned on the next header — even skipped ones.
            var data = await ReadEntryCappedAsync(entry.DataStream, cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                break;
            }

            if (!IsSafeArchiveName(entry.Name))
            {
                continue;
            }

            var tier = LicenseTier(FileNameOf(entry.Name ?? string.Empty));
            if (tier < 0)
            {
                continue;
            }

            var text = DecodeLicenseBytes(data);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // LICENSE* is unbeatable: first non-empty match wins outright.
            if (tier == 0)
            {
                return text;
            }

            if (tier == 1)
            {
                copying ??= text;
            }
            else
            {
                notice ??= text;
            }
        }

        return copying ?? notice;
    }

    private static string? ExtractFromZip(byte[] payload)
    {
        using var compressed = new MemoryStream(payload, writable: false);
        using var archive = new ZipArchive(compressed, ZipArchiveMode.Read, leaveOpen: false);

        string? copying = null;
        string? notice = null;
        var seen = 0;
        foreach (var entry in archive.Entries)
        {
            if (++seen > LicenseTextLimits.MaxArchiveEntries)
            {
                break;
            }

            var name = entry.FullName;
            if (!IsSafeArchiveName(name))
            {
                continue;
            }

            if (IsZipSymlink(entry))
            {
                continue;
            }

            var fileName = FileNameOf(name);
            if (fileName.Length == 0)
            {
                continue;
            }

            var tier = LicenseTier(fileName);
            if (tier < 0)
            {
                continue;
            }

            if (entry.Length > LicenseTextLimits.MaxEntryBytes)
            {
                continue;
            }

            using var entryStream = entry.Open();
            using var bounded = new MemoryStream();
            var chunk = new byte[8192];
            var remaining = LicenseTextLimits.MaxEntryBytes + 1;
            int read;
            while (remaining > 0 && (read = entryStream.Read(chunk, 0, Math.Min(chunk.Length, remaining))) > 0)
            {
                bounded.Write(chunk, 0, read);
                remaining -= read;
            }

            if (bounded.Length > LicenseTextLimits.MaxEntryBytes)
            {
                continue;
            }

            var text = DecodeLicenseBytes(bounded.ToArray());
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (tier == 0)
            {
                return text;
            }

            if (tier == 1)
            {
                copying ??= text;
            }
            else
            {
                notice ??= text;
            }
        }

        return copying ?? notice;
    }

    private static async Task<byte[]?> ReadEntryCappedAsync(Stream? entryStream, CancellationToken cancellationToken)
    {
        if (entryStream is null)
        {
            return null;
        }

        using var bounded = new MemoryStream();
        var chunk = new byte[8192];
        var remaining = LicenseTextLimits.MaxEntryBytes + 1;
        int read;
        while (remaining > 0 && (read = await entryStream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken).ConfigureAwait(false)) > 0)
        {
            bounded.Write(chunk, 0, read);
            remaining -= read;
        }

        // Over-cap entry data cannot be drained safely on a forward-only
        // stream: signal the caller to abort the scan (null = abort).
        return bounded.Length > LicenseTextLimits.MaxEntryBytes ? null : bounded.ToArray();
    }

    internal static bool IsZipSymlink(ZipArchiveEntry entry)
    {
        // Unix mode lives in the upper 16 bits; 0xA000 = symlink.
        // Never follow: read regular files only.
        var mode = ((uint)entry.ExternalAttributes >> 16) & 0xF000u;
        return mode == 0xA000u;
    }

    internal static bool IsSafeArchiveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // Skip absolute paths (zip-slip guard; never ExtractToFile).
        if (name.StartsWith("/", StringComparison.Ordinal)
            || name.StartsWith("\\", StringComparison.Ordinal)
            || Path.IsPathRooted(name))
        {
            return false;
        }

        if (name.Length >= 2 && name[1] == ':' && char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        // Skip any '..'-escaping segment.
        foreach (var part in name.Split('/', '\\'))
        {
            if (part.Equals("..", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static string FileNameOf(string name)
    {
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        return slash < 0 ? name : name.Substring(slash + 1);
    }

    internal static int LicenseTier(string fileName)
    {
        if (fileName.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (fileName.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (fileName.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return -1;
    }

    /// <summary>
    /// Decode pipeline (B4): UTF-8 BOM → UTF-16 BE/LE BOM → UTF-8 strict →
    /// Windows-1252 fallback. Strips NULs. Never throws (null on total
    /// failure); whitespace-only decodes as null so the caller keeps hunting.
    /// </summary>
    internal static string? DecodeLicenseBytes(byte[] bytes)
    {
        try
        {
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return CleanDecoded(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return CleanDecoded(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
            }

            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            try
            {
                var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                return CleanDecoded(strict.GetString(bytes, offset, bytes.Length - offset));
            }
            catch (DecoderFallbackException)
            {
                return CleanDecoded(Encoding.GetEncoding(1252).GetString(bytes, offset, bytes.Length - offset));
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? CleanDecoded(string text)
    {
        var stripped = text.Replace("\0", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
    }
}
