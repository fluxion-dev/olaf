using System.Net;
using System.Text;
using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

// Issue #166: npm semver ranges (plus dist-tags + npm:alias) must resolve via
// one packument GET + one version-doc GET instead of interpolating the raw
// range into the version-doc URL (registry 405). All HTTP is stubbed inline
// (packument JSON built by PackumentJson — no Fixtures files, no live
// network) following ResolverTests.cs NpmResolverTests convention.
// `new Dependency(...)` follows the same convention (IsTransitive:false, so
// Direct is true per src/Olaf.Core/Dependency.cs:9); no formatter display is
// asserted here.
// Plan rows (3+2+1+2+1+1+2+1+1 = 14 Facts, no Theory expansion):
// caret x3, star/empty x2, latest x1, npm:alias x2, unparseable x1,
// unsatisfiable x1, hyphen+tilde x2, || x1, prerelease x1.
// Cross-cutting asserts (folded into the 14, not separate Facts): fidelity
// (declared range kept + concrete version in reason/source), per-instance
// CallCount pins (1 packument + 0/1 version-doc: NpmLicenseResolver owns a
// per-instance packument L1), no-%5E-in-URL, never-registry-error,
// offline CallCount==0, cancellation propagation, cache Store/TryGet
// round-trip. Multi-arm Facts carry their tail count.
public sealed class NpmRangeResolverTests
{
    private static string PackumentJson(string[] versions, string latest)
    {
        var sb = new StringBuilder("{\"versions\":{");
        for (var i = 0; i < versions.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('"').Append(versions[i]).Append("\":{}");
        }

        sb.Append("},\"dist-tags\":{\"latest\":\"").Append(latest).Append("\"}}");
        return sb.ToString();
    }

