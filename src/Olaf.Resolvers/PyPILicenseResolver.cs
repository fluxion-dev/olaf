using System.Net;
using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class PyPILicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public PyPILicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("pypi", StringComparison.OrdinalIgnoreCase)
            && !dependency.Ecosystem.Equals("pip", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a pypi package.");
        }

        try
        {
            var url = $"https://pypi.org/pypi/{Uri.EscapeDataString(dependency.Name)}/{Uri.EscapeDataString(dependency.Version)}/json";
            using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: PyPI package '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: PyPI returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (rawLicense, classifiers, licenseExpression) = ParsePyPIJson(body);

            var spdx = SpdxMapper.Normalize(licenseExpression)
                ?? SpdxMapper.Normalize(rawLicense);

            if (spdx is null && classifiers is not null)
            {
                foreach (var classifier in classifiers)
                {
                    spdx = SpdxMapper.FromClassifier(classifier);
                    if (spdx is not null)
                    {
                        break;
                    }
                }
            }

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = SpdxLicenseTexts.GetText(spdx);
            var source = $"https://pypi.org/project/{dependency.Name}/{dependency.Version}/";
            // Enrichment reads the same already-fetched JSON API body
            // (NO-NEW-HTTP): info.digests via urls[] -> Hashes, info.author ->
            // Supplier, urls[] file URL -> DownloadUrl. Never copies SourceUrl.
            var enrichment = ParsePyPIEnrichment(body, dependency);
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null, enrichment);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: PyPI request timed out: {ex.Message}");
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

    internal static Enrichment? ParsePyPIEnrichment(string body, Dependency dependency)
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
            if (root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                if (EnrichmentHelpers.TryGetString(info, "author", out var author))
                {
                    supplier = author;
                }
            }

            string[]? hashes = null;
            string? downloadUrl = null;
            if (root.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in urls.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (hashes is null
                        && entry.TryGetProperty("digests", out var digests)
                        && digests.ValueKind == JsonValueKind.Object)
                    {
                        var parts = new List<string>();
                        foreach (var digest in digests.EnumerateObject())
                        {
                            if (digest.Value.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(digest.Value.GetString()))
                            {
                                parts.Add(digest.Name.Trim().ToLowerInvariant() + ":" + digest.Value.GetString()!.Trim());
                            }
                        }

                        if (parts.Count > 0)
                        {
                            hashes = parts.ToArray();
                        }
                    }

                    if (downloadUrl is null && EnrichmentHelpers.TryGetString(entry, "url", out var fileUrl))
                    {
                        downloadUrl = fileUrl;
                    }

                    if (hashes is not null && downloadUrl is not null)
                    {
                        break;
                    }
                }
            }

            return EnrichmentHelpers.Create(dependency, hashes, supplier, downloadUrl);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? License, List<string>? Classifiers, string? Expression) ParsePyPIJson(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        string? license = null;
        if (info.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.String)
        {
            license = lic.GetString();
        }

        string? expression = null;
        if (info.TryGetProperty("license_expression", out var expr) && expr.ValueKind == JsonValueKind.String)
        {
            expression = expr.GetString();
        }

        List<string>? classifiers = null;
        if (info.TryGetProperty("classifiers", out var cls) && cls.ValueKind == JsonValueKind.Array)
        {
            classifiers = new List<string>();
            foreach (var item in cls.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        classifiers.Add(s);
                    }
                }
            }
        }

        return (license, classifiers, expression);
    }
}
