using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// E2E tests: real network calls to Maven Central (https://repo1.maven.org/maven2).
/// Uses real HttpClient (not StubHttpMessageHandler). Marked with
/// [Trait("Category", "E2E")] so CI can filter with
/// --filter "Category!=E2E" to keep unit tests fast.
/// Artifacts are pinned versions with stable, well-known licenses.
/// Shared client E2ETestHelpers.CreateRealClient (no User-Agent needed).
/// The shared MavenLicenseResolver serves both "maven" and "gradle" labels.
/// </summary>
public sealed class MavenResolverE2ETests
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveGuava_Apache2_When_RealCentralCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "com.google.guava:guava", "32.1.2-jre", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("mvnrepository.com", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveSlf4j_MIT_When_RealCentralCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "org.slf4j:slf4j-api", "2.0.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.SourceUrl);
        Assert.NotNull(result.LicenseText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveJunit_EPL_When_RealCentralCall()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "junit:junit", "4.13.2", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("EPL-1.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.LicenseText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ResolveGuava_When_EcosystemIsGradle()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("gradle", "com.google.guava:guava", "32.1.2-jre", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_ArtifactNotFound()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "org.example:this-artifact-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Should_ReturnUnknown_When_VersionDoesNotExist()
    {
        using var http = E2ETestHelpers.CreateRealClient();
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "com.google.guava:guava", "999.999.999", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
