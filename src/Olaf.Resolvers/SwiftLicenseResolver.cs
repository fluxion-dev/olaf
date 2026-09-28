using System.Net;
using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class SwiftLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public SwiftLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        // api.github.com rejects requests without a User-Agent (403).
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "olaf-license-scanner");
        }
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("swift", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a swift package.");
        }

        var (owner, repo) = SplitOwnerRepo(dependency.Name);
        if (owner is null || repo is null)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: swift package name must be 'owner/repo' derived from its source URL.");
        }

        try
        {
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/license";
            using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: swift package '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: GitHub returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rawLicense = ParseGitHubLicenseJson(body);

            var spdx = SpdxMapper.Normalize(rawLicense)
                ?? SpdxMapper.FromLicenseUrl(rawLicense);

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = SpdxLicenseTexts.GetText(spdx);
            var source = $"https://github.com/{owner}/{repo}";
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: GitHub request timed out: {ex.Message}");
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

    internal static (string? Owner, string? Repo) SplitOwnerRepo(string name)
    {
        var parts = name.Trim().Split('/', 2);
        if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
        {
            return (null, null);
        }

        return (parts[0].Trim(), parts[1].Trim());
    }

    internal static string? ParseGitHubLicenseJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("license", out var license)
                && license.ValueKind == JsonValueKind.Object
                && license.TryGetProperty("spdx_id", out var spdx)
                && spdx.ValueKind == JsonValueKind.String)
            {
                var s = spdx.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s.Trim();
                }
            }

            foreach (var key in new[] { "spdx_id", "spdxId", "license" })
            {
                if (root.TryGetProperty(key, out var prop)
                    && prop.ValueKind == JsonValueKind.String)
                {
                    var s = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return s.Trim();
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
