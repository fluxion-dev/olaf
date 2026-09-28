using System.IO.Compression;
using System.Text;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Shared hand-rolled ustar builder (issues #71/#72) — single home for the
/// inline tgz writers previously triplicated across
/// <c>LicenseTextChainTests</c>, <c>LicenseTextFetcherTests</c>, and
/// <c>CopyrightIntegrationTests</c>. Deterministic in-memory tarballs with
/// exact control over entry names and types: hand-rolled 512-byte headers,
/// never <c>TarWriter</c> (null-DataStream trap), so symlink / absolute /
/// <c>..</c>-escaping adversarial entries stay expressible. Regular files
/// only unless the <c>Mixed</c> overload marks an entry as symlink.
/// </summary>
internal static class TarFixtureBuilder
{
    internal static byte[] BuildTgz(string entryName, string content) =>
        Gzip(BuildTar(new[] { (entryName, content, false) }));

    internal static byte[] BuildTgz(params (string Name, string Content)[] files) =>
        Gzip(BuildTar(files.Select(f => (f.Name, f.Content, false)).ToArray()));

    internal static byte[] BuildTgzMixed(params (string Name, string Content, bool Symlink)[] files) =>
        Gzip(BuildTar(files));

    internal static byte[] Gzip(byte[] tar)
    {
        using var gzBuffer = new MemoryStream();
        using (var gzip = new GZipStream(gzBuffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(tar, 0, tar.Length);
        }

        return gzBuffer.ToArray();
    }

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
}