    // Routes bare /{name} to the packument and /{name}/{version} to a minimal
    // MIT version-doc. Assert-then-slice: the "/{name}/" marker presence
    // decides the branch (never a bare Substring).
    private static StubHttpMessageHandler RangeHandler(string lookupName, string[] versions, string latest)
    {
        var packument = PackumentJson(versions, latest);
        return new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            var marker = "/" + lookupName + "/";
            Assert.True(url.Contains("registry.npmjs.org", StringComparison.Ordinal));
            if (url.Contains(marker, StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json(new
                {
                    name = lookupName,
                    version = "0.0.0",
                    license = "MIT",
                });
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(packument, Encoding.UTF8, "application/json"),
            };
        });
    }

    private static void AssertNoEncodedRangeArtifacts(StubHttpMessageHandler handler)
    {
        Assert.NotEmpty(handler.RequestedUrls);
        foreach (var url in handler.RequestedUrls)
        {
            var text = url?.ToString() ?? string.Empty;
            Assert.DoesNotContain("%5E", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("^", text, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", text, StringComparison.Ordinal);
        }
    }

    private static void AssertRangeFidelity(ResolvedLicense result, string declaredVersion, string rangeNote, string resolvedVersion)
    {
        Assert.Equal(declaredVersion, result.Dependency.Version);
        Assert.NotNull(result.Reason);
        Assert.Contains(rangeNote, result.Reason, StringComparison.Ordinal);
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("/v/" + resolvedVersion, result.SourceUrl, StringComparison.Ordinal);
    }

    // Row 1a/3 — caret resolves to the max satisfying version.
    [Fact]
    public async Task Should_ResolveCaretToMaxSatisfying()
    {
        const string name = "olaf-range-caret-166";
        const string range = "^7.12.10";
        var handler = RangeHandler(name, new[] { "7.0.0", "7.4.0", "7.12.10", "7.13.0", "8.0.0" }, "8.0.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", name, range, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        AssertRangeFidelity(result, range, "(range '^7.12.10' resolved to 7.13.0)", "7.13.0");
        Assert.Equal(2, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);
    }

    // Row 1b/3 — ^0.x special-case: ^0.2.3 stays below 0.3.0.
    [Fact]
    public async Task Should_ResolveCaretZeroMinorSpecialCase()
    {
        const string name = "olaf-range-caret0-166";
        const string range = "^0.2.3";
        var handler = RangeHandler(name, new[] { "0.2.3", "0.2.9", "0.3.0" }, "0.3.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", name, range, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        AssertRangeFidelity(result, range, "(range '^0.2.3' resolved to 0.2.9)", "0.2.9");
        Assert.Equal(2, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);
    }

    // Row 1c/3 — ^0.0.x special-case (^0.0.3 := >=0.0.3 <0.0.4, so 0.0.4 is
    // excluded) plus surrounding-whitespace trimming.
    [Fact]
    public async Task Should_ResolveCaretZeroPatchSpecialCase_WithWhitespace()
    {
        const string name = "olaf-range-caret00-166";
        const string range = "  ^0.0.3  ";
        var handler = RangeHandler(name, new[] { "0.0.3", "0.0.4", "0.1.0" }, "0.1.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", name, range, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        AssertRangeFidelity(result, range, "resolved to 0.0.3", "0.0.3");
        Assert.Contains("^0.0.3", result.Reason, StringComparison.Ordinal);
        Assert.Equal(2, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);
    }

    // Row 2a/2 — star resolves to the max registry version.
    [Fact]
    public async Task Should_ResolveStarToMaxVersion()
    {
        const string name = "olaf-range-star-166";
        const string range = "*";
        var handler = RangeHandler(name, new[] { "1.0.0", "2.0.0", "3.1.4" }, "3.1.4");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", name, range, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        AssertRangeFidelity(result, range, "(range '*' resolved to 3.1.4)", "3.1.4");
        Assert.Equal(2, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);
    }

    // Row 2b/2 — empty version means "*" (max version). Tail arm 2/2 (not
    // counted): an offline range-dep stays zero-HTTP with offline-cache-miss.
    [Fact]
    public async Task Should_ResolveEmptyToMaxVersion()
    {
        const string name = "olaf-range-empty-166";
        var handler = RangeHandler(name, new[] { "0.9.0", "1.0.0" }, "1.0.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", name, string.Empty, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        AssertRangeFidelity(result, string.Empty, "resolved to 1.0.0", "1.0.0");
        Assert.Equal(2, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);

        var offHandler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException($"offline: unexpected HTTP to {req.RequestUri}"));
        using var offHttp = ResolverTestHelpers.CreateClient(offHandler);
        var offline = new CachingLicenseResolver(offHttp, offline: true);
        var offResult = await offline.ResolveAsync(new Dependency("npm", "olaf-range-offline-166", "^1.0.0", false));

        Assert.Equal("Unknown", offResult.Status);
        Assert.NotNull(offResult.Reason);
        Assert.StartsWith("offline-cache-miss", offResult.Reason, StringComparison.Ordinal);
        Assert.Equal(0, offHandler.CallCount);
    }

    // Row 3/1 — a "latest" dist-tag resolves through the packument dist-tags
    // map (2.0.0 wins even though 3.0.0 is the max version). Tail arm 2/2
    // (not counted): the range-resolved record round-trips Store/TryGet.
    [Fact]
    public async Task Should_ResolveLatestDistTag()
    {
        const string name = "olaf-range-latest-166";
        const string range = "latest";
        var handler = RangeHandler(name, new[] { "1.0.0", "2.0.0", "3.0.0" }, "2.0.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", name, range, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        AssertRangeFidelity(result, range, "(range 'latest' resolved to 2.0.0)", "2.0.0");
        Assert.Equal(2, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);

        var dir = Path.Combine(Path.GetTempPath(), "olaf-range-cache-166-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cache = new DiskLicenseCache(Path.Combine(dir, "cache.json"));
            cache.Store(result);
            Assert.True(cache.TryGet(dep, TimeSpan.FromDays(30), out var hit));
            Assert.NotNull(hit);
            Assert.Equal("MIT", hit.SpdxId);
            Assert.Equal(range, hit.Dependency.Version);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    // Row 4a/2 — npm:alias resolves against the TARGET name+range while every
    // attribution (Dependency, reason-declared part) stays on the alias.
    [Fact]
    public async Task Should_ResolveNpmAliasTargetRange()
    {
        const string alias = "olaf-alias-166";
        const string target = "real-target-166";
        const string declared = "npm:real-target-166@^1.2.0";
        var handler = RangeHandler(target, new[] { "1.2.0", "1.9.9", "2.0.0" }, "2.0.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", alias, declared, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(alias, result.Dependency.Name);
        AssertRangeFidelity(result, declared, "(range '^1.2.0' resolved to 1.9.9)", "1.9.9");
        Assert.NotNull(result.SourceUrl);
        Assert.Contains(target, result.SourceUrl, StringComparison.Ordinal);
        Assert.Equal(2, handler.CallCount);
        Assert.NotEmpty(handler.RequestedUrls);
        foreach (var url in handler.RequestedUrls)
        {
            var text = url?.ToString() ?? string.Empty;
            Assert.Contains(target, text, StringComparison.Ordinal);
            Assert.DoesNotContain(alias, text, StringComparison.Ordinal);
            Assert.DoesNotContain("npm:", text, StringComparison.Ordinal);
        }

        AssertNoEncodedRangeArtifacts(handler);
    }

    // Row 4b/2 — bare "npm:pkg" (no '@') defaults to range "*".
    [Fact]
    public async Task Should_ResolveNpmAliasBareTarget()
    {
        const string alias = "olaf-alias-bare-166";
        const string target = "bare-target-166";
        const string declared = "npm:bare-target-166";
        var handler = RangeHandler(target, new[] { "0.1.0", "0.2.0" }, "0.2.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", alias, declared, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(alias, result.Dependency.Name);
        AssertRangeFidelity(result, declared, "(range '*' resolved to 0.2.0)", "0.2.0");
        Assert.Equal(2, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);
    }

    // Row 5/1 — unparseable specs fall back to the latest dist-tag (never
    // not-found, never registry-error).
    [Fact]
    public async Task Should_FallbackToLatest_When_Unparseable()
    {
        const string name = "olaf-range-garbage-166";
        const string range = "not-a-real-range!!!";
        var handler = RangeHandler(name, new[] { "4.0.0", "5.0.0" }, "5.0.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", name, range, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        AssertRangeFidelity(result, range, "(range 'not-a-real-range!!!' resolved to 5.0.0)", "5.0.0");
        Assert.Equal(2, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);
    }

    // Row 6/1 — a well-formed range with no satisfying version is not-found
    // Unknown (never registry-error, never a 405 echo). Tail arm 2/2 (not
    // counted): caller cancellation still propagates as
    // OperationCanceledException on the range path.
    [Fact]
    public async Task Should_ReturnNotFound_When_Unsatisfiable()
    {
        const string name = "olaf-range-nosat-166";
        const string range = ">=999.0.0 <1000.0.0";
        var handler = RangeHandler(name, new[] { "1.0.0" }, "1.0.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", name, range, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("has no registry version satisfying", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("registry-error", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("405", result.Reason, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);

        var cancelHandler = new StubHttpMessageHandler((req, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return StubHttpMessageHandler.Json(new { hello = "world" });
        });
        using var cancelHttp = ResolverTestHelpers.CreateClient(cancelHandler);
        var cancelResolver = new NpmLicenseResolver(cancelHttp);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelResolver.ResolveAsync(dep, new CancellationToken(canceled: true)));
    }

    // Row 7a/2 — hyphen range with a partial upper bound ("- 2.3" is
    // exclusive below 2.4.0).
    [Fact]
    public async Task Should_ResolveHyphenRange_PartialUpper()
    {
        const string name = "olaf-range-hyphen-166";
        const string range = "1.2.3 - 2.3";
        var handler = RangeHandler(name, new[] { "1.2.3", "2.3.0", "2.3.9", "2.4.0" }, "2.4.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", name, range, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        AssertRangeFidelity(result, range, "(range '1.2.3 - 2.3' resolved to 2.3.9)", "2.3.9");
        Assert.Equal(2, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);
    }

    // Row 7b/2 — tilde range with surrounding whitespace.
    [Fact]
    public async Task Should_ResolveTildeRange_WithWhitespace()
    {
        const string name = "olaf-range-tilde-166";
        const string range = "  ~1.2.3  ";
        var handler = RangeHandler(name, new[] { "1.2.3", "1.2.9", "1.3.0" }, "1.3.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", name, range, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        AssertRangeFidelity(result, range, "resolved to 1.2.9", "1.2.9");
        Assert.Contains("~1.2.3", result.Reason, StringComparison.Ordinal);
        Assert.Equal(2, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);
    }

    // Row 8/1 — || unions pick the max across arms; v-prefix inside a
    // comparator pair is accepted.
    [Fact]
    public async Task Should_ResolveUnionRange_WithVPrefix()
    {
        const string name = "olaf-range-union-166";
        const string range = ">=v1.0.0 <1.5.0 || >=2.0.0 <2.5.0";
        var handler = RangeHandler(name, new[] { "1.4.0", "1.9.0", "2.3.0", "3.0.0" }, "3.0.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", name, range, false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        AssertRangeFidelity(result, range, "(range '>=v1.0.0 <1.5.0 || >=2.0.0 <2.5.0' resolved to 2.3.0)", "2.3.0");
        Assert.Equal(2, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);
    }

    // Row 9/1 — node prerelease rule: a range without a prerelease comparator
    // never matches a prerelease version. Tail arm 2/2 (not counted): opting
    // into the prerelease line matches same-tuple prereleases — and reuses
    // the per-instance packument L1 (CallCount 2 -> 3, not 4).
    [Fact]
    public async Task Should_ExcludePrerelease_UnlessOptedIn()
    {
        const string name = "olaf-range-pre-166";
        var handler = RangeHandler(name, new[] { "1.2.3", "1.5.0", "2.0.0-beta.1", "2.0.0-beta.2" }, "1.5.0");
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);

        var excluded = await resolver.ResolveAsync(new Dependency("npm", name, "^1.0.0", false));

        Assert.Equal("Resolved", excluded.Status);
        AssertRangeFidelity(excluded, "^1.0.0", "(range '^1.0.0' resolved to 1.5.0)", "1.5.0");
        Assert.Equal(2, handler.CallCount);

        var optedIn = await resolver.ResolveAsync(new Dependency("npm", name, ">=2.0.0-beta.1 <3.0.0", false));

        Assert.Equal("Resolved", optedIn.Status);
        AssertRangeFidelity(optedIn, ">=2.0.0-beta.1 <3.0.0", "(range '>=2.0.0-beta.1 <3.0.0' resolved to 2.0.0-beta.2)", "2.0.0-beta.2");
        Assert.Equal(3, handler.CallCount);
        AssertNoEncodedRangeArtifacts(handler);
    }
}
