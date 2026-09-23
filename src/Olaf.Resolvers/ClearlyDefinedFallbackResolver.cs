using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class ClearlyDefinedFallbackResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public ClearlyDefinedFallbackResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        try
        {
            var primary = CreatePrimary(dependency);
            if (primary is not null)
            {
                var first = await primary.ResolveAsync(dependency, cancellationToken).ConfigureAwait(false);
                if (first.Status.Equals("Resolved", StringComparison.OrdinalIgnoreCase) && first.SpdxId is not null)
                {
                    return first;
                }
            }

            return await QueryClearlyDefinedAsync(dependency, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "timeout: fallback request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"transport-error: {ex.Message}");
        }
    }

    private ILicenseResolver? CreatePrimary(Dependency dependency)
    {
        if (dependency.Ecosystem.Equals("nuget", StringComparison.OrdinalIgnoreCase))
        {
            return new NuGetLicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("npm", StringComparison.OrdinalIgnoreCase))
        {
            return new NpmLicenseResolver(_http);
        }

        if (dependency.Ecosystem.Equals("pypi", StringComparison.OrdinalIgnoreCase)
            || dependency.Ecosystem.Equals("pip", StringComparison.OrdinalIgnoreCase))
        {
            return new PyPILicenseResolver(_http);
        }

        return null;
    }

    private async Task<ResolvedLicense> QueryClearlyDefinedAsync(Dependency dependency, CancellationToken ct)
    {
        var definitionUrl = BuildDefinitionUrl(dependency);
        if (definitionUrl is null)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: unsupported ecosystem for fallback.");
        }

        try
        {
            using var response = await _http.GetAsync(definitionUrl, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: no license for '{dependency.Name} {dependency.Version}' in registry or ClearlyDefined.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: ClearlyDefined returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var declared = ParseDeclared(body);
            var spdx = SpdxMapper.Normalize(declared);
            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: ClearlyDefined returned no usable license.");
            }

            return new ResolvedLicense(dependency, spdx, SpdxLicenseTexts.GetText(spdx), definitionUrl, "Resolved", null);
        }
        catch (OperationCanceledException)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "timeout: ClearlyDefined request timed out.");
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

    private static string? BuildDefinitionUrl(Dependency dependency)
    {
        var name = Uri.EscapeDataString(dependency.Name);
        var version = Uri.EscapeDataString(dependency.Version);
        var eco = dependency.Ecosystem.ToLowerInvariant();
        return eco switch
        {
            "npm" => $"https://api.clearlydefined.io/definitions/npm/npmjs/-/{name}/{version}",
            "nuget" => $"https://api.clearlydefined.io/definitions/nuget/nugetio/-/{name}/{version}",
            "pypi" or "pip" => $"https://api.clearlydefined.io/definitions/pypi/pypi/-/{name}/{version}",
            _ => null,
        };
    }

    private static string? ParseDeclared(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("licensed", out var licensed) && licensed.ValueKind == JsonValueKind.Object)
        {
            if (licensed.TryGetProperty("declared", out var declared) && declared.ValueKind == JsonValueKind.String)
            {
                return declared.GetString();
            }
        }

        return null;
    }
}
