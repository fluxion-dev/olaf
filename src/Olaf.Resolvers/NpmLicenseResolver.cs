using System.Net;
using System.Text.Json;
using Olaf.Core;

namespace Olaf.Resolvers;

public sealed class NpmLicenseResolver : ILicenseResolver
{
    private readonly HttpClient _http;

    // Issue #166 L1 packument cache: one packument GET per resolver-instance per
    // package name. Single-flight choice: plain Dictionary, no SemaphoreSlim.
    // Rationale: ResolveAsync awaits the fetch before any second use, so two
    // fetches on the SAME instance cannot overlap; cross-instance duplicates under
    // SemaphoreSlim(8) fan-out stay possible (CachingLicenseResolver.CreatePrimary
    // news one NpmLicenseResolver per dependency) but each is one idempotent GET.
    // A shared static cache was rejected (stale-packument risk across scans).
    private readonly Dictionary<string, string> _packuments = new(StringComparer.Ordinal);

    public NpmLicenseResolver(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default)
    {
        if (!dependency.Ecosystem.Equals("npm", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "ecosystem-mismatch: not an npm package.");
        }

        try
        {
            // Issue #166: npm:alias declares "npm:<target>@<range>"; resolution runs
            // against the TARGET name + range, attribution stays on the alias dep.
            var lookupName = dependency.Name;
            var rawSpec = dependency.Version;
            if (NpmSemverRange.TrySplitAlias(rawSpec, out var aliasTarget, out var aliasRange))
            {
                lookupName = aliasTarget;
                rawSpec = aliasRange;
            }

            // Exact X.Y.Z[-prerelease] keeps the CURRENT path byte-identical (same
            // URL shape, same errors); only ranges/dist-tags/aliases detour below.
            if (NpmSemverRange.IsExactVersion(rawSpec, out var exact) && exact is not null)
            {
                return await ResolveVersionDocAsync(dependency, lookupName, exact, rangeNote: null, cancellationToken).ConfigureAwait(false);
            }

            return await ResolveRangeAsync(dependency, lookupName, rawSpec, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"timeout: npm request timed out: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"transport-error: {ex.Message}");
        }
        catch (IOException ex)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
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

    // Issue #166 range arm: packument GET /{name} (L1 per instance) -> max-satisfying
    // exact -> existing version-doc path. The raw range is NEVER interpolated into a
    // URL by construction: ResolveVersionDocAsync only ever receives validated-exact.
    private async Task<ResolvedLicense> ResolveRangeAsync(
        Dependency dependency,
        string lookupName,
        string rawSpec,
        CancellationToken cancellationToken)
    {
        if (!_packuments.TryGetValue(lookupName, out var packumentBody))
        {
            var packumentUrl = $"https://registry.npmjs.org/{Uri.EscapeDataString(lookupName)}";
            using var packumentResponse = await ResolverHttpRetry.GetAsync(_http, packumentUrl, cancellationToken).ConfigureAwait(false);
            if (packumentResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: npm package '{dependency.Name} {dependency.Version}' not found.");
            }

            if (!packumentResponse.IsSuccessStatusCode)
            {
                return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: npm returned {(int)packumentResponse.StatusCode}.");
            }

            packumentBody = await packumentResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _packuments[lookupName] = packumentBody;
        }

        if (!NpmSemverRange.TryParsePackument(packumentBody, out var versionKeys, out var distTags))
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "parse-error: npm packument returned unusable JSON.");
        }

        var spec = rawSpec.Trim();
        string? resolved = null;
        var supported = NpmSemverRange.IsSupportedRange(spec);
        if (distTags.TryGetValue(spec, out var tagged)
            && NpmSemverRange.IsExactVersion(tagged, out var tagExact)
            && tagExact is not null)
        {
            resolved = tagExact;
        }
        else if (supported
            && NpmSemverRange.TryResolve(versionKeys, spec, out var best)
            && best is not null
            && NpmSemverRange.IsExactVersion(best, out var bestExact)
            && bestExact is not null)
        {
            resolved = bestExact;
        }
        else if (!supported
            && distTags.TryGetValue("latest", out var latest)
            && NpmSemverRange.IsExactVersion(latest, out var latestExact)
            && latestExact is not null)
        {
            // Unparseable spec (incl. unknown dist-tags) falls back to latest.
            resolved = latestExact;
        }

        if (resolved is null)
        {
            // Unsatisfiable range (or no usable latest): not-found Unknown — never
            // registry-error, and never cached as such (caller owns caching).
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: npm package '{dependency.Name} {dependency.Version}' has no registry version satisfying range '{rawSpec}'.");
        }

        // Fidelity contract: Dependency keeps the DECLARED range; the concrete version
        // rides in the version-doc/source URL and in this reason suffix.
        var rangeNote = $"range '{rawSpec}' resolved to {resolved}";
        return await ResolveVersionDocAsync(dependency, lookupName, resolved, rangeNote, cancellationToken).ConfigureAwait(false);
    }

