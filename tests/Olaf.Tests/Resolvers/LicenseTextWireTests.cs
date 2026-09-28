using System.IO.Compression;
using System.Net;
using System.Text;
using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Issue #71 B2: resolver-level wiring — npm tarball path fills
/// <c>LicenseText</c> (id stands, no re-resolution), NuGet licenseUrl
/// fallback preserved through the shared fetcher (dedupe parity), and Go
/// module-zip caps leave the happy path unchanged. Each stub serves the
/// registry JSON on the first endpoint plus tarball bytes on the second;
/// no live network, zero new E2E.
/// </summary>
public sealed class LicenseTextWireTests
{
    // Tgz bytes via TarFixtureBuilder (shared hand-rolled ustar helper);
    // zip bytes stay local (framework ZipArchive).
    private static byte[] BuildZip(string entryName, string content)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task Should_FillLicenseTextFromTarball_When_NpmRegistryServesTarball()
    {
        var tgz = TarFixtureBuilder.BuildTgz("package/LICENSE", "NPM-WIRED-TARBALL-TEXT");
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.EndsWith(".tgz", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tgz) };
            }

            return StubHttpMessageHandler.Json(new
            {
                license = "MIT",
                dist = new { tarball = "https://registry.npmjs.org/acme/-/acme-1.0.0.tgz" },
                author = new { name = "Acme" },
            });
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);

        var result = await resolver.ResolveAsync(new Dependency("npm", "acme", "1.0.0", false));

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("NPM-WIRED-TARBALL-TEXT", result.LicenseText);
        Assert.Null(result.Reason);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Should_FallBackToLicenseUrl_When_NuGetTarballMisses()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.EndsWith(".nupkg", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.NotFound();
            }

            if (uri.EndsWith("acme-license.txt", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Text("NUGET-LICENSEURL-TEXT");
            }

            return StubHttpMessageHandler.Json(new
            {
                licenseExpression = "MIT",
                licenseUrl = "https://example.com/acme-license.txt",
                packageContent = "https://api.nuget.org/v3-flatcontainer/acme/1.0.0/acme.1.0.0.nupkg",
            });
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NuGetLicenseResolver(http);

        var result = await resolver.ResolveAsync(new Dependency("nuget", "Acme", "1.0.0", false));

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("NUGET-LICENSEURL-TEXT", result.LicenseText);
        Assert.Null(result.Reason);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Should_ResolveFromModuleZip_When_GoInfoLacksLicense()
    {
        // Go caps (#71) bound the zip scan; happy path unchanged: the zip
        // LICENSE maps to MIT and LicenseText is the curated DB text.
        var zip = BuildZip(
            "example.com/mod@v1.2.3/LICENSE",
            "MIT License\nPermission is hereby granted, free of charge, to any person obtaining a copy\nwithout warranty of any kind");
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
            return uri.EndsWith(".zip", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }
                : StubHttpMessageHandler.Json(new { Version = "v1.2.3" });
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new GoLicenseResolver(http);

        var result = await resolver.ResolveAsync(new Dependency("go", "example.com/mod", "v1.2.3", false));

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(SpdxLicenseTexts.GetText("MIT"), result.LicenseText);
        Assert.Null(result.Reason);
        Assert.NotNull(result.Enrichment);
        Assert.EndsWith(".zip", result.Enrichment.DownloadUrl, StringComparison.Ordinal);
        Assert.Equal(2, handler.CallCount);
    }
}
