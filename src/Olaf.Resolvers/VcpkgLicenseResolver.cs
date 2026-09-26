using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class VcpkgLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public VcpkgLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("vcpkg", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a vcpkg port.");
        }

        try
        {
            // Embedded license field: each port ships a vcpkg.json carrying a
            // top-level "license" (string or array of strings).
            var url = $"https://raw.githubusercontent.com/microsoft/vcpkg/master/ports/{Uri.EscapeDataString(dependency.Name.Trim())}/vcpkg.json";
            using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: vcpkg port '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: vcpkg registry returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rawLicense = ParseVcpkgPortJson(body);

            var spdx = SpdxMapper.Normalize(rawLicense);

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = SpdxLicenseTexts.GetText(spdx);
            var source = $"https://vcpkg.io/en/packages?q={Uri.EscapeDataString(dependency.Name.Trim())}";
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: vcpkg registry request timed out: {ex.Message}");
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

    internal static string? ParseVcpkgPortJson(string body)
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

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
