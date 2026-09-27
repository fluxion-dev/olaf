using System.Xml.Linq;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class MavenLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    public MavenLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("maven", StringComparison.OrdinalIgnoreCase)
            && !dependency.Ecosystem.Equals("gradle", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not a maven/gradle artifact.");
        }

        var (groupId, artifactId) = SplitName(dependency.Name);
        if (groupId is null || artifactId is null)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"license-unknown: '{dependency.Name}' is not a group:artifact coordinate.");
        }

        var version = dependency.Version?.Trim();
        if (string.IsNullOrWhiteSpace(version) || version == "*")
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"license-unknown: '{dependency.Name}' has no pinned version.");
        }

        try
        {
            var groupPath = groupId.Replace('.', '/');
            var url = $"https://repo1.maven.org/maven2/{groupPath}/{Uri.EscapeDataString(artifactId)}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(artifactId)}-{Uri.EscapeDataString(version)}.pom";

            // Licenses are often declared on a <parent> POM (e.g. guava -> guava-parent):
            // walk up the parent chain (bounded) until licenses are found.
            for (var depth = 0; depth < 3; depth++)
            {
                string? current;
                var outcome = await FetchPomAsync(url, cancellationToken).ConfigureAwait(false);
                if (outcome.Status == FetchStatus.NotFound)
                {
                    if (depth == 0)
                    {
                        return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: Maven artifact '{dependency.Name} {version}' not found.");
                    }

                    break;
                }

                if (outcome.Status == FetchStatus.RegistryError)
                {
                    return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: Maven Central returned {outcome.StatusCode}.");
                }

                current = outcome.Body;
                var spdx = ParsePomLicenses(current!);
                if (spdx is not null)
                {
                    var text = SpdxLicenseTexts.GetText(spdx);
                    var source = $"https://mvnrepository.com/artifact/{groupId}/{artifactId}/{version}";
                    return new ResolvedLicense(dependency, spdx, text, source, "Resolved", null);
                }

                var parent = ParseParentCoordinates(current!);
                if (parent is null)
                {
                    break;
                }

                var parentPath = parent.Value.GroupId.Replace('.', '/');
                url = $"https://repo1.maven.org/maven2/{parentPath}/{Uri.EscapeDataString(parent.Value.ArtifactId)}/{Uri.EscapeDataString(parent.Value.Version)}/{Uri.EscapeDataString(parent.Value.ArtifactId)}-{Uri.EscapeDataString(parent.Value.Version)}.pom";
            }

            return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: Maven Central request timed out: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"transport-error: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"parse-error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"resolver-error: {ex.Message}");
        }
    }

    internal static (string? GroupId, string? ArtifactId) SplitName(string name)
    {
        if (!MavenCoordinates.TrySplit(name, out var rawGroup, out var rawArtifact))
        {
            return (null, null);
        }

        var groupId = rawGroup!.Trim();
        var artifactId = rawArtifact!.Trim();
        if (groupId.Length == 0 || artifactId.Length == 0 || artifactId.Contains(':'))
        {
            return (null, null);
        }

        return (groupId, artifactId);
    }

    internal static string? ParsePomLicenses(string body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            var doc = XDocument.Parse(body);
            var root = doc.Root;
            if (root is null)
            {
                return null;
            }

            var found = new List<string>();
            foreach (var license in root.Descendants().Where(e => e.Name.LocalName == "license"))
            {
                var name = license.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
                var url = license.Elements().FirstOrDefault(e => e.Name.LocalName == "url")?.Value;

                var spdx = SpdxMapper.Normalize(name)
                    ?? SpdxMapper.FromLicenseUrl(url)
                    ?? SpdxMapper.FromClassifier(name);
                if (spdx is not null && !found.Contains(spdx, StringComparer.Ordinal))
                {
                    found.Add(spdx);
                }
            }

            if (found.Count == 0)
            {
                return null;
            }

            return found.Count == 1 ? found[0] : string.Join(" AND ", found);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    internal static (string GroupId, string ArtifactId, string Version)? ParseParentCoordinates(string body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            var doc = XDocument.Parse(body);
            var root = doc.Root;
            var parent = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "parent");
            if (parent is null)
            {
                return null;
            }

            var groupId = parent.Elements().FirstOrDefault(e => e.Name.LocalName == "groupId")?.Value?.Trim();
            var artifactId = parent.Elements().FirstOrDefault(e => e.Name.LocalName == "artifactId")?.Value?.Trim();
            var version = parent.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(artifactId) || string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            // Property placeholders cannot be resolved without the full model.
            if (version.Contains("${", StringComparison.Ordinal))
            {
                return null;
            }

            return (groupId, artifactId, version);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private enum FetchStatus
    {
        Ok,
        NotFound,
        RegistryError,
    }

    private readonly record struct FetchOutcome(FetchStatus Status, string? Body, int StatusCode);

    private async Task<FetchOutcome> FetchPomAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new FetchOutcome(FetchStatus.NotFound, null, (int)response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new FetchOutcome(FetchStatus.RegistryError, null, (int)response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new FetchOutcome(FetchStatus.Ok, body, (int)response.StatusCode);
    }
}
