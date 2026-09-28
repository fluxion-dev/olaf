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
            return await QueryClearlyDefinedAsync(dependency, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: fallback request timed out: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"transport-error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"resolver-error: {ex.Message}");
        }
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
            using var response = await ResolverHttpRetry.GetAsync(_http, definitionUrl, ct).ConfigureAwait(false);
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

            // Trivial sibling enrichment from the same already-fetched
            // definitions JSON (NO-NEW-HTTP): files[].sha (first sha256) ->
            // Hashes, parties[] supplier-ish -> Supplier; else null.
            var enrichment = ParseClearlyDefinedEnrichment(body, dependency);
            return new ResolvedLicense(dependency, spdx, SpdxLicenseTexts.GetText(spdx), definitionUrl, "Resolved", null, enrichment);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: ClearlyDefined request timed out: {ex.Message}");
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

    private static string? BuildDefinitionUrl(Dependency dependency)
    {
        var version = Uri.EscapeDataString(dependency.Version);
        var eco = dependency.Ecosystem.ToLowerInvariant();
        if (eco == "composer")
        {
            // ClearlyDefined composer coordinates: provider packagist, namespace vendor.
            var parts = dependency.Name.Split('/', 2);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                return null;
            }

            var vendor = Uri.EscapeDataString(parts[0]);
            var pkg = Uri.EscapeDataString(parts[1]);
            return $"https://api.clearlydefined.io/definitions/composer/packagist/{vendor}/{pkg}/{version}";
        }

        var name = Uri.EscapeDataString(dependency.Name);
        return eco switch
        {
            "npm" => $"https://api.clearlydefined.io/definitions/npm/npmjs/-/{name}/{version}",
            "nuget" => $"https://api.clearlydefined.io/definitions/nuget/nugetio/-/{name}/{version}",
            "pypi" or "pip" => $"https://api.clearlydefined.io/definitions/pypi/pypi/-/{name}/{version}",
            "bundler" or "gem" => $"https://api.clearlydefined.io/definitions/gem/rubygems/-/{name}/{version}",
            "vcpkg" => $"https://api.clearlydefined.io/definitions/vcpkg/vcpkgio/-/{name}/{version}",
            "conan" => $"https://api.clearlydefined.io/definitions/conan/conancenter/-/{name}/{version}",
            "go" => $"https://api.clearlydefined.io/definitions/go/github/-/{name}/{version}",
            _ => null,
        };
    }

    internal static Enrichment? ParseClearlyDefinedEnrichment(string body, Dependency dependency)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? hash = null;
            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    // First sha256 wins: direct "sha256"/"sha" fields, else a
                    // nested "hashes" object.
                    if (EnrichmentHelpers.TryGetString(file, "sha256", out var sha256))
                    {
                        hash = "sha256:" + sha256!.Trim();
                        break;
                    }

                    if (EnrichmentHelpers.TryGetString(file, "sha", out var sha))
                    {
                        hash = "sha256:" + sha!.Trim();
                        break;
                    }

                    if (file.TryGetProperty("hashes", out var hashes)
                        && hashes.ValueKind == JsonValueKind.Object
                        && EnrichmentHelpers.TryGetString(hashes, "sha256", out var nested))
                    {
                        hash = "sha256:" + nested!.Trim();
                        break;
                    }
                }
            }

            string? supplier = null;
            foreach (var scope in EnumeratePartiesScopes(root))
            {
                supplier = ReadPartySupplier(scope);
                if (supplier is not null)
                {
                    break;
                }
            }

            return EnrichmentHelpers.Create(
                dependency,
                EnrichmentHelpers.HashList(hash),
                supplier,
                downloadUrl: null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> EnumeratePartiesScopes(JsonElement root)
    {
        if (root.TryGetProperty("parties", out var direct) && direct.ValueKind == JsonValueKind.Array)
        {
            yield return direct;
        }

        if (root.TryGetProperty("licensed", out var licensed) && licensed.ValueKind == JsonValueKind.Object
            && licensed.TryGetProperty("parties", out var nested) && nested.ValueKind == JsonValueKind.Array)
        {
            yield return nested;
        }
    }

    private static string? ReadPartySupplier(JsonElement parties)
    {
        foreach (var party in parties.EnumerateArray())
        {
            if (party.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Supplier-ish: prefer a human name, else url, else email.
            if (EnrichmentHelpers.TryGetString(party, "name", out var name))
            {
                return name!.Trim();
            }

            if (EnrichmentHelpers.TryGetString(party, "url", out var url))
            {
                return url!.Trim();
            }

            if (EnrichmentHelpers.TryGetString(party, "email", out var email))
            {
                return email!.Trim();
            }
        }

        return null;
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
