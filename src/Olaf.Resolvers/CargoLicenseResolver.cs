using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class CargoLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public CargoLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("cargo", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a cargo crate.");
        }

        try
        {
            var url = $"https://crates.io/api/v1/crates/{Uri.EscapeDataString(dependency.Name)}/{Uri.EscapeDataString(dependency.Version)}";
            using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: cargo crate '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: crates.io returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rawLicense = ParseCratesJson(body);

            var spdx = SpdxMapper.Normalize(rawLicense);

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = SpdxLicenseTexts.GetText(spdx);
            var source = $"https://crates.io/crates/{dependency.Name}/{dependency.Version}";
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: crates.io request timed out: {ex.Message}");
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

    internal static string? ParseCratesJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "license", "licenseExpression", "license_expression" })
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

            if (root.TryGetProperty("crate", out var crate) && crate.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "license", "License", "licenseExpression" })
                {
                    if (crate.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            return s;
                        }
                    }
                }
            }

            foreach (var key in new[] { "license", "License", "licenseExpression", "license_expression" })
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
}
