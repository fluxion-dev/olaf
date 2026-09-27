using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Implementer-owned synthetic builders for container-image tests (issues #64–65).
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

    /// <summary>
    /// apk <c>installed</c> stanza with <c>L:</c> (declared license) + <c>A:</c> (arch).
    /// License/arch are validated-but-deferred to issue #70: present in the
    /// fixture so tests pin that parsing still succeeds, values not stored.
    /// </summary>
    internal static string ApkInstalledWithMeta(params (string Name, string Version, string License, string Arch)[] packages)
    {
        var sb = new StringBuilder();
        foreach (var (name, version, license, arch) in packages)
        {
            sb.Append("P:").Append(name).Append('\n');
            sb.Append("V:").Append(version).Append('\n');
            sb.Append("L:").Append(license).Append('\n');
            sb.Append("A:").Append(arch).Append('\n');
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// dpkg <c>status</c> stanza with <c>Architecture:</c>.
    /// Arch is validated-but-deferred to issue #70 (see <see cref="ApkInstalledWithMeta"/>).
    /// </summary>
    internal static string DpkgStatusWithArch(params (string Name, string Version, string Arch)[] packages)
    {
        var sb = new StringBuilder();
        foreach (var (name, version, arch) in packages)
        {
            sb.Append("Package: ").Append(name).Append('\n');
            sb.Append("Status: install ok installed\n");
            sb.Append("Architecture: ").Append(arch).Append('\n');
            sb.Append("Version: ").Append(version).Append('\n');
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// dpkg stanza with a folded <c>Description:</c> body (continuation lines
    /// starting with space/tab) — key detection must not false-match them.
    /// </summary>
    internal static string DpkgStatusFolded(string name, string version)
    {
        var sb = new StringBuilder();
        sb.Append("Package: ").Append(name).Append('\n');
        sb.Append("Status: install ok installed\n");
        sb.Append("Version: ").Append(version).Append('\n');
        sb.Append("Description: a long description\n");
        sb.Append(" continued line starting with a space\n");
        sb.Append("\ttab-continued line\n");
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// apk stanza with a folded continuation line (starting with a space) —
    /// key detection must not false-match it.
    /// </summary>
    internal static string ApkInstalledFolded(string name, string version)
    {
        var sb = new StringBuilder();
        sb.Append("P:").Append(name).Append('\n');
        sb.Append("V:").Append(version).Append('\n');
        sb.Append("A:x86_64\n");
        sb.Append(" continuation line starting with a space\n");
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// rpm <c>Packages</c> text dump: one <c>name-ver-rel.arch</c> NVRA line
    /// per package (<c>rpm -qa</c> default output).
    /// </summary>
    internal static string RpmPackages(params string[] nvra)
    {
        var sb = new StringBuilder();
        foreach (var line in nvra)
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Synthetic binary rpm bytes (NUL-heavy, BerkeleyDB-flavored magic) —
    /// the parser must sniff these and yield <c>[]</c>, never crash.
    /// </summary>
    internal static byte[] BinaryRpmBytes()
    {
        return new byte[] { 0xED, 0xAB, 0xEE, 0xDB, 0x00, 0x62, 0x69, 0x6E, 0x00, 0x72, 0x70, 0x6D, 0x00 };
    }

    internal static string WriteTempBytesFile(string fileName, byte[] contents)
    {
        var dir = ParserTestHelpers.CreateTempDir();
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, contents);
        return path;
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
