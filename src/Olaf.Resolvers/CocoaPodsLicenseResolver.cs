using System.Net;
using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class CocoaPodsLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public CocoaPodsLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("cocoapods", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a cocoapods pod.");
        }

        try
        {
            var url = $"https://trunk.cocoapods.org/api/v1/pods/{Uri.EscapeDataString(dependency.Name.Trim())}";
            using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: cocoapods pod '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: trunk.cocoapods.org returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rawLicense = ParseTrunkJson(body);

            var spdx = SpdxMapper.Normalize(rawLicense);

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = SpdxLicenseTexts.GetText(spdx);
            var source = $"https://cocoapods.org/pods/{dependency.Name.Trim()}";
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: trunk.cocoapods.org request timed out: {ex.Message}");
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

    internal static string? ParseTrunkJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Trunk/CDN payloads carry the license as a string ("MIT"), as an
            // object ({"type": "MIT"}), or as an array of strings.
            if (root.TryGetProperty("license", out var license))
            {
                switch (license.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = license.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            return s.Trim();
                        }

                        break;
                    case JsonValueKind.Object:
                        foreach (var key in new[] { "type", "Type", "identifier", "spdx" })
                        {
                            if (license.TryGetProperty(key, out var prop)
                                && prop.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(prop.GetString()))
                            {
                                return prop.GetString()!.Trim();
                            }
                        }

                        break;
                    case JsonValueKind.Array:
                        foreach (var item in license.EnumerateArray())
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

            if (root.TryGetProperty("licenses", out var licenses)
                && licenses.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in licenses.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        return item.GetString()!.Trim();
                    }
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
