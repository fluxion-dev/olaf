using System.Formats.Tar;
using System.Text.Json;
using Olaf.Parsers;

namespace Olaf.Tests.Parsers;

/// <summary>
/// Coverage for the container-image parser (issue #64): tar detection
/// (extension + layout markers), docker/OCI/ exploded-dir scans, OCI whiteout
/// rules, layer last-wins, non-image/corrupt-tar errors, size cap, zip-slip
/// guards, rpm-bytes passthrough (deferred to #65), gzip-outer, temp cleanup.
/// All image unit tests live in this one class so the <c>MaxImageBytes</c>
/// mutation in the cap test cannot race parallel test classes (xUnit runs
/// methods within a class sequentially).
/// </summary>
public sealed class ContainerImageParserTests
{
    private const string ApkDbPath = "lib/apk/db/installed";
    private const string DpkgDbPath = "var/lib/dpkg/status";

    private static string NewImagePath(string fileName = "image.tar")
    {
        return Path.Combine(ParserTestHelpers.CreateTempDir(), fileName);
    }

    /// <summary>
    /// Outer docker-save wrapper around prebuilt layer blobs (for layers that
    /// need raw tar entries such as whiteouts or hostile paths).
    /// </summary>
    private static string BuildDockerImageFromBlobs(string destPath, IReadOnlyList<byte[]> blobs)
    {
        var layerNames = blobs.Select((_, i) => $"layer{i}/layer.tar").ToList();
        var manifest = JsonSerializer.Serialize(
            new[] { new { Config = "config.json", RepoTags = new[] { "test:latest" }, Layers = layerNames } });

        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            ImageFixtureBuilder.AddTextEntry(writer, "manifest.json", manifest);
            ImageFixtureBuilder.AddTextEntry(writer, "config.json", "{}");
            for (var i = 0; i < blobs.Count; i++)
            {
                ImageFixtureBuilder.AddBytesEntry(writer, layerNames[i], blobs[i]);
            }
        }

