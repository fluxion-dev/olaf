using System.IO.Compression;
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
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
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
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
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

        try
        {
            using var response = await _http.GetAsync(zipUrl, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: go module '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: Go proxy returned {(int)response.StatusCode}.");
            }

            await using var zipStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: false);

            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name is null)
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

                await using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream, leaveOpen: true);
                var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var spdx = SpdxMapper.Normalize(content)
                    ?? SpdxMapper.FromLicenseUrl(content);

                if (spdx is not null)
                {
                    return new ResolvedLicense(dependency, spdx, SpdxLicenseTexts.GetText(spdx), sourceUrl, "Resolved", null);
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
        catch (Exception ex) when (ex is System.IO.IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"parse-error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"resolver-error: {ex.Message}");
        }
    }
}
