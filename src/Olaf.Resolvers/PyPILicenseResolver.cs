using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class PyPILicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public PyPILicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("pypi", StringComparison.OrdinalIgnoreCase)
            && !dependency.Ecosystem.Equals("pip", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a pypi package.");
        }

        try
        {
            var url = $"https://pypi.org/pypi/{Uri.EscapeDataString(dependency.Name)}/{Uri.EscapeDataString(dependency.Version)}/json";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: PyPI package '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: PyPI returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (rawLicense, classifiers, licenseExpression) = ParsePyPIJson(body);

            var spdx = SpdxMapper.Normalize(licenseExpression)
                ?? SpdxMapper.Normalize(rawLicense);

            if (spdx is null && classifiers is not null)
            {
                foreach (var classifier in classifiers)
                {
                    spdx = SpdxMapper.FromClassifier(classifier);
                    if (spdx is not null)
                    {
                        break;
                    }
                }
            }

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = SpdxLicenseTexts.GetText(spdx);
            var source = $"https://pypi.org/project/{dependency.Name}/{dependency.Version}/";
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
        }
        catch (OperationCanceledException)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "timeout: PyPI request timed out.");
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

    private static (string? License, List<string>? Classifiers, string? Expression) ParsePyPIJson(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        string? license = null;
        if (info.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.String)
        {
            license = lic.GetString();
        }

        string? expression = null;
        if (info.TryGetProperty("license_expression", out var expr) && expr.ValueKind == JsonValueKind.String)
        {
            expression = expr.GetString();
        }

        List<string>? classifiers = null;
        if (info.TryGetProperty("classifiers", out var cls) && cls.ValueKind == JsonValueKind.Array)
        {
            classifiers = new List<string>();
            foreach (var item in cls.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        classifiers.Add(s);
                    }
                }
            }
        }

        return (license, classifiers, expression);
    }
}
