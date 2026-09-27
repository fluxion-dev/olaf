using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Implementer-owned synthetic builders for container-image tests (issue #64).
/// All image fixtures are built programmatically with <see cref="TarWriter"/>;
/// no binary <c>.tar</c> check-ins, no network. Tester asserts only.
/// </summary>
internal static class ImageFixtureBuilder
{
    internal static string ApkInstalled(params (string Name, string Version)[] packages)
    {
        var sb = new StringBuilder();
        foreach (var (name, version) in packages)
        {
            sb.Append("P:").Append(name).Append('\n');
            sb.Append("V:").Append(version).Append('\n');
            sb.Append("A:").Append("x86_64").Append('\n');
            sb.Append('\n');
        }

        return sb.ToString();
    }

    internal static string DpkgStatus(params (string Name, string Version)[] packages)
    {
        var sb = new StringBuilder();
        foreach (var (name, version) in packages)
        {
            sb.Append("Package: ").Append(name).Append('\n');
            sb.Append("Status: install ok installed\n");
            sb.Append("Version: ").Append(version).Append('\n');
            sb.Append('\n');
        }

        return sb.ToString();
    }

    internal static string WriteTempTextFile(string fileName, string contents)
    {
        var dir = ParserTestHelpers.CreateTempDir();
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    internal static byte[] GzipBytes(byte[] raw)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }

        return buffer.ToArray();
    }

    internal static void AddTextEntry(TarWriter writer, string entryName, string contents)
    {
        AddBytesEntry(writer, entryName, Encoding.UTF8.GetBytes(contents));
    }

    internal static void AddBytesEntry(TarWriter writer, string entryName, byte[] contents)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = new MemoryStream(contents),
        };
        writer.WriteEntry(entry);
    }

    internal static byte[] BuildLayerTar(IReadOnlyDictionary<string, string> pathToContent)
    {
        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, content) in pathToContent)
            {
                AddTextEntry(writer, path, content);
            }
        }

        return buffer.ToArray();
    }

    internal static byte[] BuildRawLayerTar(Action<TarWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            write(writer);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Docker-save style image: <c>manifest.json</c> + <c>{id}/layer.tar</c> entries.
    /// Layers apply bottom→top in list order.
    /// </summary>
    internal static string BuildDockerImageTar(
        string destPath, IReadOnlyList<IReadOnlyDictionary<string, string>> layers, bool gzipOuter = false)
    {
        var layerNames = new List<string>();
        var blobs = new List<byte[]>();
        for (var i = 0; i < layers.Count; i++)
        {
            layerNames.Add($"layer{i}/layer.tar");
            blobs.Add(BuildLayerTar(layers[i]));
        }

        var manifest = JsonSerializer.Serialize(
            new[] { new { Config = "config.json", RepoTags = new[] { "test:latest" }, Layers = layerNames } });

        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            AddTextEntry(writer, "manifest.json", manifest);
            AddTextEntry(writer, "config.json", "{}");
            for (var i = 0; i < blobs.Count; i++)
            {
                AddBytesEntry(writer, layerNames[i], blobs[i]);
            }
        }

        var bytes = buffer.ToArray();
        File.WriteAllBytes(destPath, gzipOuter ? GzipBytes(bytes) : bytes);
        return destPath;
    }

    /// <summary>OCI layout tarball: <c>index.json</c> + <c>oci-layout</c> + <c>blobs/sha256/*</c>.</summary>
    internal static string BuildOciImageTar(
        string destPath, IReadOnlyList<IReadOnlyDictionary<string, string>> layers, bool gzipLayers = false)
    {
        var layerDigests = new List<string>();
        var blobMap = new Dictionary<string, byte[]>();
        for (var i = 0; i < layers.Count; i++)
        {
            var tar = BuildLayerTar(layers[i]);
            var payload = gzipLayers ? GzipBytes(tar) : tar;
            var digest = "sha256:layer" + i;
            layerDigests.Add(digest);
            blobMap["blobs/sha256/layer" + i] = payload;
        }

        var manifestDoc = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            layers = layerDigests.Select(d => new { mediaType = "application/vnd.oci.image.layer.v1.tar", digest = d }),
        });
        blobMap["blobs/sha256/manifest0"] = Encoding.UTF8.GetBytes(manifestDoc);
        var indexDoc = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            manifests = new[]
            {
                new
                {
                    mediaType = "application/vnd.oci.image.manifest.v1+json",
                    digest = "sha256:manifest0",
                    size = manifestDoc.Length,
                },
            },
        });

        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            AddTextEntry(writer, "oci-layout", "{\"imageLayoutVersion\":\"1.0.0\"}");
            AddTextEntry(writer, "index.json", indexDoc);
            foreach (var (name, payload) in blobMap)
            {
                AddBytesEntry(writer, name, payload);
            }
        }

        File.WriteAllBytes(destPath, buffer.ToArray());
        return destPath;
    }

    /// <summary>Valid tar that is NOT an image (no manifest.json/index.json).</summary>
    internal static string BuildNonImageTar(string destPath)
    {
        using var file = File.Create(destPath);
        using var writer = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: false);
        AddTextEntry(writer, "notes.txt", "not an image");
        return destPath;
    }

    /// <summary>Truncated image tar (valid header, cut payload) — must read as corrupt.</summary>
    internal static string BuildCorruptTar(string destPath, IReadOnlyDictionary<string, string> layer)
    {
        var dir = ParserTestHelpers.CreateTempDir();
        var valid = Path.Combine(dir, "valid.tar");
        BuildDockerImageTar(valid, new[] { layer });
        var bytes = File.ReadAllBytes(valid);
        File.WriteAllBytes(destPath, bytes[..Math.Min(bytes.Length, 1024)]);
        return destPath;
    }

    internal static string WriteGarbage(string destPath)
    {
        File.WriteAllBytes(destPath, "this is not a tarball at all"u8.ToArray());
        return destPath;
    }
}
