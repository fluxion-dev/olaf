using System.IO.Compression;
using System.Net;
using System.Text;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Issue #71 B5: pinned fallback chain — embedded-file (tarball) text &gt;
/// licenseUrl fetch &gt; embedded DB text &gt; null. Reason vocabulary
/// (<c>tarball-miss:&lt;detail&gt;</c> · <c>tarball-timeout</c> ·
/// <c>tarball-too-large:&lt;bytes&gt;</c> ·
/// <c>licenseurl-fetch-failed:&lt;status-or-transport&gt;</c> ·
/// <c>spdxdb-miss:&lt;id&gt;</c>): first-failure wins and every null-text
/// result carries a reason (no silent null). Caller cancellation is
/// rethrown, never converted to Unknown. Stub handler only — no network.
/// </summary>
public sealed class LicenseTextChainTests
{
    private const string TarballUrl = "https://example.com/acme-1.0.0.tgz";
    private const string LicenseFileUrl = "https://example.com/acme-license.txt";

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

    [Fact]
    public async Task Should_PreferTarball_When_BothTarballAndLicenseUrlHit()
    {
        var licenseUrlHits = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.EndsWith(".tgz", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(BuildTgz("package/LICENSE", "TARBALL-WINS")),
                };
            }

            licenseUrlHits++;
            return StubHttpMessageHandler.Text("URL-LOSES");
        });
        using var http = ResolverTestHelpers.CreateClient(handler);

        var (text, reason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http, TarballUrl, LicenseFileUrl, "MIT", CancellationToken.None);

        Assert.Equal("TARBALL-WINS", text);
        Assert.Null(reason);
        Assert.Equal(0, licenseUrlHits);
    }

    [Fact]
    public async Task Should_FallBackToLicenseUrl_When_TarballMisses()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
            return uri.EndsWith(".tgz", StringComparison.Ordinal)
                ? StubHttpMessageHandler.NotFound()
                : StubHttpMessageHandler.Text("URL-WINS");
        });
        using var http = ResolverTestHelpers.CreateClient(handler);

        var (text, reason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http, TarballUrl, LicenseFileUrl, "NO-SUCH-LICENSE-071", CancellationToken.None);

        Assert.Equal("URL-WINS", text);
        Assert.Null(reason);
    }

    [Fact]
    public async Task Should_FallBackToDb_When_TarballAndLicenseUrlMiss()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);

        var (text, reason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http, TarballUrl, LicenseFileUrl, "MIT", CancellationToken.None);

        Assert.Equal(SpdxLicenseTexts.GetText("MIT"), text);
        Assert.Null(reason);
    }

    [Fact]
    public async Task Should_PinFirstFailure_When_AllStagesMiss()
    {
        // Tarball 404 + licenseUrl 404 + unknown id: null text pins the
        // FIRST failure (tarball-miss) — and a null text never ships
        // without a reason (no silent null).
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);

        var (text, reason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http, TarballUrl, LicenseFileUrl, "NO-SUCH-LICENSE-071", CancellationToken.None);

        Assert.Null(text);
        Assert.NotNull(reason);
        Assert.Equal("tarball-miss:not-found", reason);
    }

    [Fact]
    public async Task Should_SkipLicenseUrl_When_SchemeIsNotHttp()
    {
        // Non-http licenseUrl is skipped outright: zero HTTP, DB still wins.
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.Text("MUST-NOT-FETCH"));
        using var http = ResolverTestHelpers.CreateClient(handler);

        var (text, reason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
            http, tarballUrl: null, "ftp://example.com/acme-license.txt", "MIT", CancellationToken.None);

        Assert.Equal(SpdxLicenseTexts.GetText("MIT"), text);
        Assert.Null(reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Should_RethrowCancellation_When_CallerTokenFires()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.Text("MUST-NOT-FETCH"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            LicenseTextFetcher.TryFetchLicenseTextAsync(http, TarballUrl, LicenseFileUrl, "MIT", cts.Token));
    }
}
