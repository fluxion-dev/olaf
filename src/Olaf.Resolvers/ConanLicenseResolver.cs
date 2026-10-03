using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class ConanLicenseResolver : ILicenseResolver
{
    // ConanCenter has no usable license API (conan.io/center/api 404s for
    // existing recipes — former primary deleted in #123), and ClearlyDefined
    // serves 200-empty for conan. The recipe index on GitHub carries the
    // license instead: config.yml maps versions to recipe folders, and each
    // folder's conanfile.py declares a `license` attribute. Same shape as
    // VcpkgLicenseResolver (raw.githubusercontent fetches, no API).
    internal const string IndexBase = "https://raw.githubusercontent.com/conan-io/conan-center-index/master/recipes";

    // `license = "MIT"` / `license = 'MIT'` / `license = ("MIT", ...)`:
    // first quoted string wins. The lookbehind keeps `spdx_license = ...`
    // (or any dotted/long attribute) from matching.
    private static readonly Regex LicenseAttributeRegex = new(
        @"(?<![\w.])license\s*=\s*\(?\s*['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    private readonly HttpClient _http;

    public ConanLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("conan", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a conan recipe.");
        }

        var name = dependency.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: empty conan recipe name.");
        }

        var escaped = Uri.EscapeDataString(name);

        try
        {
            var configUrl = $"{IndexBase}/{escaped}/config.yml";
            using var configResponse = await ResolverHttpRetry.GetAsync(_http, configUrl, cancellationToken).ConfigureAwait(false);
            if (configResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: conan recipe '{name} {dependency.Version}' not found.");
            }

            if (!configResponse.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: conan center index returned {(int)configResponse.StatusCode}.");
            }

            var config = await configResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // Exact version hit when the index still lists it; otherwise the
            // latest folder — the license attribute is folder-level and
            // version-independent (vcpkg precedent: version ignored).
            var folder = PickFolder(config, dependency.Version) ?? "all";

            var recipeUrl = $"{IndexBase}/{escaped}/{Uri.EscapeDataString(folder)}/conanfile.py";
            using var recipeResponse = await ResolverHttpRetry.GetAsync(_http, recipeUrl, cancellationToken).ConfigureAwait(false);
            if (recipeResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: conan recipe '{name} {dependency.Version}' not found.");
            }

            if (!recipeResponse.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: conan center index returned {(int)recipeResponse.StatusCode}.");
            }

            var recipe = await recipeResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rawLicense = ParseLicenseAttribute(recipe);

            var spdx = SpdxMapper.Normalize(rawLicense);

            if (spdx is null)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
            }

            var text = SpdxLicenseTexts.GetText(spdx);
            var source = $"https://conan.io/center/recipes/{escaped}";
            return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: conan center index request timed out: {ex.Message}");
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

    internal static string? PickFolder(string configYaml, string? version)
    {
        // Minimal config.yml reader: `versions:` maps quoted versions to
        // `folder:` entries. Comment/blank lines are skipped; order is
        // preserved so index position 0 is the latest version.
        try
        {
            var folders = new List<(string Version, string Folder)>();
            string? pendingVersion = null;
            foreach (var raw in configYaml.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var versionMatch = Regex.Match(line, @"^[""']([^""']+)[""']\s*:\s*$");
                if (versionMatch.Success)
                {
                    pendingVersion = versionMatch.Groups[1].Value.Trim();
                    continue;
                }

                var folderMatch = Regex.Match(line, @"^folder\s*:\s*[""']?([^""'#\s]+)");
                if (folderMatch.Success && !string.IsNullOrWhiteSpace(pendingVersion))
                {
                    folders.Add((pendingVersion, folderMatch.Groups[1].Value.Trim()));
                    pendingVersion = null;
                }
            }

            if (folders.Count == 0)
            {
                return null;
            }

            var wanted = version?.Trim().Trim('"', '\'').Trim();
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                var exact = folders.FirstOrDefault(f => f.Version.Equals(wanted, StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(exact.Folder))
                {
                    return exact.Folder;
                }
            }

            return folders[0].Folder;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static string? ParseLicenseAttribute(string conanfile)
    {
        try
        {
            var match = LicenseAttributeRegex.Match(conanfile);
            if (!match.Success)
            {
                return null;
            }

            var value = match.Groups[1].Value.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
