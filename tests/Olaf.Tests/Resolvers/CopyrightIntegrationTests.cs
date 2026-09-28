using System.Net;
using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Issue #72 R2: resolver hook — scraped-text-first, metadata-author
/// fallback only when text yields zero, bare emails never promoted,
/// DB-subset texts never scraped. Stub handler only, no network; inline
/// strings only, no fixture files.
/// </summary>
public sealed class CopyrightIntegrationTests
{
    private const string TarballUrl = "https://example.com/acme-1.0.0.tgz";

    // Tarball bytes via TarFixtureBuilder (shared hand-rolled ustar helper).

    [Fact]
    public async Task Should_PreferScrapedText_When_TarballTextHasCopyright()
    {
        // Scraped text wins over the registry author ("Registry Author" is
        // the supplier but never the holder here).
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.EndsWith(".tgz", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(TarFixtureBuilder.BuildTgz(
                        "package/LICENSE",
                        "MIT License\nCopyright (c) 2024 Acme Corp\nPermission is hereby granted.")),
                };
            }

            return StubHttpMessageHandler.Json(new
            {
                license = "MIT",
                dist = new { tarball = TarballUrl },
                author = new { name = "Registry Author" },
            });
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);

        var result = await resolver.ResolveAsync(new Dependency("npm", "acme", "1.0.0", false));

        Assert.Equal("MIT", result.SpdxId);
        Assert.NotNull(result.Enrichment);
        Assert.Equal("Registry Author", result.Enrichment.Supplier);
        Assert.Equal(new[] { "Acme Corp" }, result.Enrichment.CopyrightHolders);
    }

    [Fact]
    public async Task Should_FallBackToAuthor_When_TextYieldsZero()
    {
        // No dist tarball and no licenseUrl: the MIT DB text carries no
        // year-anchored holder, so the registry author is the holder.
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                info = new { license = "MIT", author = "Ada Lovelace" },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new PyPILicenseResolver(http);

        var result = await resolver.ResolveAsync(new Dependency("pypi", "analytical", "1.0.0", false));

        Assert.Equal("MIT", result.SpdxId);
        Assert.NotNull(result.Enrichment);
        Assert.Equal("Ada Lovelace", result.Enrichment.Supplier);
        Assert.Equal(new[] { "Ada Lovelace" }, result.Enrichment.CopyrightHolders);
    }

    [Fact]
    public async Task Should_RejectBareEmail_When_NoDisplayName()
    {
        // Supplier keeps the email fallback (issue #70); holders never do.
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                license = "MIT",
                author = new { email = "bot@example.com" },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);

        var result = await resolver.ResolveAsync(new Dependency("npm", "botpkg", "1.0.0", false));

        Assert.Equal("MIT", result.SpdxId);
        Assert.NotNull(result.Enrichment);
        Assert.Equal("bot@example.com", result.Enrichment.Supplier);
        Assert.Null(result.Enrichment.CopyrightHolders);
    }

    [Fact]
    public async Task Should_NeverScrapeDbText_When_OnlyDbTextPresent()
    {
        // DB text is present (non-null LicenseText) yet yields no holders;
        // the provenance overload reports the DB stage as non-per-package.
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { license = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);

        var result = await resolver.ResolveAsync(new Dependency("npm", "bare", "1.0.0", false));

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(SpdxLicenseTexts.GetText("MIT"), result.LicenseText);
        Assert.Null(result.Enrichment);

        var missHandler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var missHttp = ResolverTestHelpers.CreateClient(missHandler);
        var (dbText, dbReason, isPerPackage) = await LicenseTextFetcher.TryFetchLicenseTextWithProvenanceAsync(
            missHttp, tarballUrl: null, licenseUrl: null, "MIT", CancellationToken.None);

        Assert.Equal(SpdxLicenseTexts.GetText("MIT"), dbText);
        Assert.Null(dbReason);
        Assert.False(isPerPackage);
    }
}
