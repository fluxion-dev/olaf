using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Olaf.Core;

namespace Olaf.Parsers;

/// <summary>
/// Container image parser (OCI/Docker tarball or exploded layout directory).
/// Emits <c>apk</c>/<c>dpkg</c> <see cref="Dependency"/> records (<c>IsTransitive:false</c>);
/// the parser's own <see cref="Ecosystem"/> is <c>"container"</c> (a routing label only —
/// nothing asserts <c>parser.Ecosystem == dep.Ecosystem</c>, verified by grep).
/// Layer merge intentionally bypasses the registry's global first-wins dedup:
/// within one image the dedup key is name-only per container DB path and the
/// topmost layer wins (filesystem-overlay semantics).
/// RPM <c>Packages</c> DB bytes are ignored (binary, deferred to issue #65) — never crash.
/// </summary>
public sealed class ContainerImageParser : IEcosystemParser
{
    /// <summary>Default cap in whole MB (pinned; also shown in <c>--help</c>).</summary>
    public const long DefaultMaxImageMb = 1024;

    /// <summary>Default cap in bytes (1024 MiB).</summary>
    public const long DefaultMaxImageBytes = DefaultMaxImageMb * 1024 * 1024;

    /// <summary>
    /// Cap on total uncompressed bytes handled per image (layer blobs staged +
    /// files extracted to the overlay). Conservative single budget: a layer's bytes
    /// count once when staged and again when extracted. Set per CLI run from
    /// <c>--max-image-mb</c>; tests that mutate it should restore the default.
    /// </summary>
    public static long MaxImageBytes { get; set; } = DefaultMaxImageBytes;

