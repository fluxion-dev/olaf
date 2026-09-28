using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class ComposerLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public ComposerLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("composer", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a composer package.");
        }

        if (!dependency.Name.Contains('/', StringComparison.Ordinal))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: composer package name must be 'vendor/package'.");
        }

        try
        {
            var url = $"https://repo.packagist.org/p2/{dependency.Name.Trim()}.json";
            using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: composer package '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: packagist returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rawLicense = ParsePackagistJson(body, dependency.Name);

            var spdx = SpdxMapper.Normalize(rawLicense);

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = SpdxLicenseTexts.GetText(spdx);
            var source = $"https://packagist.org/packages/{dependency.Name}";
            // Enrichment reads the same already-fetched packagist JSON
            // (NO-NEW-HTTP): dist.shasum -> Hashes ("sha1:"), authors[] ->
            // Supplier, dist.url -> DownloadUrl. Never copies SourceUrl.
            var enrichment = ParseComposerEnrichment(body, dependency.Name, dependency);
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null, enrichment);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: packagist request timed out: {ex.Message}");
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

    internal static Enrichment? ParseComposerEnrichment(string body, string packageName, Dependency dependency)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("packages", out var packages)
                || packages.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement versions = default;
            var found = false;
            if (packages.TryGetProperty(packageName, out var exact)
                && exact.ValueKind == JsonValueKind.Array)
            {
                versions = exact;
                found = true;
            }
            else
            {
                foreach (var prop in packages.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array
                        && string.Equals(prop.Name, packageName, StringComparison.OrdinalIgnoreCase))
                    {
                        versions = prop.Value;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                return null;
            }

            // First entry carrying dist/authors data wins (mirrors the
            // first-license-wins scan in ParsePackagistJson).
            foreach (var entry in versions.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? hash = null;
                string? distUrl = null;
                if (entry.TryGetProperty("dist", out var dist) && dist.ValueKind == JsonValueKind.Object)
                {
                    if (EnrichmentHelpers.TryGetString(dist, "shasum", out var shasum))
                    {
                        hash = "sha1:" + shasum!.Trim();
                    }

                    if (EnrichmentHelpers.TryGetString(dist, "url", out var du))
                    {
                        distUrl = du;
                    }
                }

                string? supplier = null;
                if (entry.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
                {
                    foreach (var author in authors.EnumerateArray())
                    {
                        if (author.ValueKind == JsonValueKind.Object)
                        {
                            if (EnrichmentHelpers.TryGetString(author, "name", out var an))
                            {
                                supplier = an;
                                break;
                            }

                            if (EnrichmentHelpers.TryGetString(author, "email", out var ae))
                            {
                                supplier = ae;
                                break;
                            }
                        }
                        else if (author.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(author.GetString()))
                        {
                            supplier = author.GetString()!.Trim();
                            break;
                        }
                    }
                }

                if (hash is not null || supplier is not null || distUrl is not null)
                {
                    return EnrichmentHelpers.Create(
                        dependency,
                        EnrichmentHelpers.HashList(hash),
                        supplier,
                        distUrl);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? ParsePackagistJson(string body, string packageName)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("packages", out var packages)
                || packages.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement versions = default;
            var found = false;
            if (packages.TryGetProperty(packageName, out var exact)
                && exact.ValueKind == JsonValueKind.Array)
            {
                versions = exact;
                found = true;
            }
            else
            {
                foreach (var prop in packages.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array
                        && string.Equals(prop.Name, packageName, StringComparison.OrdinalIgnoreCase))
                    {
                        versions = prop.Value;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                return null;
            }

            foreach (var entry in versions.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!entry.TryGetProperty("license", out var lic))
                {
                    continue;
                }

                switch (lic.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = lic.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            return s.Trim();
                        }

                        break;
                    case JsonValueKind.Array:
                        foreach (var item in lic.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(item.GetString()))
                            {
                                return item.GetString()!.Trim();
                            }
                        }

                        break;
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
