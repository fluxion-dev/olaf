using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class NuGetLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public NuGetLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("nuget", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a nuget package.");
        }

        try
        {
            var lowerName = dependency.Name.ToLowerInvariant();
            var lowerVersion = dependency.Version.ToLowerInvariant();
            var url = $"https://api.nuget.org/v3/registration5-gz-semver2/{Uri.EscapeDataString(lowerName)}/{Uri.EscapeDataString(lowerVersion)}.json";

            using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: NuGet package '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: NuGet returned {(int)response.StatusCode}.");
            }

            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            var (expression, licenseUrl) = ParseBody(body);

            var spdx = SpdxMapper.Normalize(expression)
                ?? SpdxMapper.FromLicenseUrl(licenseUrl);

            // Bug #61: live registration leaf returns catalogEntry as a URL string
            // (not an inline object), so the first doc has no license fields.
            // Follow the catalog URL once via the shared retry helper and parse
            // licenseExpression/licenseUrl from the catalog doc.
            string? catalogBody = null;
            if (spdx is null)
            {
                var (catalogExpression, catalogLicenseUrl, fetchedCatalogBody) = await FollowCatalogEntryAsync(body, cancellationToken).ConfigureAwait(false);
                catalogBody = fetchedCatalogBody;
                if (catalogExpression is not null)
                {
                    expression = catalogExpression;
                }

                if (catalogLicenseUrl is not null)
                {
                    licenseUrl = catalogLicenseUrl;
                }

                if (catalogExpression is not null || catalogLicenseUrl is not null)
                {
                    spdx = SpdxMapper.Normalize(expression)
                        ?? SpdxMapper.FromLicenseUrl(licenseUrl);
                }
            }

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = await TryFetchLicenseTextAsync(licenseUrl, cancellationToken).ConfigureAwait(false)
                ?? SpdxLicenseTexts.GetText(spdx);

            var source = string.IsNullOrWhiteSpace(licenseUrl)
                ? $"https://www.nuget.org/packages/{dependency.Name}/{dependency.Version}"
                : licenseUrl;

            // Enrichment reads the same already-fetched registration/catalog JSON
            // (NO-NEW-HTTP): packageHash+algorithm -> Hashes, authors ->
            // Supplier, packageContent -> DownloadUrl. Prefers the catalog doc
            // only when it was already fetched for license resolution above.
            // Never copies SourceUrl to download.
            var enrichment = ParseNuGetEnrichment(catalogBody ?? body, dependency)
                ?? (catalogBody is null ? null : ParseNuGetEnrichment(body, dependency));
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null, enrichment);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: NuGet request timed out: {ex.Message}");
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

    private async Task<(string? Expression, string? LicenseUrl, string? CatalogBody)> FollowCatalogEntryAsync(string body, CancellationToken ct)
    {
        var catalogUrl = ExtractCatalogEntryUrl(body);
        if (!TryCreateAbsoluteHttpUri(catalogUrl, out var catalogUri) || catalogUri is null)
        {
            return (null, null, null);
        }

        using var catalogResponse = await ResolverHttpRetry.GetAsync(_http, catalogUri, ct).ConfigureAwait(false);
        if (!catalogResponse.IsSuccessStatusCode)
        {
            return (null, null, null);
        }

        var catalogBody = await ReadBodyAsync(catalogResponse, ct).ConfigureAwait(false);
        var (expression, licenseUrl) = ParseBody(catalogBody);
        return (expression, licenseUrl, catalogBody);
    }

    private static bool TryCreateAbsoluteHttpUri(string? catalogUrl, out Uri? catalogUri)
    {
        catalogUri = null;
        if (string.IsNullOrWhiteSpace(catalogUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(catalogUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        catalogUri = uri;
        return true;
    }

    internal static Enrichment? ParseNuGetEnrichment(string body, Dependency dependency)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body) || IsXmlBody(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Scan the root plus every inline catalogEntry candidate, mirroring
            // ParseRegistrationJson: first entry carrying enrichment fields wins.
            var scopes = new List<JsonElement> { root };
            scopes.AddRange(EnumerateCatalogEntryCandidates(root));
            foreach (var scope in scopes)
            {
                if (scope.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? hash = null;
                if (EnrichmentHelpers.TryGetString(scope, "packageHash", out var packageHash))
                {
                    var algo = "sha512";
                    if (EnrichmentHelpers.TryGetString(scope, "packageHashAlgorithm", out var rawAlgo)
                        && !string.IsNullOrWhiteSpace(rawAlgo))
                    {
                        algo = rawAlgo!.Trim().ToLowerInvariant();
                    }

                    hash = algo + ":" + packageHash!.Trim();
                }

                EnrichmentHelpers.TryGetString(scope, "authors", out var authors);
                EnrichmentHelpers.TryGetString(scope, "packageContent", out var packageContent);
                if (hash is not null || authors is not null || packageContent is not null)
                {
                    return EnrichmentHelpers.Create(
                        dependency,
                        EnrichmentHelpers.HashList(hash),
                        authors,
                        packageContent);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? Expression, string? LicenseUrl) ParseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        if (IsXmlBody(body))
        {
            return ParseNuspecXml(body);
        }

        return ParseRegistrationJson(body);
    }

    private static bool IsXmlBody(string body) => body.TrimStart().StartsWith('<');

    private static (string? Expression, string? LicenseUrl) ParseRegistrationJson(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (TryGetString(root, "licenseExpression", out var expr)
            || TryGetString(root, "license", out expr))
        {
            TryGetString(root, "licenseUrl", out var url);
            return (expr, url);
        }

        foreach (var candidate in EnumerateCatalogEntryCandidates(root))
        {
            if (candidate.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var (candidateExpression, candidateLicenseUrl) = TryReadLicenseFields(candidate);
            if (candidateExpression is not null || candidateLicenseUrl is not null)
            {
                return (candidateExpression, candidateLicenseUrl);
            }
        }

        TryGetString(root, "licenseUrl", out var onlyUrl);
        return (null, onlyUrl);
    }

    private static (string? Expression, string? LicenseUrl) TryReadLicenseFields(JsonElement entry)
    {
        TryGetString(entry, "licenseExpression", out var expression);
        TryGetString(entry, "licenseUrl", out var licenseUrl);
        return (expression, licenseUrl);
    }

    private static IEnumerable<JsonElement> EnumerateCatalogEntryCandidates(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (root.TryGetProperty("catalogEntry", out var direct))
        {
            yield return direct;
        }

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("catalogEntry", out var catalogEntry))
                {
                    yield return catalogEntry;
                }

                if (item.TryGetProperty("items", out var nested) && nested.ValueKind == JsonValueKind.Array)
                {
                    foreach (var leaf in nested.EnumerateArray())
                    {
                        if (leaf.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (leaf.TryGetProperty("catalogEntry", out var leafEntry))
                        {
                            yield return leafEntry;
                        }
                    }
                }
            }
        }
    }

    private static string? ExtractCatalogEntryUrl(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        if (IsXmlBody(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            foreach (var candidate in EnumerateCatalogEntryCandidates(root))
            {
                if (candidate.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var url = candidate.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // registration5-gz-semver2 always serves Content-Encoding: gzip, even when
        // the HttpClient handler has no AutomaticDecompression (prod CLI + E2E
        // create bare `new HttpClient`). When the handler already decompressed,
        // ContentEncoding is empty and this is a plain string read. Stubs have no
        // encoding either, so this is a no-op for unit tests.
        var encodings = response.Content.Headers.ContentEncoding;
        var isGzip = HasContentEncoding(encodings, "gzip");
        var isDeflate = HasContentEncoding(encodings, "deflate");

        if (!isGzip && !isDeflate)
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream(bytes);
        using Stream decompressor = isGzip
            ? new GZipStream(ms, CompressionMode.Decompress)
            : new DeflateStream(ms, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressor);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static bool HasContentEncoding(IEnumerable<string> encodings, string name)
        => encodings.Any(encoding => encoding.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static (string? Expression, string? LicenseUrl) ParseNuspecXml(string body)
    {
        try
        {
            var doc = XDocument.Parse(body);
            var metadata = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
            if (metadata is null)
            {
                return (null, null);
            }

            var licenseEl = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
            var urlEl = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "licenseUrl");
            return (licenseEl?.Value, urlEl?.Value);
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool TryGetString(JsonElement el, string name, out string? value)
    {
        value = null;
        if (el.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!el.TryGetProperty(name, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString();
            return true;
        }

        return false;
    }

    private async Task<string?> TryFetchLicenseTextAsync(string? licenseUrl, CancellationToken ct)
    {
        if (!TryCreateAbsoluteHttpUri(licenseUrl, out var uri) || uri is null)
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
