using System.Net;
using System.Security.Cryptography;
using System.Text;
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

            if (spdx is not null)
            {
                var text = SpdxLicenseTexts.GetText(spdx);
                var source = $"https://cocoapods.org/pods/{dependency.Name.Trim()}";
                return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
            }

            // Trunk 200 but no usable license: CDN podspec fallback (issue #84).
            // Version rule: requested Version verbatim, unless null/empty/* ->
            // latest = versions[].name last element from the already-fetched trunk body.
            var podName = dependency.Name.Trim();
            var requested = dependency.Version?.Trim();
            var version = string.IsNullOrWhiteSpace(requested) || requested == "*"
                ? ParseLatestVersion(body)
                : requested;
            if (string.IsNullOrWhiteSpace(version))
            {
                return LicenseUnknown(dependency);
            }

            var cdnUrl = BuildCdnUrl(podName, version!);
            try
            {
                using var cdnResponse = await ResolverHttpRetry.GetAsync(_http, cdnUrl, cancellationToken).ConfigureAwait(false);
                if (!cdnResponse.IsSuccessStatusCode)
                {
                    return LicenseUnknown(dependency);
                }

                var cdnBody = await cdnResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var cdnSpdx = SpdxMapper.Normalize(ParseTrunkJson(cdnBody));
                if (cdnSpdx is null)
                {
                    return LicenseUnknown(dependency);
                }

                var cdnText = SpdxLicenseTexts.GetText(cdnSpdx);
                var cdnSource = $"https://cocoapods.org/pods/{podName}";
                return new ResolvedLicense(dependency, cdnSpdx, cdnText, cdnSource, "Resolved", null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                return LicenseUnknown(dependency);
            }
            catch (IOException)
            {
                // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
                return LicenseUnknown(dependency);
            }
            catch (UnauthorizedAccessException)
            {
                return LicenseUnknown(dependency);
            }
            catch (Exception)
            {
                return LicenseUnknown(dependency);
            }
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
        catch (IOException ex)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"resolver-error: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"resolver-error: {ex.Message}");
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

    private static ResolvedLicense LicenseUnknown(Dependency dependency) =>
        new(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");

    internal static string BuildCdnUrl(string podName, string version)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(podName));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Join('/', "https://cdn.cocoapods.org/Specs", hex.Substring(0, 1), hex.Substring(1, 1), hex.Substring(2, 1),
            Uri.EscapeDataString(podName), Uri.EscapeDataString(version),
            Uri.EscapeDataString(podName) + ".podspec.json");
    }

    internal static string? ParseLatestVersion(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Trunk versions shape: {"versions": [{"name": "5.8.1"}, ...]};
            // last element = newest (no extra GET).
            if (root.TryGetProperty("versions", out var versions)
                && versions.ValueKind == JsonValueKind.Array)
            {
                string? latest = null;
                foreach (var item in versions.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("name", out var name)
                        && name.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(name.GetString()))
                    {
                        latest = name.GetString()!.Trim();
                    }
                }

                return latest;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
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
