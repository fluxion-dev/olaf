using System.Net;
using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class BundlerLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public BundlerLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("bundler", StringComparison.OrdinalIgnoreCase)
            && !dependency.Ecosystem.Equals("gem", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a bundler gem.");
        }

        try
        {
            var url = $"https://rubygems.org/api/v1/gems/{Uri.EscapeDataString(dependency.Name)}.json";
            using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: bundler gem '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: rubygems returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rawLicense = ParseRubyGemsJson(body);

            var spdx = SpdxMapper.Normalize(rawLicense);

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = SpdxLicenseTexts.GetText(spdx);
            var source = $"https://rubygems.org/gems/{dependency.Name}";
            // PARTIAL enrichment (NO-NEW-HTTP): authors -> Supplier from the
            // same already-fetched rubygems JSON; DownloadUrl is constructed
            // canonical from the LOCKED dependency (never gem_uri verbatim).
            // Yanked-link tradeoff: names locked version always, never verified.
            // Versioned sha lives on an unfetched endpoint -> Hashes null.
            var enrichment = ParseBundlerEnrichment(body, dependency);
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null, enrichment);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: rubygems request timed out: {ex.Message}");
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

    internal static Enrichment? ParseBundlerEnrichment(string body, Dependency dependency)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? supplier = null;
            if (root.TryGetProperty("authors", out var authors))
            {
                if (authors.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(authors.GetString()))
                {
                    supplier = authors.GetString()!.Trim();
                }
                else if (authors.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in authors.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            supplier = item.GetString()!.Trim();
                            break;
                        }
                    }
                }
            }

            // Canonical locked-version DownloadUrl (issue #167, decision B;
            // Go precedent GoLicenseResolver.cs:51-58): built from the LOCKED
            // dependency, never gem_uri verbatim (names latest). Never
            // verified — no verifying fetch (rejected option A). NO-NEW-HTTP.
            var downloadUrl = $"https://rubygems.org/downloads/{Uri.EscapeDataString(dependency.Name)}-{Uri.EscapeDataString(dependency.Version)}.gem";

            return EnrichmentHelpers.Create(
                dependency,
                hashes: null,
                supplier,
                downloadUrl);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? ParseRubyGemsJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("licenses", out var licenses)
                && licenses.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in licenses.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        var normalized = SpdxMapper.Normalize(item.GetString()!.Trim());
                        if (normalized is not null)
                        {
                            return item.GetString()!.Trim();
                        }
                    }
                }
            }

            foreach (var key in new[] { "license", "License" })
            {
                if (root.TryGetProperty(key, out var prop)
                    && prop.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(prop.GetString()))
                {
                    return prop.GetString()!.Trim();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
