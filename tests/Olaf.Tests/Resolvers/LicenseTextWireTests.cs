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
    private static byte[] BuildTgz(string entryName, string content) =>
        Gzip(BuildTar(entryName, content));

    private static byte[] Gzip(byte[] tar)
    {
        using var gzBuffer = new MemoryStream();
        using (var gzip = new GZipStream(gzBuffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(tar, 0, tar.Length);
        }

        return gzBuffer.ToArray();
    }

    // Minimal ustar writer (regular file, single entry): deterministic
    // in-memory tarball — no TarWriter DataStream uncertainty.
    private static byte[] BuildTar(string name, string content)
    {
        var payload = Encoding.UTF8.GetBytes(content);
        using var buffer = new MemoryStream();
        var header = new byte[512];
        WriteField(header, 0, 100, name);
        WriteField(header, 100, 8, "0000777\0");
        WriteField(header, 108, 8, "0000000\0");
        WriteField(header, 116, 8, "0000000\0");
        WriteOctal(header, 124, 12, payload.Length);
        WriteOctal(header, 136, 12, 0);
        for (var i = 148; i < 156; i++)
        {
            header[i] = 0x20;
        }

        header[156] = (byte)'0';
        WriteField(header, 257, 6, "ustar\0");
        WriteField(header, 263, 2, "00");
        var checksum = 0;
        foreach (var b in header)
        {
            checksum += b;
        }

        WriteField(header, 148, 8, Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ");
        buffer.Write(header, 0, header.Length);
        buffer.Write(payload, 0, payload.Length);
        var pad = (512 - (payload.Length % 512)) % 512;
        for (var i = 0; i < pad; i++)
        {
            buffer.WriteByte(0);
        }

        buffer.Write(new byte[1024], 0, 1024);
        return buffer.ToArray();
    }

    private static void WriteField(byte[] header, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, header, offset, Math.Min(bytes.Length, length));
    }

    private static void WriteOctal(byte[] header, int offset, int length, int value) =>
        WriteField(header, offset, length, Convert.ToString(value, 8).PadLeft(length - 1, '0') + "\0");

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
        var tgz = BuildTgz("package/LICENSE", "NPM-WIRED-TARBALL-TEXT");
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
