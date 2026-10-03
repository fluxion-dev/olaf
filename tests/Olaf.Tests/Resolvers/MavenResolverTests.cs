using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Maven license resolver (shared by maven + gradle, issue #12).
/// Instantiated directly via `new MavenLicenseResolver(http)` — deliberately
/// NOT via ResolverTestHelpers.ResolveResolver("gradle"), which matches on
/// type-name-Contains and would miss the shared resolver.
/// All HTTP is stubbed (no live network); unknown/offline paths use phantom
/// coordinates that can never resolve.
/// </summary>
public sealed class MavenResolverTests
{
    private const string GuavaPom = """
        <project>
          <modelVersion>4.0.0</modelVersion>
          <groupId>com.google.guava</groupId>
          <artifactId>guava</artifactId>
          <version>32.1.2-jre</version>
          <licenses>
            <license>
              <name>Apache License, Version 2.0</name>
              <url>https://www.apache.org/licenses/LICENSE-2.0</url>
            </license>
          </licenses>
        </project>
        """;

    [Fact]
    public async Task Should_ResolveSpdx_When_CentralPomHasApacheLicense()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text(GuavaPom));
        using var http = ResolverTestHelpers.CreateClient(handler);
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
    public async Task Should_ResolveSpdx_When_EcosystemIsGradle()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text(GuavaPom));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("gradle", "com.google.guava:guava", "32.1.2-jre", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    public async Task Should_RequestCentralPom_When_CoordinatesGiven()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text(GuavaPom));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "com.google.guava:guava", "32.1.2-jre", false);

        await resolver.ResolveAsync(dep);

        Assert.NotEmpty(handler.RequestedUrls);
        var url = handler.RequestedUrls[0]?.ToString() ?? string.Empty;
        Assert.Contains("repo1.maven.org/maven2", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("com/google/guava/guava/32.1.2-jre/guava-32.1.2-jre.pom", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_FollowParentPom_When_ChildPomHasNoLicenses()
    {
        const string childPom = """
            <project>
              <modelVersion>4.0.0</modelVersion>
              <parent>
                <groupId>com.google.guava</groupId>
                <artifactId>guava-parent</artifactId>
                <version>32.1.2-jre</version>
              </parent>
              <artifactId>guava</artifactId>
            </project>
            """;
        var handler = new StubHttpMessageHandler((req, _) =>
            req.RequestUri?.ToString().Contains("guava-parent", StringComparison.Ordinal) == true
                ? StubHttpMessageHandler.Text(GuavaPom)
                : StubHttpMessageHandler.Text(childPom));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("gradle", "com.google.guava:guava", "32.1.2-jre", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Should_NameLockedChildJar_When_LicensesResolveViaParent()
    {
        // Issue #169 (A3): parent-walk rows name the LOCKED child
        // coordinates, never the parent — even when the parent POM carries
        // its own homepage <url>.
        const string childPom = """
            <project>
              <modelVersion>4.0.0</modelVersion>
              <parent>
                <groupId>com.google.guava</groupId>
                <artifactId>guava-parent</artifactId>
                <version>32.1.2-jre</version>
              </parent>
              <artifactId>guava</artifactId>
            </project>
            """;
        const string parentPom = """
            <project>
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.google.guava</groupId>
              <artifactId>guava-parent</artifactId>
              <version>32.1.2-jre</version>
              <licenses>
                <license>
                  <name>Apache License, Version 2.0</name>
                  <url>https://www.apache.org/licenses/LICENSE-2.0</url>
                </license>
              </licenses>
              <url>https://github.com/google/guava</url>
            </project>
            """;
        var handler = new StubHttpMessageHandler((req, _) =>
            req.RequestUri?.ToString().Contains("guava-parent", StringComparison.Ordinal) == true
                ? StubHttpMessageHandler.Text(parentPom)
                : StubHttpMessageHandler.Text(childPom));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("gradle", "com.google.guava:guava", "32.1.2-jre", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.Enrichment);
        Assert.Equal("https://repo1.maven.org/maven2/com/google/guava/guava/32.1.2-jre/guava-32.1.2-jre.jar", result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_ArtifactNotFound()
    {
        // Issue #169 (D1): 404 is Unknown with null enrichment, never throws.
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "com.example:this-artifact-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.Null(result.Enrichment);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_Central404()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "org.example:this-artifact-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_PomHasNoLicenses()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text("<project><modelVersion>4.0.0</modelVersion></project>"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("gradle", "org.example:this-artifact-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_PomIsMalformed()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text("this is not xml{{{"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "com.google.guava:guava", "32.1.2-jre", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_CoordinateNotGroupArtifact()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text(GuavaPom));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "not-a-coordinate", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_OfflineTransportFails()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException("offline mode: no network"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "org.example:this-artifact-definitely-does-not-exist-olaf-xyz", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("transport-error", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount); // retried exactly once
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_EcosystemMismatch()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text(GuavaPom));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("ecosystem-mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }
}
