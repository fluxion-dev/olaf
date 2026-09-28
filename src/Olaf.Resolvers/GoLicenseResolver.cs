using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class GoLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public GoLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("go", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a go module.");
        }

        try
        {
            var url = $"https://proxy.golang.org/{dependency.Name.Trim('/')}/@v/{Uri.EscapeDataString(dependency.Version)}.info";
            using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: go module '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: Go proxy returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rawLicense = ParseGoProxyJson(body);

            var spdx = SpdxMapper.Normalize(rawLicense)
                ?? SpdxMapper.FromLicenseUrl(rawLicense);

            if (spdx is null)
            {
                return await TrySourceZipFallbackAsync(dependency, cancellationToken).ConfigureAwait(false);
            }

            var text = SpdxLicenseTexts.GetText(spdx);
            var source = $"https://pkg.go.dev/{dependency.Name}@{dependency.Version}";
            // PARTIAL enrichment (NO-NEW-HTTP): the module zip URL is
            // constructed, never fetched, -> DownloadUrl. The .ziphash
            // endpoint is unfetched -> Hashes null; author null.
            var enrichment = EnrichmentHelpers.Create(
                dependency,
                hashes: null,
                supplier: null,
                $"https://proxy.golang.org/{dependency.Name.Trim('/')}/@v/{Uri.EscapeDataString(dependency.Version)}.zip");
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null, enrichment);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: Go proxy request timed out: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"transport-error: {ex.Message}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"parse-error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"resolver-error: {ex.Message}");
        }
    }

    internal static string? ParseGoProxyJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in new[] { "License", "license", "licenseExpression", "license_expression", "SPDX-License-Identifier", "spdx" })
            {
                if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var s = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return s;
                    }
                }
            }

            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "license", "License", "licenseExpression" })
                {
                    if (version.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            return s;
                        }
                    }
                }
            }

            if (root.TryGetProperty("licensed", out var licensed) && licensed.ValueKind == JsonValueKind.Object
                && licensed.TryGetProperty("declared", out var declared) && declared.ValueKind == JsonValueKind.String)
            {
                var s = declared.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ResolvedLicense> TrySourceZipFallbackAsync(Dependency dependency, CancellationToken cancellationToken)
    {
        var zipUrl = $"https://proxy.golang.org/{dependency.Name.Trim('/')}/@v/{Uri.EscapeDataString(dependency.Version)}.zip";
        var sourceUrl = $"https://pkg.go.dev/{dependency.Name}@{dependency.Version}";

        // Caps only (issue #71): the zip scan below is unchanged on the happy
        // path — a linked 5s budget, a 512-entry cap, absolute/..-escaping and
        // symlink skips, and a 1 MiB per-entry cap bound it. No id
        // re-resolution change: file content still maps via SpdxMapper only.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(LicenseTextLimits.PerPackageTimeoutSeconds));
        var timeoutToken = timeoutCts.Token;

        try
        {
            using var response = await _http.GetAsync(zipUrl, timeoutToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: go module '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: Go proxy returned {(int)response.StatusCode}.");
            }

            await using var zipStream = await response.Content.ReadAsStreamAsync(timeoutToken).ConfigureAwait(false);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

            var seen = 0;
            foreach (var entry in archive.Entries)
            {
                if (++seen > LicenseTextLimits.MaxArchiveEntries)
                {
                    break;
                }

                var name = entry.FullName;
                if (!LicenseTextFetcher.IsSafeArchiveName(name)
                    || LicenseTextFetcher.IsZipSymlink(entry))
                {
                    continue;
                }

                var fileName = Path.GetFileName(name);
                if (string.IsNullOrEmpty(fileName))
                {
                    continue;
                }

                var isLicenseFile = string.Equals(fileName, "LICENSE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "LICENSE.md", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "LICENSE.txt", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "COPYING", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "COPYING.md", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "COPYRIGHT", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "NOTICE", StringComparison.OrdinalIgnoreCase);

                if (!isLicenseFile)
                {
                    continue;
                }

                if (entry.Length > LicenseTextLimits.MaxEntryBytes)
                {
                    continue;
                }

                await using var entryStream = entry.Open();
                var entryBytes = await LicenseTextFetcher.ReadUpToAsync(entryStream, LicenseTextLimits.MaxEntryBytes + 1, timeoutToken).ConfigureAwait(false);
                if (entryBytes.Length > LicenseTextLimits.MaxEntryBytes)
                {
                    continue;
                }

                var content = LicenseTextFetcher.DecodeLicenseBytes(entryBytes);

                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var spdx = SpdxMapper.Normalize(content)
                    ?? SpdxMapper.FromLicenseText(content)
                    ?? SpdxMapper.FromLicenseUrl(content);

                if (spdx is not null)
                {
                    // Same PARTIAL enrichment as the .info path: constructed zip
                    // URL -> DownloadUrl; Hashes null; author null.
                    var zipEnrichment = EnrichmentHelpers.Create(
                        dependency,
                        hashes: null,
                        supplier: null,
                        zipUrl);
                    return new ResolvedLicense(dependency, spdx, SpdxLicenseTexts.GetText(spdx), sourceUrl, "Resolved", null, zipEnrichment);
                }

                // If we found a license file but couldn't map to SPDX, report what we found
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"license-unknown: found license file '{fileName}' but could not map to SPDX.");
            }

            return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: no license file found in source zip.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: source zip request timed out: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"transport-error: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"parse-error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"resolver-error: {ex.Message}");
        }
    }
}