    // The single version-doc lookup. Exact path callers pass the declared name/version
    // (byte-identical URL/errors to the pre-#166 code); range callers pass the resolved
    // target name + validated-exact version plus a reason suffix naming both.
    private async Task<ResolvedLicense> ResolveVersionDocAsync(
        Dependency dependency,
        string lookupName,
        string exactVersion,
        string? rangeNote,
        CancellationToken cancellationToken)
    {
        var url = $"https://registry.npmjs.org/{Uri.EscapeDataString(lookupName)}/{Uri.EscapeDataString(exactVersion)}";
        using var response = await ResolverHttpRetry.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"not-found: npm package '{lookupName} {exactVersion}' not found.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", $"registry-error: npm returned {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var (rawLicense, licenseUrl) = ParseNpmJson(body);
        var spdx = SpdxMapper.Normalize(rawLicense)
            ?? SpdxMapper.FromLicenseUrl(licenseUrl);

        if (spdx is null)
        {
            return new ResolvedLicense(dependency, null, null, null, "Unknown", "license-unknown: registry returned no usable license.");
        }

        var source = $"https://www.npmjs.com/package/{lookupName}/v/{exactVersion}";
        // Enrichment reads the same already-fetched versioned registry JSON
        // (NO-NEW-HTTP): dist.integrity -> Hashes, author/maintainers ->
        // Supplier, dist.tarball -> DownloadUrl. Never copies SourceUrl.
        var enrichment = ParseNpmEnrichment(body, dependency);
        // License text fills LicenseText ONLY (no id re-resolution):
        // tarball (dist.tarball via Enrichment) > licenseUrl > DB > null.
        var (text, textReason, isPerPackage) = await LicenseTextFetcher.TryFetchLicenseTextWithProvenanceAsync(
            _http, enrichment?.DownloadUrl, licenseUrl, spdx, cancellationToken).ConfigureAwait(false);
        // Issue #72: holders from per-package texts only (tarball/
        // licenseUrl provenance — never DB subset text); metadata-author
        // fallback when zero; bare emails never promoted.
        enrichment = CopyrightScraper.AttachHolders(dependency, enrichment, isPerPackage ? text : null, enrichment?.Supplier);
        var reason = rangeNote is null ? textReason : $"{textReason} ({rangeNote})";
        return new ResolvedLicense(dependency, spdx, text, source, "Resolved", reason, enrichment);
    }

    internal static Enrichment? ParseNpmEnrichment(string body, Dependency dependency)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? integrity = null;
            string? tarball = null;
            if (root.TryGetProperty("dist", out var dist) && dist.ValueKind == JsonValueKind.Object)
            {
                if (EnrichmentHelpers.TryGetString(dist, "integrity", out var integ))
                {
                    integrity = integ;
                }

                if (EnrichmentHelpers.TryGetString(dist, "tarball", out var tb))
                {
                    tarball = tb;
                }
            }

            string? supplier = null;
            if (root.TryGetProperty("author", out var author))
            {
                supplier = ReadPersonName(author);
            }

            if (supplier is null
                && root.TryGetProperty("maintainers", out var maintainers)
                && maintainers.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in maintainers.EnumerateArray())
                {
                    supplier = ReadPersonName(m);
                    if (supplier is not null)
                    {
                        break;
                    }
                }
            }

            // integrity is "algo-base64digest" (e.g. "sha512-..."); Hashes are "algo:value".
            string? hash = null;
            if (!string.IsNullOrWhiteSpace(integrity))
            {
                var dash = integrity!.IndexOf('-');
                if (dash > 0 && dash < integrity.Length - 1)
                {
                    hash = integrity.Substring(0, dash).Trim().ToLowerInvariant()
                        + ":" + integrity.Substring(dash + 1).Trim();
                }
            }

            return EnrichmentHelpers.Create(
                dependency,
                EnrichmentHelpers.HashList(hash),
                supplier,
                tarball);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadPersonName(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString()!.Trim();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (EnrichmentHelpers.TryGetString(element, "name", out var name))
            {
                return name!.Trim();
            }

            if (EnrichmentHelpers.TryGetString(element, "email", out var email))
            {
                return email!.Trim();
            }
        }

        return null;
    }

    private static (string? License, string? LicenseUrl) ParseNpmJson(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        string? licenseUrl = null;
        if (root.TryGetProperty("licenseTextUrl", out var ltu) && ltu.ValueKind == JsonValueKind.String)
        {
            licenseUrl = ltu.GetString();
        }

        if (root.TryGetProperty("licenseUrl", out var lu) && lu.ValueKind == JsonValueKind.String)
        {
            licenseUrl ??= lu.GetString();
        }

        if (root.TryGetProperty("license", out var lic))
        {
            switch (lic.ValueKind)
            {
                case JsonValueKind.String:
                    return (lic.GetString(), licenseUrl);
                case JsonValueKind.Object:
                    if (lic.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        if (lic.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        {
                            licenseUrl ??= u.GetString();
                        }

                        return (t.GetString(), licenseUrl);
                    }

                    if (lic.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        return (n.GetString(), licenseUrl);
                    }

                    break;
                case JsonValueKind.Array:
                    var parts = new List<string>();
                    foreach (var item in lic.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var s = item.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                parts.Add(s.Trim());
                            }
                        }
                        else if (item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("type", out var at)
                            && at.ValueKind == JsonValueKind.String)
                        {
                            var s = at.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                parts.Add(s.Trim());
                            }
                        }
                    }

                    if (parts.Count > 0)
                    {
                        return (string.Join(" OR ", parts), licenseUrl);
                    }

                    break;
            }
        }

        if (root.TryGetProperty("licenses", out var lics) && lics.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in lics.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var t)
                    && t.ValueKind == JsonValueKind.String)
                {
                    var s = t.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        parts.Add(s.Trim());
                    }
                }
            }

            if (parts.Count > 0)
            {
                return (string.Join(" OR ", parts), licenseUrl);
            }
        }

        return (null, licenseUrl);
    }
}
