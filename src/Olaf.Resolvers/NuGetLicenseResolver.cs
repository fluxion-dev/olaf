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

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (expression, licenseUrl) = ParseBody(body);

            var spdx = SpdxMapper.Normalize(expression)
                ?? SpdxMapper.FromLicenseUrl(licenseUrl);

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = await TryFetchLicenseTextAsync(licenseUrl, cancellationToken).ConfigureAwait(false)
                ?? SpdxLicenseTexts.GetText(spdx);

            var source = string.IsNullOrWhiteSpace(licenseUrl)
                ? $"https://www.nuget.org/packages/{dependency.Name}/{dependency.Version}"
                : licenseUrl;

            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
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

    private static (string? Expression, string? LicenseUrl) ParseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('<'))
        {
            return ParseNuspecXml(body);
        }

        return ParseRegistrationJson(body);
    }

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

        if (root.TryGetProperty("catalogEntry", out var entry) && entry.ValueKind == JsonValueKind.Object)
        {
            TryGetString(entry, "licenseExpression", out var e2);
            TryGetString(entry, "licenseUrl", out var u2);
            if (e2 is not null || u2 is not null)
            {
                return (e2, u2);
            }
        }

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("catalogEntry", out var ce) && ce.ValueKind == JsonValueKind.Object)
                {
                    TryGetString(ce, "licenseExpression", out var e3);
                    TryGetString(ce, "licenseUrl", out var u3);
                    if (e3 is not null || u3 is not null)
                    {
                        return (e3, u3);
                    }
                }

                if (item.TryGetProperty("items", out var nested) && nested.ValueKind == JsonValueKind.Array)
                {
                    foreach (var leaf in nested.EnumerateArray())
                    {
                        if (leaf.TryGetProperty("catalogEntry", out var ce2) && ce2.ValueKind == JsonValueKind.Object)
                        {
                            TryGetString(ce2, "licenseExpression", out var e4);
                            TryGetString(ce2, "licenseUrl", out var u4);
                            if (e4 is not null || u4 is not null)
                            {
                                return (e4, u4);
                            }
                        }
                    }
                }
            }
        }

        TryGetString(root, "licenseUrl", out var onlyUrl);
        return (null, onlyUrl);
    }

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
        if (string.IsNullOrWhiteSpace(licenseUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(licenseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
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