        File.WriteAllBytes(destPath, buffer.ToArray());
        return destPath;
    }

    [Theory]
    [InlineData("image.tar")]
    [InlineData("image.tar.gz")]
    [InlineData("image.tgz")]
    [InlineData("IMAGE.TAR")]
    public void Should_MatchTarExtensions_When_CanHandle(string fileName)
    {
        var parser = ParserTestHelpers.ResolveParser("container");

        Assert.True(parser.CanHandle(fileName));
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("index.json")]
    [InlineData("oci-layout")]
    public void Should_MatchLayoutMarkers_When_CanHandle(string fileName)
    {
        var parser = ParserTestHelpers.ResolveParser("container");

        Assert.True(parser.CanHandle(fileName));
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData("installed")]
    [InlineData("status")]
    [InlineData("notes.txt")]
    public void Should_RejectNonImageNames_When_CanHandle(string fileName)
    {
        var parser = ParserTestHelpers.ResolveParser("container");

        Assert.False(parser.CanHandle(fileName));
    }

    [Fact]
    public void Should_ScanApkAndDpkg_When_DockerImageTar()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("musl", "1.2.5-r0"), ("zlib", "1.3.1-r0")),
                },
                new Dictionary<string, string>
                {
                    [DpkgDbPath] = ImageFixtureBuilder.DpkgStatus(("bash", "5.2-5")),
                },
            });

        var deps = parser.Parse(path);

        Assert.Equal(3, deps.Count);
        Assert.Contains(deps, d => d.Ecosystem == "apk" && d.Name == "musl" && d.Version == "1.2.5-r0");
        Assert.Contains(deps, d => d.Ecosystem == "apk" && d.Name == "zlib" && d.Version == "1.3.1-r0");
        Assert.Contains(deps, d => d.Ecosystem == "dpkg" && d.Name == "bash" && d.Version == "5.2-5");
    }

    [Fact]
    public void Should_ScanLayers_When_OciImageTar()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildOciImageTar(
            NewImagePath("image.tar"),
            new[]
            {
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("busybox", "1.36.1-r0")),
                    [DpkgDbPath] = ImageFixtureBuilder.DpkgStatus(("grep", "3.11-1")),
                },
            });

        var deps = parser.Parse(path);

        Assert.Equal(2, deps.Count);
        Assert.Contains(deps, d => d.Ecosystem == "apk" && d.Name == "busybox");
        Assert.Contains(deps, d => d.Ecosystem == "dpkg" && d.Name == "grep");
    }

    [Fact]
    public void Should_ScanLayers_When_ExplodedOciDirectory()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var dir = ParserTestHelpers.CreateTempDir();
        var layerBytes = ImageFixtureBuilder.BuildLayerTar(new Dictionary<string, string>
        {
            [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("libcrypto", "3.1.5-r0")),
        });
        var manifestDoc = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            layers = new[] { new { mediaType = "application/vnd.oci.image.layer.v1.tar", digest = "sha256:layer0" } },
        });
        var indexDoc = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            manifests = new[]
            {
                new { mediaType = "application/vnd.oci.image.manifest.v1+json", digest = "sha256:manifest0", size = manifestDoc.Length },
            },
        });
        Directory.CreateDirectory(Path.Combine(dir, "blobs", "sha256"));
        File.WriteAllBytes(Path.Combine(dir, "blobs", "sha256", "layer0"), layerBytes);
        File.WriteAllText(Path.Combine(dir, "blobs", "sha256", "manifest0"), manifestDoc);
        File.WriteAllText(Path.Combine(dir, "index.json"), indexDoc);

        var deps = parser.Parse(dir);

        Assert.Contains(deps, d => d.Ecosystem == "apk" && d.Name == "libcrypto" && d.Version == "3.1.5-r0");
    }

    [Fact]
    public void Should_RemoveTarget_When_WhiteoutInUpperLayer()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("doomed", "1.0-r0")),
                    [DpkgDbPath] = ImageFixtureBuilder.DpkgStatus(("survivor", "2.0-1")),
                },
                new Dictionary<string, string>
                {
                    ["lib/apk/db/.wh.installed"] = string.Empty,
                },
            });

        var deps = parser.Parse(path);

        Assert.DoesNotContain(deps, d => d.Name == "doomed");
        Assert.Contains(deps, d => d.Ecosystem == "dpkg" && d.Name == "survivor");
    }

    [Fact]
    public void Should_KeepWhNamedPackages_When_WhiteoutApplies()
    {
        // Files merely containing "wh" elsewhere are untouched: package names
        // with "wh" inside must survive a sibling whiteout.
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("doomed", "1.0-r0")),
                    [DpkgDbPath] = ImageFixtureBuilder.DpkgStatus(("somewhere-lib", "4.0-1")),
                },
                new Dictionary<string, string>
                {
                    ["lib/apk/db/.wh.installed"] = string.Empty,
                },
            });

        var deps = parser.Parse(path);

        Assert.DoesNotContain(deps, d => d.Name == "doomed");
        Assert.Contains(deps, d => d.Ecosystem == "dpkg" && d.Name == "somewhere-lib" && d.Version == "4.0-1");
    }

    [Fact]
    public void Should_ClearDirectory_When_OpaqueWhiteout()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("apkpkg", "1.0-r0")),
                    [DpkgDbPath] = ImageFixtureBuilder.DpkgStatus(("opaque-victim", "1.0-1")),
                },
                new Dictionary<string, string>
                {
                    ["var/lib/dpkg/.wh..wh..opq"] = string.Empty,
                },
            });

        var deps = parser.Parse(path);

        Assert.DoesNotContain(deps, d => d.Name == "opaque-victim");
        Assert.Contains(deps, d => d.Ecosystem == "apk" && d.Name == "apkpkg");
    }

    [Fact]
    public void Should_PreferTopmostVersion_When_SamePackageAcrossLayers()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("foo", "1.0-r0")),
                },
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("foo", "2.0-r0")),
                },
            });

        var deps = parser.Parse(path);

        var foo = Assert.Single(deps, d => d.Name == "foo");
        Assert.Equal("2.0-r0", foo.Version);
    }

    [Fact]
    public void Should_RestorePackage_When_DeletedThenReadded()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("phoenix", "1.0-r0")),
                },
                new Dictionary<string, string>
                {
                    ["lib/apk/db/.wh.installed"] = string.Empty,
                },
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("phoenix", "3.0-r0")),
                },
            });

        var deps = parser.Parse(path);

        var reborn = Assert.Single(deps, d => d.Name == "phoenix");
        Assert.Equal("3.0-r0", reborn.Version);
    }

    [Fact]
    public void Should_ThrowInvalidOperation_When_NonImageTar()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildNonImageTar(NewImagePath());

        var parserEx = Assert.Throws<InvalidOperationException>(() => parser.Parse(path));
        Assert.Contains("not a container image", parserEx.Message, StringComparison.Ordinal);

        var registryEx = Assert.Throws<InvalidOperationException>(() => new ParserRegistry().Scan(path));
        Assert.Contains("not a container image", registryEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ThrowInvalidData_When_CorruptTruncatedTar()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildCorruptTar(
            NewImagePath(),
            new Dictionary<string, string>
            {
                [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("truncated", "1.0-r0")),
            });

        var ex = Assert.Throws<InvalidDataException>(() => parser.Parse(path));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Should_ThrowWhen_CapExceeded()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("big", "1.0-r0")),
                },
            });
        var previous = ContainerImageParser.MaxImageBytes;
        ContainerImageParser.MaxImageBytes = 16;
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => parser.Parse(path));

            Assert.Contains("--max-image-mb", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            ContainerImageParser.MaxImageBytes = previous;
        }
    }

    [Fact]
    public void Should_SkipUnsafeEntries_When_ZipSlipPaths()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var sandbox = ParserTestHelpers.CreateTempDir();
        var canary = Path.Combine(sandbox, "pwned.txt");
        var layer = ImageFixtureBuilder.BuildRawLayerTar(writer =>
        {
            ImageFixtureBuilder.AddTextEntry(writer, "../evil.txt", "escape");
            ImageFixtureBuilder.AddTextEntry(writer, canary, "absolute");
            ImageFixtureBuilder.AddTextEntry(writer, ApkDbPath, ImageFixtureBuilder.ApkInstalled(("innocent", "1.0-r0")));
        });
        var path = BuildDockerImageFromBlobs(NewImagePath(), new[] { layer });

        var deps = parser.Parse(path);

        Assert.Contains(deps, d => d.Ecosystem == "apk" && d.Name == "innocent");
        Assert.False(File.Exists(canary));
        Assert.False(File.Exists(Path.Combine(sandbox, "evil.txt")));
    }

    [Fact]
    public void Should_IgnoreRpmBytes_When_RpmDbPresent()
    {
        // RPM Packages binaries are deferred to #65: ignored, never crash.
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    ["var/lib/rpm/Packages"] = "\0binary\0rpm\0bytes\0",
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("rpm-neighbor", "1.0-r0")),
                },
            });

        var deps = parser.Parse(path);

        Assert.Contains(deps, d => d.Ecosystem == "apk" && d.Name == "rpm-neighbor");
        Assert.DoesNotContain(deps, d => d.Ecosystem == "rpm");
    }

    [Fact]
    public void Should_ReadGzipOuter_When_TgzImage()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath("image.tgz"),
            new[]
            {
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("gzipd", "1.0-r0")),
                },
            },
            gzipOuter: true);

        var deps = parser.Parse(path);

        Assert.Contains(deps, d => d.Ecosystem == "apk" && d.Name == "gzipd");
    }

    [Fact]
    public void Should_LeaveNoTempDirs_When_ImageScanned()
    {
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("tidy", "1.0-r0")),
                },
            });
        var before = new HashSet<string>(
            Directory.GetDirectories(Path.GetTempPath(), "olaf-img-*"), StringComparer.Ordinal);

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        var after = Directory.GetDirectories(Path.GetTempPath(), "olaf-img-*");
        Assert.All(after, d => Assert.Contains(d, before));
    }

    [Fact]
    public void Should_ScanBothDbKinds_When_ExplodedDockerDirectory()
    {
        // Exploded docker-save layout: manifest.json + layer files on disk.
        var parser = ParserTestHelpers.ResolveParser("container");
        var dir = ParserTestHelpers.CreateTempDir();
        var layerBytes = ImageFixtureBuilder.BuildLayerTar(new Dictionary<string, string>
        {
            [DpkgDbPath] = ImageFixtureBuilder.DpkgStatus(("exploded", "7.0-1")),
        });
        Directory.CreateDirectory(Path.Combine(dir, "layer0"));
        File.WriteAllBytes(Path.Combine(dir, "layer0", "layer.tar"), layerBytes);
        var manifest = JsonSerializer.Serialize(
            new[] { new { Config = "config.json", RepoTags = new[] { "test:latest" }, Layers = new[] { "layer0/layer.tar" } } });
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest);

        var deps = parser.Parse(dir);

        Assert.Contains(deps, d => d.Ecosystem == "dpkg" && d.Name == "exploded");
    }

    [Fact]
    public void Should_EmitApkDpkgOnly_When_ImageScanned()
    {
        // The parser's own Ecosystem is the "container" routing label; emitted
        // deps carry apk|dpkg (nothing asserts parser.Ecosystem == dep.Ecosystem).
        var parser = ParserTestHelpers.ResolveParser("container");
        Assert.Equal("container", parser.Ecosystem);
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    [ApkDbPath] = ImageFixtureBuilder.ApkInstalled(("a", "1.0-r0")),
                    [DpkgDbPath] = ImageFixtureBuilder.DpkgStatus(("b", "1.0-1")),
                },
            });

        var deps = parser.Parse(path);

        Assert.NotEmpty(deps);
        Assert.All(deps, d => Assert.DoesNotContain("container", d.Ecosystem, StringComparison.Ordinal));
    }

    [Fact]
    public void Should_ScanRpmTextLayers_When_RpmDbPresent()
    {
        // Text rpm layers route to RpmParser; the top layer wins for the same
        // package via overlay overwrite; both rpm suffixes are recognized.
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    ["var/lib/rpm/Packages"] = ImageFixtureBuilder.RpmPackages("overlay-tool-1.0-1.x86_64"),
                },
                new Dictionary<string, string>
                {
                    ["var/lib/rpm/Packages"] = ImageFixtureBuilder.RpmPackages(
                        "overlay-tool-2.0-1.x86_64",
                        "overlay-other-3.0-2.noarch"),
                    ["usr/lib/sysimage/rpm/Packages"] = ImageFixtureBuilder.RpmPackages("sysimage-tool-9.9-9.x86_64"),
                },
            });

        var deps = parser.Parse(path);
        var rpm = deps.Where(d => d.Ecosystem == "rpm").ToDictionary(d => d.Name, d => d.Version);

        Assert.Equal(3, rpm.Count);
        Assert.Equal("2.0-1", rpm["overlay-tool"]);
        Assert.Equal("3.0-2", rpm["overlay-other"]);
        Assert.Equal("9.9-9", rpm["sysimage-tool"]);
    }

    [Fact]
    public void Should_ReturnEmptyRpm_When_RpmLayerIsBinary()
    {
        // Binary BerkeleyDB bytes yield [], never throw (deferred to #70) —
        // extends (not replaces) the neighbor-passthrough coverage above.
        var parser = ParserTestHelpers.ResolveParser("container");
        var path = ImageFixtureBuilder.BuildDockerImageTar(
            NewImagePath(),
            new[]
            {
                new Dictionary<string, string>
                {
                    ["var/lib/rpm/Packages"] = "\0binary\0rpm\0bytes\0",
                },
            });

        var deps = parser.Parse(path);

        Assert.Empty(deps);
    }
}
