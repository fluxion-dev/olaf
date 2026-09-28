using Olaf.Core;

namespace Olaf.Tests;

/// <summary>
/// Issue #70 B4: `PurlBuilder.Build` is the single Package-URL home shared by
/// resolvers (`Enrichment.Purl`) and formatters (`CycloneDxPurl` delegates —
/// subsume, not fork). Offline unit style, no network. Vectors pin every
/// mapping row: npm scope `%40`, pypi+pip alias, golang alias, maven+gradle
/// colon split + bare fallback, nuget, cargo, gem alias, composer, swift,
/// cocoapods, vcpkg, conan, apk/dpkg/rpm + unmapped generic fallback,
/// qualifiers emitted vs null/empty omitted (formatters pass null for now).
/// </summary>
public sealed class PurlBuilderTests
{
    [Fact]
    public void Should_EncodeScope_When_NpmScopedName()
    {
        Assert.Equal("pkg:npm/%40scope/name@1.0.0", PurlBuilder.Build("npm", "@scope/name", "1.0.0"));
        Assert.Equal("pkg:npm/express@4.18.2", PurlBuilder.Build("npm", "express", "4.18.2"));
    }

    [Fact]
    public void Should_MapPypiType_When_PipOrPypiEcosystem()
    {
        Assert.Equal("pkg:pypi/requests@2.31.0", PurlBuilder.Build("pip", "requests", "2.31.0"));
        Assert.Equal("pkg:pypi/requests@2.31.0", PurlBuilder.Build("pypi", "requests", "2.31.0"));
    }

    [Fact]
    public void Should_MapGolangType_When_GoOrGolangEcosystem()
    {
        Assert.Equal("pkg:golang/github.com/foo/bar@1.2.3", PurlBuilder.Build("go", "github.com/foo/bar", "1.2.3"));
        Assert.Equal("pkg:golang/github.com/foo/bar@1.2.3", PurlBuilder.Build("golang", "github.com/foo/bar", "1.2.3"));
    }

    [Fact]
    public void Should_SplitGroupArtifact_When_MavenOrGradleCoordinates()
    {
        Assert.Equal("pkg:maven/org.example/artifact@2.0.0", PurlBuilder.Build("maven", "org.example:artifact", "2.0.0"));
        Assert.Equal("pkg:maven/org.example/plugin@3.0.0", PurlBuilder.Build("gradle", "org.example:plugin", "3.0.0"));
    }

    [Fact]
    public void Should_FallbackBareName_When_MavenWithoutColon()
    {
        Assert.Equal("pkg:maven/bare-artifact@1.0.0", PurlBuilder.Build("maven", "bare-artifact", "1.0.0"));
    }

    [Fact]
    public void Should_MapRegistryTypes_When_NuGetOrCargo()
    {
        Assert.Equal("pkg:nuget/Newtonsoft.Json@13.0.1", PurlBuilder.Build("nuget", "Newtonsoft.Json", "13.0.1"));
        Assert.Equal("pkg:cargo/serde@1.0.0", PurlBuilder.Build("cargo", "serde", "1.0.0"));
    }

    [Fact]
    public void Should_MapGemAndComposerTypes_When_BundlerOrComposer()
    {
        Assert.Equal("pkg:gem/rails@7.0.8", PurlBuilder.Build("bundler", "rails", "7.0.8"));
        Assert.Equal("pkg:gem/rails@7.0.8", PurlBuilder.Build("gem", "rails", "7.0.8"));
        Assert.Equal("pkg:composer/monolog/monolog@3.5.0", PurlBuilder.Build("composer", "monolog/monolog", "3.5.0"));
    }

    [Fact]
    public void Should_MapNativeTypes_When_SwiftCocoapodsVcpkgConan()
    {
        Assert.Equal("pkg:swift/owner/repo@1.0.0", PurlBuilder.Build("swift", "owner/repo", "1.0.0"));
        Assert.Equal("pkg:cocoapods/AFNetworking@4.0.1", PurlBuilder.Build("cocoapods", "AFNetworking", "4.0.1"));
        Assert.Equal("pkg:vcpkg/fmt@10.2.1", PurlBuilder.Build("vcpkg", "fmt", "10.2.1"));
        Assert.Equal("pkg:conan/fmt@10.2.1", PurlBuilder.Build("conan", "fmt", "10.2.1"));
    }

    [Fact]
    public void Should_FallbackGeneric_When_EcosystemUnmapped()
    {
        Assert.Equal("pkg:generic/bash@5.2", PurlBuilder.Build("apk", "bash", "5.2"));
        Assert.Equal("pkg:generic/bash@5.2", PurlBuilder.Build("dpkg", "bash", "5.2"));
        Assert.Equal("pkg:generic/bash@5.2", PurlBuilder.Build("rpm", "bash", "5.2"));
        Assert.Equal("pkg:generic/mystery@9.9.9", PurlBuilder.Build("mystery-eco", "mystery", "9.9.9"));
        Assert.Equal("pkg:generic/mystery@9.9.9", PurlBuilder.Build(null, "mystery", "9.9.9"));
    }

    [Fact]
    public void Should_EmitQualifierSuffixOnly_When_QualifiersNonEmpty()
    {
        var qualifiers = new Dictionary<string, string?> { ["arch"] = "x64" };

        Assert.Equal(
            "pkg:npm/express@4.18.2?arch=x64",
            PurlBuilder.Build("npm", "express", "4.18.2", qualifiers));
        Assert.Equal(
            "pkg:npm/express@4.18.2",
            PurlBuilder.Build("npm", "express", "4.18.2", null));
        Assert.Equal(
            "pkg:npm/express@4.18.2",
            PurlBuilder.Build("npm", "express", "4.18.2", new Dictionary<string, string?>()));
    }
}
