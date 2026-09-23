using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class NpmLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public NpmLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("npm", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not an npm package.");
        }

        try
        {
            var url = $"https://registry.npmjs.org/{Uri.EscapeDataString(dependency.Name)}/{Uri.EscapeDataString(dependency.Version)}";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: npm package '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: npm returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (rawLicense, licenseUrl) = ParseNpmJson(body);

            var spdx = SpdxMapper.Normalize(rawLicense)
                ?? SpdxMapper.FromLicenseUrl(licenseUrl);

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = await TryFetchLicenseTextAsync(licenseUrl, cancellationToken).ConfigureAwait(false)
                ?? SpdxLicenseTexts.GetText(spdx);

            var source = $"https://www.npmjs.com/package/{dependency.Name}/v/{dependency.Version}";
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
        }
        catch (OperationCanceledException)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "timeout: npm request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"transport-error: {ex.Message}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"parse-error: {ex.Message}");
        }
    }

    private static (string? License, string? LicenseUrl) ParseNpmJson(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        string? licenseUrl = null;
        if (root.TryGetProperty("licenseTextUrl", out var ltu) && ltu.ValueKind == JsonValueKind.String)
        {
            licenseUrl = ltu.GetString();
        }

        if (root.TryGetProperty("licenseUrl", out var lu) && lu.ValueKind == JsonValueKind.String)
        {
            licenseUrl ??= lu.GetString();
        }

        if (root.TryGetProperty("license", out var lic))
        {
            switch (lic.ValueKind)
            {
                case JsonValueKind.String:
                    return (lic.GetString(), licenseUrl);
                case JsonValueKind.Object:
                    if (lic.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        if (lic.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        {
                            licenseUrl ??= u.GetString();
                        }

                        return (t.GetString(), licenseUrl);
                    }

                    if (lic.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        return (n.GetString(), licenseUrl);
                    }

                    break;
                case JsonValueKind.Array:
                    var parts = new List<string>();
                    foreach (var item in lic.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var s = item.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                parts.Add(s.Trim());
                            }
                        }
                        else if (item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("type", out var at)
                            && at.ValueKind == JsonValueKind.String)
                        {
                            var s = at.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                parts.Add(s.Trim());
                            }
                        }
                    }

                    if (parts.Count > 0)
                    {
                        return (string.Join(" OR ", parts), licenseUrl);
                    }

                    break;
            }
        }

        if (root.TryGetProperty("licenses", out var lics) && lics.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in lics.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var t)
                    && t.ValueKind == JsonValueKind.String)
                {
                    var s = t.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        parts.Add(s.Trim());
                    }
                }
            }

            if (parts.Count > 0)
            {
                return (string.Join(" OR ", parts), licenseUrl);
            }
        }

        return (null, licenseUrl);
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
