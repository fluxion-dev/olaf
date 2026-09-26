using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class ConanLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public ConanLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("conan", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a conan package.");
        }

        try
        {
            var name = dependency.Name.Trim();
            var version = dependency.Version.Trim();
            var url = string.Equals(version, "*", StringComparison.Ordinal)
                ? $"https://conan.io/center/api/recipes/{Uri.EscapeDataString(name)}"
                : $"https://conan.io/center/api/recipes/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}";
            using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: conan package '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: conan.io returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rawLicense = ParseConanCenterJson(body);

            var spdx = SpdxMapper.Normalize(rawLicense);

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = SpdxLicenseTexts.GetText(spdx);
            var source = $"https://conan.io/center/recipes/{name}";
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: conan.io request timed out: {ex.Message}");
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

    internal static string? ParseConanCenterJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

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
                        foreach (var key in new[] { "type", "identifier", "spdx", "name" })
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