    public string Ecosystem => "container";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Exploded OCI/docker-save layout markers.
        return string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "index.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "oci-layout", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            // Exploded layout: lenient by design (dir scans must not explode on
            // stray index.json files) — missing/unparseable markers yield [].
            return ParseExplodedDirectory(inputPath);
        }

        return ParseImageTar(inputPath);
    }

    internal IReadOnlyList<Dependency> ParseImageTar(string path)
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var stageDir = Path.Combine(tempRoot, "stage");
            var overlayDir = Path.Combine(tempRoot, "overlay");
            Directory.CreateDirectory(stageDir);
            Directory.CreateDirectory(overlayDir);

            long totalBytes = 0;
            var staged = StageOuterTar(path, stageDir, ref totalBytes);
            var layerOrder = ResolveLayerOrder(staged, path);
            foreach (var layerName in layerOrder)
            {
                if (!staged.TryGetValue(layerName, out var blobPath))
                {
                    throw new InvalidDataException(
                        $"Container image '{path}' is truncated: layer '{layerName}' is missing.");
                }

                ExtractLayerBlob(blobPath, overlayDir, ref totalBytes);
            }

            return ScanOverlay(overlayDir);
        }
        finally
        {
            DeleteQuietly(tempRoot);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "olaf-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; never mask scan results.
        }
    }

    private static Stream OpenMaybeGzip(Stream stream)
    {
        if (IsGzipMagic(stream))
        {
            return new GZipStream(stream, CompressionMode.Decompress, leaveOpen: false);
        }

        return stream;
    }

    private static bool IsGzipMagic(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return false;
        }

        var header = new byte[2];
        var read = stream.Read(header, 0, 2);
        stream.Seek(0, SeekOrigin.Begin);
        return read == 2 && header[0] == 0x1F && header[1] == 0x8B;
    }

    /// <summary>
    /// Single pass over the outer tar: small image-JSON entries go to memory,
    /// layer/blob entries are staged to temp files (counted toward the cap).
    /// </summary>
    private static Dictionary<string, string> StageOuterTar(
        string path, string stageDir, ref long totalBytes)
    {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;

        using var file = File.OpenRead(path);
        using var outer = OpenMaybeGzip(file);
        using var reader = new TarReader(outer);
        try
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                var normalized = NormalizeEntryPath(entry.Name);
                if (normalized is null)
                {
                    continue; // Zip-slip guard: reject absolute / parent-escaping names.
                }

                if (entry.EntryType is TarEntryType.Directory)
                {
                    continue;
                }

                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    // Guard: skip symlinks, hardlinks, devices, xattrs — never follow or create them.
                    continue;
                }

                if (IsImageJsonName(normalized))
                {
                    // DataStream may be null for empty files.
                    texts[normalized] = entry.DataStream is null ? string.Empty : ReadEntryText(entry);
                    continue;
                }

                var stagedPath = Path.Combine(stageDir, $"layer-{index++:D4}.tar");
                using (var dest = File.Create(stagedPath))
                {
                    if (entry.DataStream is not null)
                    {
                        CopyCounted(entry.DataStream, dest, ref totalBytes);
                    }
                }

                staged[normalized] = stagedPath;
            }
        }
        catch (IOException ex)
        {
            // System.Formats.Tar surfaces truncation/checksum damage as IOException,
            // which TryAddDependencies would swallow ([] + exit 0). Re-map to
            // InvalidDataException so corrupt tars escape to a clean exit-2 error.
            throw new InvalidDataException($"Container image '{path}' is corrupt or truncated: {ex.Message}", ex);
        }

        staged["\0manifest.json"] = texts.GetValueOrDefault("manifest.json", string.Empty);
        staged["\0index.json"] = texts.GetValueOrDefault("index.json", string.Empty);
        return staged;
    }

    private static bool IsImageJsonName(string normalized)
    {
        // Markers are top-level entries only (docker manifest.json, OCI
        // index.json/oci-layout); nested same-named files stage as blobs.
        return string.Equals(normalized, "manifest.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "index.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "oci-layout", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadEntryText(TarEntry entry)
    {
        using var buffer = new MemoryStream();
        entry.DataStream!.CopyTo(buffer);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Layer application order bottom→top from manifest.json (docker save) or
    /// index.json + blob manifests (OCI). No markers → not an image.
    /// </summary>
    private static IReadOnlyList<string> ResolveLayerOrder(Dictionary<string, string> staged, string path)
    {
        var manifestJson = staged["\0manifest.json"];
        if (!string.IsNullOrWhiteSpace(manifestJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(manifestJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    // First manifest wins (multi-image saves: document which image was scanned).
                    var first = doc.RootElement[0];
                    if (first.ValueKind == JsonValueKind.Object
                        && first.TryGetProperty("Layers", out var layers)
                        && layers.ValueKind == JsonValueKind.Array)
                    {
                        var order = layers.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => NormalizeEntryPath(e.GetString() ?? string.Empty))
                            .Where(n => n is not null)
                            .Cast<string>()
                            .ToList();
                        if (order.Count > 0)
                        {
                            return order;
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Container image '{path}' has an unreadable manifest.json: {ex.Message}", ex);
            }

            throw new InvalidOperationException(
                $"No container image layers found in '{path}': manifest.json has no usable Layers entry.");
        }

        var indexJson = staged["\0index.json"];
        if (!string.IsNullOrWhiteSpace(indexJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(indexJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("manifests", out var manifests)
                    && manifests.ValueKind == JsonValueKind.Array)
                {
                    string? digest = null;
                    foreach (var m in manifests.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object
                            || !m.TryGetProperty("digest", out var d)
                            || d.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var candidate = d.GetString() ?? string.Empty;
                        digest ??= candidate;
                        if (m.TryGetProperty("mediaType", out var mt)
                            && mt.ValueKind == JsonValueKind.String
                            && (mt.GetString() ?? string.Empty).Contains("image.manifest", StringComparison.Ordinal))
                        {
                            digest = candidate;
                            break;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(digest))
                    {
                        return ResolveOciLayers(staged, digest, path);
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Container image '{path}' has an unreadable index.json: {ex.Message}", ex);
            }

            throw new InvalidOperationException(
                $"No container image layers found in '{path}': index.json has no usable manifests entry.");
        }

        throw new InvalidOperationException(
            $"No parser found for '{path}': not a container image (missing manifest.json/index.json).");
    }

    private static IReadOnlyList<string> ResolveOciLayers(
        Dictionary<string, string> staged, string manifestDigest, string path)
    {
        var blobName = "blobs/" + manifestDigest.Replace(':', '/');
        if (!staged.TryGetValue(blobName, out var blobPath))
        {
            throw new InvalidDataException(
                $"Container image '{path}' is truncated: manifest blob '{blobName}' is missing.");
        }

        string manifestText;
        try
        {
            using var blob = File.OpenRead(blobPath);
            using var maybeGzip = OpenMaybeGzip(blob);
            using var buffer = new MemoryStream();
            maybeGzip.CopyTo(buffer);
            manifestText = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (IOException ex)
        {
            throw new InvalidDataException($"Container image '{path}' has an unreadable manifest blob: {ex.Message}", ex);
        }

        try
        {
            using var doc = JsonDocument.Parse(manifestText);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("layers", out var layers)
                && layers.ValueKind == JsonValueKind.Array)
            {
                var order = new List<string>();
                foreach (var layer in layers.EnumerateArray())
                {
                    if (layer.ValueKind == JsonValueKind.Object
                        && layer.TryGetProperty("digest", out var d)
                        && d.ValueKind == JsonValueKind.String)
                    {
                        var name = "blobs/" + (d.GetString() ?? string.Empty).Replace(':', '/');
                        if (name.Length > "blobs/".Length)
                        {
                            order.Add(name);
                        }
                    }
                }

                if (order.Count > 0)
                {
                    return order;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Container image '{path}' has an unreadable manifest blob: {ex.Message}", ex);
        }

        throw new InvalidOperationException(
            $"No container image layers found in '{path}': manifest blob has no usable layers entry.");
    }

    private static void ExtractLayerBlob(string blobPath, string overlayDir, ref long totalBytes)
    {
        try
        {
            using var blob = File.OpenRead(blobPath);
            using var layer = OpenMaybeGzip(blob);
            ExtractLayerEntries(layer, overlayDir, ref totalBytes);
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            // Same corrupt-tar remap as the outer pass (see StageOuterTar).
            throw new InvalidDataException($"Container image layer is corrupt or truncated: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Applies one layer tar onto the overlay FS with OCI whiteout rules and
    /// zip-slip guards (absolute/parent-escaping names and non-file/dir entries
    /// are skipped, never created or followed).
    /// </summary>
    internal static void ExtractLayerEntries(Stream layerStream, string overlayDir, ref long totalBytes)
    {
        var overlayFull = Path.GetFullPath(overlayDir);
        using var reader = new TarReader(layerStream);
        try
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                var normalized = NormalizeEntryPath(entry.Name);
                if (normalized is null)
                {
                    continue;
                }

                var segments = normalized.Split('/');
                var baseName = segments.Last();

                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    if (baseName.StartsWith(".wh.", StringComparison.Ordinal))
                    {
                        ApplyWhiteout(overlayFull, segments, baseName);
                        continue;
                    }

                    // Files merely containing "wh" elsewhere are untouched (prefix rule above).
                    var destPath = Confine(overlayFull, normalized);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    using (var dest = File.Create(destPath))
                    {
                        // DataStream may be null for empty files — still create the file.
                        if (entry.DataStream is not null)
                        {
                            CopyCounted(entry.DataStream, dest, ref totalBytes);
                        }
                    }

                    continue;
                }

                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(Confine(overlayFull, normalized));
                    continue;
                }

                // Guard: skip symlinks, hardlinks, devices, xattrs — never follow or create them.
                continue;
            }
        }
        catch (IOException ex)
        {
            // Same corrupt-tar remap as the outer pass (see StageOuterTar).
            throw new InvalidDataException($"Container image layer is corrupt or truncated: {ex.Message}", ex);
        }
    }

    private static void ApplyWhiteout(string overlayFull, string[] segments, string baseName)
    {
        var dirRel = string.Join('/', segments.Take(segments.Length - 1));
        var dirFull = dirRel.Length == 0 ? overlayFull : Confine(overlayFull, dirRel);
        if (string.Equals(baseName, ".wh..wh..opq", StringComparison.Ordinal))
        {
            // Opaque directory: clear all lower-layer entries under this dir.
            if (Directory.Exists(dirFull))
            {
                foreach (var file in Directory.GetFiles(dirFull))
                {
                    File.Delete(file);
                }

                foreach (var sub in Directory.GetDirectories(dirFull))
                {
                    Directory.Delete(sub, recursive: true);
                }
            }

            return;
        }

        var hidden = baseName.Substring(".wh.".Length);
        if (hidden.Length == 0)
        {
            return;
        }

        var targetRel = dirRel.Length == 0 ? hidden : dirRel + "/" + hidden;
        var target = Confine(overlayFull, targetRel);
        if (File.Exists(target))
        {
            File.Delete(target);
        }
        else if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }

    /// <summary>Normalizes a tar entry name to a relative slash path; null rejects.</summary>
    internal static string? NormalizeEntryPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name[0] == '/'
            || name[0] == '\\'
            || Path.IsPathRooted(name))
        {
            return null;
        }

        var clean = new List<string>();
        foreach (var part in name.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                return null;
            }

            clean.Add(part);
        }

        return clean.Count == 0 ? null : string.Join('/', clean);
    }

    private static string Confine(string overlayFull, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(overlayFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.Equals(overlayFull, StringComparison.Ordinal)
            && !full.StartsWith(overlayFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            // Defense in depth (NormalizeEntryPath already rejects "..").
            throw new InvalidDataException($"Container image entry escapes the image root: '{rel}'.");
        }

        return full;
    }

    private static void CopyCounted(Stream source, Stream destination, ref long totalBytes)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            totalBytes += read;
            if (totalBytes > MaxImageBytes)
            {
                throw new InvalidOperationException(
                    $"Container image exceeds the size cap ({MaxImageBytes} bytes uncompressed handled). " +
                    $"Raise it with --max-image-mb.");
            }
        }
    }

    /// <summary>
    /// Routes merged overlay DB files to the thin parsers. RPM <c>Packages</c>
    /// binaries (var/lib/rpm) are deliberately ignored here — binary format
    /// deferred to issue #65, never crash on them.
    /// </summary>
    internal static IReadOnlyList<Dependency> ScanOverlay(string overlayDir)
    {
        var merged = new List<Dependency>();
        var seen = new HashSet<(string Ecosystem, string Name)>();
        var apk = new ApkParser();
        var dpkg = new DpkgParser();

        List<string> dbFiles;
        try
        {
            dbFiles = Directory.EnumerateFiles(overlayDir, "*", SearchOption.AllDirectories)
                .Where(p => IsApkDb(overlayDir, p) || IsDpkgDb(overlayDir, p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }
        catch (IOException)
        {
            // Covers File/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<Dependency>();
        }

        foreach (var dbFile in dbFiles)
        {
            var deps = IsApkDb(overlayDir, dbFile)
                ? apk.Parse(dbFile)
                : dpkg.Parse(dbFile);
            foreach (var dep in deps)
            {
                if (seen.Add((dep.Ecosystem, dep.Name)))
                {
                    merged.Add(dep);
                }
            }
        }

        return merged
            .OrderBy(d => d.Ecosystem, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Version, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsApkDb(string overlayDir, string path)
    {
        var rel = Path.GetRelativePath(overlayDir, path).Replace(Path.DirectorySeparatorChar, '/');
        return rel.EndsWith("lib/apk/db/installed", StringComparison.Ordinal);
    }

    private static bool IsDpkgDb(string overlayDir, string path)
    {
        var rel = Path.GetRelativePath(overlayDir, path).Replace(Path.DirectorySeparatorChar, '/');
        return rel.EndsWith("lib/dpkg/status", StringComparison.Ordinal);
    }

    private IReadOnlyList<Dependency> ParseExplodedDirectory(string dir)
    {
        try
        {
            var indexPath = Path.Combine(dir, "index.json");
            var manifestPath = Path.Combine(dir, "manifest.json");
            string? indexJson = File.Exists(indexPath) ? File.ReadAllText(indexPath) : null;
            string? manifestJson = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : null;
            if (indexJson is null && manifestJson is null)
            {
                return Array.Empty<Dependency>();
            }

            var tempRoot = CreateTempRoot();
            try
            {
                var overlayDir = Path.Combine(tempRoot, "overlay");
                Directory.CreateDirectory(overlayDir);
                long totalBytes = 0;
                foreach (var blobPath in ResolveExplodedLayers(dir, indexJson, manifestJson))
                {
                    try
                    {
                        using var blob = File.OpenRead(blobPath);
                        using var layer = OpenMaybeGzip(blob);
                        ExtractLayerEntries(layer, overlayDir, ref totalBytes);
                    }
                    catch (IOException)
                    {
                        return Array.Empty<Dependency>();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return Array.Empty<Dependency>();
                    }
                }

                return ScanOverlay(overlayDir);
            }
            finally
            {
                DeleteQuietly(tempRoot);
            }
        }
        catch (IOException)
        {
            // Covers File/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<Dependency>();
        }
    }

    private static IEnumerable<string> ResolveExplodedLayers(string dir, string? indexJson, string? manifestJson)
    {
        var blobs = new List<string>();
        try
        {
            if (manifestJson is not null)
            {
                using var doc = JsonDocument.Parse(manifestJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var first = doc.RootElement[0];
                    if (first.ValueKind == JsonValueKind.Object
                        && first.TryGetProperty("Layers", out var layers)
                        && layers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var layer in layers.EnumerateArray())
                        {
                            if (layer.ValueKind == JsonValueKind.String)
                            {
                                var rel = NormalizeEntryPath(layer.GetString() ?? string.Empty);
                                if (rel is not null)
                                {
                                    blobs.Add(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
                                }
                            }
                        }
                    }
                }
            }
            else if (indexJson is not null)
            {
                using var doc = JsonDocument.Parse(indexJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("manifests", out var manifests)
                    && manifests.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in manifests.EnumerateArray())
                    {
                        if (m.ValueKind == JsonValueKind.Object
                            && m.TryGetProperty("digest", out var d)
                            && d.ValueKind == JsonValueKind.String)
                        {
                            var blob = Path.Combine(dir, ("blobs/" + (d.GetString() ?? string.Empty).Replace(':', '/')).Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(blob))
                            {
                                blobs.AddRange(ResolveExplodedManifestLayers(dir, blob));
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Lenient exploded-dir path: unparseable markers yield [] (see Parse).
        }

        return blobs.Where(File.Exists);
    }

    private static IEnumerable<string> ResolveExplodedManifestLayers(string dir, string manifestBlob)
    {
        var layers = new List<string>();
        try
        {
            var text = File.ReadAllText(manifestBlob);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("layers", out var layerArray)
                && layerArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var layer in layerArray.EnumerateArray())
                {
                    if (layer.ValueKind == JsonValueKind.Object
                        && layer.TryGetProperty("digest", out var d)
                        && d.ValueKind == JsonValueKind.String)
                    {
                        var blob = Path.Combine(dir, ("blobs/" + (d.GetString() ?? string.Empty).Replace(':', '/')).Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(blob))
                        {
                            layers.Add(blob);
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
            // Covers File/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }

        return layers;
    }
}
