using System.Text.Json;
using Olaf.Tests.Parsers;

namespace Olaf.Tests.Cli;

/// <summary>
/// CLI end-to-end for container-image scans (issue #64): image tarball scan,
/// standalone apk/dpkg with --ecosystem, rpm rejection (reserved for #65),
/// non-image/corrupt-tar exit 2, --max-image-mb validation + cap exit 2.
/// Subprocess e2e via <see cref="CliTestHelpers.RunCli"/>; fixtures are
/// synthetic <c>TarWriter</c> builds in temp dirs (no network).
/// </summary>
public sealed class ContainerImageCliTests
{
    private static string BuildDockerImage(IReadOnlyList<IReadOnlyDictionary<string, string>> layers, string fileName = "image.tar")
    {
        var dir = CliTestHelpers.CreateTempDir();
        return ImageFixtureBuilder.BuildDockerImageTar(Path.Combine(dir, fileName), layers);
    }

    private static IReadOnlyDictionary<string, string> ApkLayer(params (string Name, string Version)[] packages)
    {
        return new Dictionary<string, string>
        {
            ["lib/apk/db/installed"] = ImageFixtureBuilder.ApkInstalled(packages),
        };
    }

    private static IReadOnlyDictionary<string, string> RpmLayer(params string[] nvra)
    {
        return new Dictionary<string, string>
        {
            ["var/lib/rpm/Packages"] = ImageFixtureBuilder.RpmPackages(nvra),
        };
    }

    [Fact]
    public void Should_ExitZero_When_DockerImageTar()
    {
        var image = BuildDockerImage(new[] { ApkLayer(("cli-musl", "1.2.5-r0")) });
        try
        {
            var result = CliTestHelpers.RunCli("--input", image, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("cli-musl", result.Stdout, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(Path.GetDirectoryName(image)!);
        }
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemApkStandalone()
    {
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "installed",
            ImageFixtureBuilder.ApkInstalled(("cli-busybox", "1.36.1-r0")));
        try
        {
            var result = CliTestHelpers.RunCli("--input", path, "--format", "json", "--ecosystem", "apk");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("cli-busybox", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemDpkgStandalone()
    {
        var path = ImageFixtureBuilder.WriteTempTextFile(
            "status",
            ImageFixtureBuilder.DpkgStatus(("cli-bash", "5.2-5")));
        try
        {
            var result = CliTestHelpers.RunCli("--input", path, "--format", "json", "--ecosystem", "dpkg");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("cli-bash", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void Should_ExitZero_When_EcosystemRpmStandalone()
    {
        // RPM text-dump parsing shipped in #65: --ecosystem rpm is accepted.
        var path = CliTestHelpers.FixturePath("rpm", "Packages");
        var result = CliTestHelpers.RunCli("--input", path, "--format", "json", "--ecosystem", "rpm");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("bash", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Exit2_When_NonImageTar()
    {
        var dir = CliTestHelpers.CreateTempDir();
        var path = ImageFixtureBuilder.BuildNonImageTar(Path.Combine(dir, "plain.tar"));
        try
        {
            var result = CliTestHelpers.RunCli("--input", path, "--format", "json");

            Assert.Equal(2, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stderr));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_Exit2_When_CorruptTar()
    {
        var dir = CliTestHelpers.CreateTempDir();
        var path = ImageFixtureBuilder.BuildCorruptTar(
            Path.Combine(dir, "corrupt.tar"),
            new Dictionary<string, string>
            {
                ["lib/apk/db/installed"] = ImageFixtureBuilder.ApkInstalled(("truncated", "1.0-r0")),
            });
        try
        {
            var result = CliTestHelpers.RunCli("--input", path, "--format", "json");

            Assert.Equal(2, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stderr));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_Exit2_When_CapExceeded()
    {
        // Fractional megabytes are accepted: 0.000001 MB ≈ 1 byte, far below
        // any synthetic image, so the cap trips deterministically.
        var image = BuildDockerImage(new[] { ApkLayer(("capped", "1.0-r0")) });
        try
        {
            var result = CliTestHelpers.RunCli("--input", image, "--format", "json", "--max-image-mb", "0.000001");

            Assert.Equal(2, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stderr));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(Path.GetDirectoryName(image)!);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Should_Exit2_When_MaxImageMbInvalid(string value)
    {
        var image = BuildDockerImage(new[] { ApkLayer(("flag-guard", "1.0-r0")) });
        try
        {
            var result = CliTestHelpers.RunCli("--input", image, "--format", "json", "--max-image-mb", value);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("--max-image-mb", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(Path.GetDirectoryName(image)!);
        }
    }

    [Fact]
    public void Should_Exit2_When_MaxImageMbMissingValue()
    {
        var image = BuildDockerImage(new[] { ApkLayer(("flag-guard", "1.0-r0")) });
        try
        {
            var result = CliTestHelpers.RunCli("--input", image, "--format", "json", "--max-image-mb");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("--max-image-mb", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(Path.GetDirectoryName(image)!);
        }
    }

    [Fact]
    public void Should_Exit2_When_GarbageBytesAsTar()
    {
        // Non-tar bytes with a .tar name must fail loudly (exit 2), never scan as empty.
        var dir = CliTestHelpers.CreateTempDir();
        var path = ImageFixtureBuilder.WriteGarbage(Path.Combine(dir, "garbage.tar"));
        try
        {
            var result = CliTestHelpers.RunCli("--input", path, "--format", "json");

            Assert.Equal(2, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Stderr));
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_DocumentMaxImageMb_When_Help()
    {
        var result = CliTestHelpers.RunCli("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--max-image-mb", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ExitZero_When_ImageContainsRpmLayer()
    {
        var image = BuildDockerImage(new[] { RpmLayer("cli-rpm-tool-1.0-1.x86_64") });
        try
        {
            var result = CliTestHelpers.RunCli("--input", image, "--format", "json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("cli-rpm-tool", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            CliTestHelpers.DeleteTempDir(Path.GetDirectoryName(image)!);
        }
    }

    [Fact]
    public void Should_DocumentRpm_When_Help()
    {
        var result = CliTestHelpers.RunCli("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("rpm", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }
}
