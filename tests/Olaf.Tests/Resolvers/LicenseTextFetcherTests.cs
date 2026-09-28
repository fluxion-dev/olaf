using System.IO.Compression;
using System.Net;
using System.Text;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Issue #71 B2–B4: shared <see cref="LicenseTextFetcher"/> — tarball
/// extraction (tgz / zip / nupkg), LICENSE-first-match + COPYING/NOTICE
/// tier order, 1 MiB cap, corrupt-archive and symlink/absolute skips, plus
/// the UTF-8 / UTF-16 / Windows-1252 decode pipeline. All archives are
/// built in-memory (byte arrays only, no checked-in binaries, no live
/// network); extraction is exercised via the internal
/// <c>ExtractLicenseTextAsync</c> / <c>DecodeLicenseBytes</c> surface plus
/// end-to-end <c>TryFetchLicenseTextAsync</c> with a stub handler.
/// </summary>
public sealed class LicenseTextFetcherTests
{
    private static byte[] BuildTgz(params (string Name, string Content)[] files) =>
        Gzip(BuildTar(files.Select(f => (f.Name, f.Content, Symlink: false)).ToArray()));

    private static byte[] BuildTgzMixed(params (string Name, string Content, bool Symlink)[] files) =>
        Gzip(BuildTar(files));

    private static byte[] Gzip(byte[] tar)
    {
        using var gzBuffer = new MemoryStream();
        using (var gzip = new GZipStream(gzBuffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(tar, 0, tar.Length);
        }

        return gzBuffer.ToArray();
    }

    // Minimal ustar writer (regular files + symlinks only): deterministic
    // in-memory tarballs with exact control over entry names (absolute,
    // ".."-escaping) and types — no TarWriter DataStream uncertainty.
    private static byte[] BuildTar((string Name, string Content, bool Symlink)[] entries)
    {
        using var buffer = new MemoryStream();
        foreach (var (name, content, symlink) in entries)
        {
            var payload = symlink ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(content);
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

            header[156] = (byte)(symlink ? '2' : '0');
            WriteField(header, 157, 100, symlink ? content : string.Empty);
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

    private static byte[] BuildZip(params (string Name, string Content, bool Symlink)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content, symlink) in files)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                if (symlink)
                {
                    entry.ExternalAttributes = unchecked((int)0xA0000000);
                }

                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        return buffer.ToArray();
    }

    private static HttpResponseMessage Bytes(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

    [Fact]
    public async Task Should_ExtractLicenseFile_When_TgzContainsLicense()
    {
        var payload = BuildTgz(
            ("package/package.json", "{\"name\":\"acme\"}"),
            ("package/LICENSE", "TARBALL MIT TEXT"));

        var text = await LicenseTextFetcher.ExtractLicenseTextAsync(payload, ".tgz", CancellationToken.None);

        Assert.Equal("TARBALL MIT TEXT", text);
    }

    [Fact]
    public async Task Should_ExtractLicenseFile_When_ZipContainsLicense()
    {
        var payload = BuildZip(
            ("acme-1.0.0/README.md", "readme", false),
            ("acme-1.0.0/LICENSE", "ZIP MIT TEXT", false));

        var text = await LicenseTextFetcher.ExtractLicenseTextAsync(payload, ".zip", CancellationToken.None);

        Assert.Equal("ZIP MIT TEXT", text);
    }

    [Fact]
    public async Task Should_ExtractLicenseFile_When_NupkgShapedZip()
    {
        // Nupkg IS a zip (PK magic): nested lib/ path + .nuspec alongside.
        var payload = BuildZip(
            ("lib/net8.0/acme.dll", "binary-stub", false),
            ("acme.nuspec", "<package/>", false),
            ("LICENSE", "NUPKG MIT TEXT", false));

        var text = await LicenseTextFetcher.ExtractLicenseTextAsync(payload, ".nupkg", CancellationToken.None);

        Assert.Equal("NUPKG MIT TEXT", text);
    }

    [Fact]
    public async Task Should_ReturnFirstMatch_When_TgzHasTwoLicenseFiles()
    {
        var payload = BuildTgz(
            ("a/LICENSE", "FIRST"),
            ("b/LICENSE", "SECOND"));

        var text = await LicenseTextFetcher.ExtractLicenseTextAsync(payload, ".tgz", CancellationToken.None);

        Assert.Equal("FIRST", text);
    }

    [Fact]
    public async Task Should_PreferCopyingOverNotice_When_NoLicensePresent()
    {
        // NOTICE listed first: tier (COPYING > NOTICE) wins, not order.
        var payload = BuildTgz(
            ("pkg/NOTICE", "NOTICE-TEXT"),
            ("pkg/COPYING", "COPYING-TEXT"));

        var text = await LicenseTextFetcher.ExtractLicenseTextAsync(payload, ".tgz", CancellationToken.None);

        Assert.Equal("COPYING-TEXT", text);

        var noticeOnly = BuildZip(("pkg/NOTICE", "NOTICE-ONLY", false));
        var fallback = await LicenseTextFetcher.ExtractLicenseTextAsync(noticeOnly, ".zip", CancellationToken.None);

        Assert.Equal("NOTICE-ONLY", fallback);
    }

    [Fact]
    public async Task Should_ReportTooLarge_When_TarballExceedsOneMib()
    {
        // Streaming gate: MaxBytes + 1 zeros trip tarball-too-large before
        // any archive parsing (content is irrelevant).
        var oversized = new byte[LicenseTextLimits.MaxBytes + 1];
        var handler = new StubHttpMessageHandler((req, _) => Bytes(oversized));
        using var http = ResolverTestHelpers.CreateClient(handler);

        var (text, reason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http, "https://example.com/acme-1.0.0.tgz", licenseUrl: null, "NO-SUCH-LICENSE-071", CancellationToken.None);

        Assert.Null(text);
        Assert.Equal($"tarball-too-large:{LicenseTextLimits.MaxBytes + 1}", reason);
    }

    [Fact]
    public async Task Should_ReportMiss_When_TarballIsCorrupt()
    {
        var corrupt = Encoding.UTF8.GetBytes("this is definitely not a gzip or zip archive");
        var handler = new StubHttpMessageHandler((req, _) => Bytes(corrupt));
        using var http = ResolverTestHelpers.CreateClient(handler);

        var (text, reason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http, "https://example.com/acme-1.0.0.tgz", licenseUrl: null, "NO-SUCH-LICENSE-071", CancellationToken.None);

        Assert.Null(text);
        Assert.Equal("tarball-miss:no-license-file", reason);
    }

    [Fact]
    public async Task Should_SkipSymlinkAndAbsoluteEntries_When_Extracting()
    {
        // Symlink LICENSE, absolute-path LICENSE, and ".."-escaping
        // LICENSE are all skipped; the real COPYING wins. The ".." entry
        // is first: a broken skip would return EVIL immediately (tier 0).
        var payload = BuildTgzMixed(
            ("pkg/../../evil/LICENSE", "EVIL", false),
            ("LICENSE", "COPYING/COPYING", true),
            ("/abs/LICENSE", "ABS-TEXT", false),
            ("COPYING/COPYING", "COPYING-OK", false));

        var text = await LicenseTextFetcher.ExtractLicenseTextAsync(payload, ".tgz", CancellationToken.None);

        Assert.Equal("COPYING-OK", text);

        var zipPayload = BuildZip(
            ("LICENSE", "LINK-TARGET", true),
            ("COPYING", "ZIP-COPYING-OK", false));
        var zipText = await LicenseTextFetcher.ExtractLicenseTextAsync(zipPayload, ".zip", CancellationToken.None);

        Assert.Equal("ZIP-COPYING-OK", zipText);

        Assert.False(LicenseTextFetcher.IsSafeArchiveName("/abs/LICENSE"));
        Assert.False(LicenseTextFetcher.IsSafeArchiveName("pkg/../../evil/LICENSE"));
        Assert.False(LicenseTextFetcher.IsSafeArchiveName("../evil"));
        Assert.True(LicenseTextFetcher.IsSafeArchiveName("pkg/COPYING"));
    }

    [Fact]
    public void Should_DecodeUtf16LeBom_When_BomPresent()
    {
        const string expected = "Hello UTF-16 LE licensed";
        var payload = new byte[] { 0xFF, 0xFE }
            .Concat(Encoding.Unicode.GetBytes(expected)).ToArray();

        var text = LicenseTextFetcher.DecodeLicenseBytes(payload);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Should_DecodeUtf8Strict_When_NoBom()
    {
        const string expected = "Café — MIT licensed";
        var payload = Encoding.UTF8.GetBytes(expected);

        var text = LicenseTextFetcher.DecodeLicenseBytes(payload);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Should_FallBackToLatin1AndStripNuls_When_InvalidUtf8()
    {
        // 0xE9 is invalid strict UTF-8 here -> Windows-1252 ("é"); the
        // embedded NUL is stripped, never surfaced.
        var payload = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, 0x00, (byte)'!' };

        var text = LicenseTextFetcher.DecodeLicenseBytes(payload);

        Assert.Equal("café!", text);
    }
}
