using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Issue #70 B2: enrichment ONLY from already-fetched payloads (NO-NEW-HTTP —
/// enrichment itself adds zero GETs). Issue #71 adds ONE license-text GET
/// (tarball via DownloadUrl, then licenseUrl) for npm/nuget/pypi/cargo, so
/// those four single-GET pins became two GETs (registry + tarball); the
/// rest still assert a single GET. YES group (npm/nuget/pypi/cargo/
/// composer/maven) harvests hashes+supplier+download; PARTIAL (bundler/go)
/// harvests what its endpoint carries (hashes null); NULL group
/// (swift/cocoapods/vcpkg/conan) returns null; ClearlyDefined is YES-trivial.
/// Absent fields stay null (never ""/fake); SourceUrl is never copied to
/// DownloadUrl. All HTTP stubbed via <see cref="StubHttpMessageHandler"/> —
/// no live network; inline JSON, no checked-in fixtures.
/// </summary>
public sealed class EnrichmentResolverTests
{
    [Fact]
    public async Task Should_HarvestHashesSupplierDownload_When_NpmRegistryJson()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                license = "MIT",
                dist = new
                {
                    integrity = "sha512-abc123==",
                    tarball = "https://registry.npmjs.org/express/-/express-4.18.2.tgz",
                },
                author = new { name = "TJ Holowaychuk" },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);
        var dep = new Dependency("npm", "express", "4.18.2", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(2, handler.CallCount); // #71: registry + tarball license-text GET
        Assert.NotNull(result.Enrichment);
        Assert.Equal("pkg:npm/express@4.18.2", result.Enrichment.Purl);
        Assert.Equal(new[] { "sha512:abc123==" }, result.Enrichment.Hashes);
        Assert.Equal("TJ Holowaychuk", result.Enrichment.Supplier);
        Assert.Equal("https://registry.npmjs.org/express/-/express-4.18.2.tgz", result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_HarvestHashSupplierDownload_When_NuGetRegistrationJson()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                licenseExpression = "MIT",
                packageHash = "abc123",
                packageHashAlgorithm = "SHA-512",
                authors = "Microsoft",
                packageContent = "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.1/newtonsoft.json.13.0.1.nupkg",
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NuGetLicenseResolver(http);
        var dep = new Dependency("nuget", "Newtonsoft.Json", "13.0.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(2, handler.CallCount); // #71: registry + tarball license-text GET
        Assert.NotNull(result.Enrichment);
        Assert.Equal("pkg:nuget/Newtonsoft.Json@13.0.1", result.Enrichment.Purl);
        // Algorithm passes through lowercased (hyphen preserved here;
        // SBOM formatters normalize to SHA-512/SHA512 at emit time).
        Assert.Equal(new[] { "sha-512:abc123" }, result.Enrichment.Hashes);
        Assert.Equal("Microsoft", result.Enrichment.Supplier);
        Assert.Equal(
            "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.1/newtonsoft.json.13.0.1.nupkg",
            result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_HarvestDigestsAuthorFileUrl_When_PyPIJsonApi()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                info = new { license = "MIT", author = "Kenneth Reitz" },
                urls = new[]
                {
                    new
                    {
                        digests = new { sha256 = "deadbeef", md5 = "cafef00d" },
                        url = "https://files.pythonhosted.org/packages/requests-2.31.0-py3-none-any.whl",
                    },
                },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new PyPILicenseResolver(http);
        var dep = new Dependency("pypi", "requests", "2.31.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(2, handler.CallCount); // #71: registry + tarball license-text GET
        Assert.NotNull(result.Enrichment);
        Assert.Equal("pkg:pypi/requests@2.31.0", result.Enrichment.Purl);
        Assert.Equal(new[] { "sha256:deadbeef", "md5:cafef00d" }, result.Enrichment.Hashes);
        Assert.Equal("Kenneth Reitz", result.Enrichment.Supplier);
        Assert.Equal("https://files.pythonhosted.org/packages/requests-2.31.0-py3-none-any.whl", result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_HarvestChecksumDownload_When_CratesJson()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                version = new
                {
                    license = "MIT",
                    checksum = "deadbeef1234",
                    dl_path = "/api/v1/crates/serde/1.0.0/download",
                },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new CargoLicenseResolver(http);
        var dep = new Dependency("cargo", "serde", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(2, handler.CallCount); // #71: registry + tarball license-text GET
        Assert.NotNull(result.Enrichment);
        Assert.Equal("pkg:cargo/serde@1.0.0", result.Enrichment.Purl);
        Assert.Equal(new[] { "sha256:deadbeef1234" }, result.Enrichment.Hashes);
        Assert.Null(result.Enrichment.Supplier);
        Assert.Equal("https://crates.io/api/v1/crates/serde/1.0.0/download", result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_HarvestShasumAuthorsDistUrl_When_PackagistJson()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                packages = new Dictionary<string, object>
                {
                    ["monolog/monolog"] = new[]
                    {
                        new
                        {
                            version = "3.5.0",
                            license = new[] { "MIT" },
                            dist = new
                            {
                                shasum = "abc123def",
                                url = "https://api.github.com/repos/Seldaek/monolog/zipball/abc123",
                            },
                            authors = new[] { new { name = "Jordi Boggiano" } },
                        },
                    },
                },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ComposerLicenseResolver(http);
        var dep = new Dependency("composer", "monolog/monolog", "3.5.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(1, handler.CallCount);
        Assert.NotNull(result.Enrichment);
        Assert.Equal("pkg:composer/monolog/monolog@3.5.0", result.Enrichment.Purl);
        Assert.Equal(new[] { "sha1:abc123def" }, result.Enrichment.Hashes);
        Assert.Equal("Jordi Boggiano", result.Enrichment.Supplier);
        Assert.Equal("https://api.github.com/repos/Seldaek/monolog/zipball/abc123", result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_HarvestOrganizationProjectUrl_When_MavenPomXml()
    {
        const string pom = """
            <project>
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.example</groupId>
              <artifactId>lib</artifactId>
              <version>1.0.0</version>
              <licenses>
                <license>
                  <name>Apache License, Version 2.0</name>
                  <url>https://www.apache.org/licenses/LICENSE-2.0</url>
                </license>
              </licenses>
              <organization>
                <name>Example Org</name>
              </organization>
              <url>https://example.com/lib</url>
            </project>
            """;
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text(pom));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "com.example:lib", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal(1, handler.CallCount);
        Assert.NotNull(result.Enrichment);
        Assert.Equal("pkg:maven/com.example/lib@1.0.0", result.Enrichment.Purl);
        Assert.Null(result.Enrichment.Hashes);
        Assert.Equal("Example Org", result.Enrichment.Supplier);
        Assert.Equal("https://repo1.maven.org/maven2/com/example/lib/1.0.0/lib-1.0.0.jar", result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_ConstructCanonicalJar_When_PomHasHomepageUrl()
    {
        // Issue #169 (A1): the POM project <url> is a homepage, never the jar.
        const string pom = """
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
              <organization>
                <name>Google</name>
              </organization>
              <url>https://github.com/google/guava</url>
            </project>
            """;
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text(pom));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "com.google.guava:guava", "32.1.2-jre", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal(1, handler.CallCount);
        Assert.Single(handler.RequestedUrls);
        Assert.NotNull(result.Enrichment);
        Assert.Equal("https://repo1.maven.org/maven2/com/google/guava/guava/32.1.2-jre/guava-32.1.2-jre.jar", result.Enrichment.DownloadUrl);
        Assert.Equal("Google", result.Enrichment.Supplier);
    }

    [Fact]
    public async Task Should_ConstructCanonicalJar_When_EcosystemIsGradle()
    {
        // Issue #169 (A2): shared resolver serves the gradle label identically.
        const string pom = """
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
              <url>https://github.com/google/guava</url>
            </project>
            """;
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text(pom));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("gradle", "com.google.guava:guava", "32.1.2-jre", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal(1, handler.CallCount);
        Assert.Single(handler.RequestedUrls);
        Assert.NotNull(result.Enrichment);
        Assert.Equal("https://repo1.maven.org/maven2/com/google/guava/guava/32.1.2-jre/guava-32.1.2-jre.jar", result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_NotUseHomepage_When_PomUrlIsPresent()
    {
        // Issue #169 (B1): value-pinned jar entails homepage absence; the
        // explicit NotEqual is belt-and-braces — both must agree.
        const string pom = """
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
              <url>https://github.com/google/guava</url>
            </project>
            """;
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text(pom));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "com.google.guava:guava", "32.1.2-jre", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.NotNull(result.Enrichment);
        Assert.Equal("https://repo1.maven.org/maven2/com/google/guava/guava/32.1.2-jre/guava-32.1.2-jre.jar", result.Enrichment.DownloadUrl);
        Assert.NotEqual("https://github.com/google/guava", result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_EscapeSegments_When_CoordinatesNeedEncoding()
    {
        // Issue #169 (C1): dotted group splits to path; artifact + version
        // segments are EscapeDataString-encoded.
        const string pom = """
            <project>
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.example.sub</groupId>
              <artifactId>lib-core</artifactId>
              <version>1.0+build 2</version>
              <licenses>
                <license>
                  <name>Apache License, Version 2.0</name>
                  <url>https://www.apache.org/licenses/LICENSE-2.0</url>
                </license>
              </licenses>
            </project>
            """;
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text(pom));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new MavenLicenseResolver(http);
        var dep = new Dependency("maven", "com.example.sub:lib-core", "1.0+build 2", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.NotNull(result.Enrichment);
        Assert.Equal("https://repo1.maven.org/maven2/com/example/sub/lib-core/1.0%2Bbuild%202/lib-core-1.0%2Bbuild%202.jar", result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_HarvestSupplierDownloadWithoutHashes_When_RubyGemsJson()
    {
        // PARTIAL: versioned sha lives on an unfetched endpoint -> Hashes null.
        // Issue #167 (B): DownloadUrl is constructed canonical from the LOCKED
        // dependency, never lifted verbatim from gem_uri (which names latest).
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                name = "rails",
                licenses = new[] { "MIT" },
                authors = "David Heinemeier Hansson",
                gem_uri = "https://rubygems.org/gems/rails-7.0.8.gem",
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new BundlerLicenseResolver(http);
        var dep = new Dependency("bundler", "rails", "7.0.8", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(1, handler.CallCount); // NO-NEW-HTTP: enrichment adds zero GETs.
        Assert.Single(handler.RequestedUrls); // single metadata GET, never a download-URL fetch.
        Assert.NotNull(result.Enrichment);
        Assert.Equal("pkg:gem/rails@7.0.8", result.Enrichment.Purl);
        Assert.Null(result.Enrichment.Hashes);
        Assert.Equal("David Heinemeier Hansson", result.Enrichment.Supplier);
        Assert.Equal("https://rubygems.org/downloads/rails-7.0.8.gem", result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_ConstructZipDownloadWithoutHashes_When_GoProxyInfo()
    {
        // PARTIAL: module zip URL constructed, never fetched -> DownloadUrl;
        // .ziphash unfetched -> Hashes null; author null.
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { License = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new GoLicenseResolver(http);
        var dep = new Dependency("go", "example.com/mod", "v1.2.3", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(1, handler.CallCount);
        Assert.NotNull(result.Enrichment);
        Assert.Equal("pkg:golang/example.com/mod@v1.2.3", result.Enrichment.Purl);
        Assert.Null(result.Enrichment.Hashes);
        Assert.Null(result.Enrichment.Supplier);
        Assert.Equal("https://proxy.golang.org/example.com/mod/@v/v1.2.3.zip", result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_HarvestFileShaAndParty_When_ClearlyDefinedDefinitionsJson()
    {
        // YES-trivial: files[].sha (first sha256) -> Hashes; parties[] -> Supplier.
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                licensed = new
                {
                    declared = "MIT",
                    parties = new[] { new { name = "Jane Doe" } },
                },
                files = new[] { new { path = "package/LICENSE", sha256 = "deadbeef" } },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ClearlyDefinedFallbackResolver(http);
        var dep = new Dependency("npm", "express", "4.18.2", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(1, handler.CallCount);
        Assert.NotNull(result.Enrichment);
        Assert.Equal(new[] { "sha256:deadbeef" }, result.Enrichment.Hashes);
        Assert.Equal("Jane Doe", result.Enrichment.Supplier);
        Assert.Null(result.Enrichment.DownloadUrl);
    }

    [Fact]
    public async Task Should_ReturnNullEnrichment_When_NullGroupEcosystemResolves()
    {
        // NULL group: swift endpoint lacks enrichment data.
        var swiftHandler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { license = new { spdx_id = "MIT" } }));
        using var swiftHttp = ResolverTestHelpers.CreateClient(swiftHandler);
        var swiftResult = await new SwiftLicenseResolver(swiftHttp)
            .ResolveAsync(new Dependency("swift", "owner/repo", "1.0.0", false));

        Assert.Equal("MIT", swiftResult.SpdxId);
        Assert.Null(swiftResult.Enrichment);
    }

    [Fact]
    public async Task Should_ReturnNullEnrichment_When_EnrichmentFieldsAbsent()
    {
        // Absent -> null (never "" or fabricated values).
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { license = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);

        var result = await resolver.ResolveAsync(new Dependency("npm", "express", "4.18.2", false));

        Assert.Equal("MIT", result.SpdxId);
        Assert.Null(result.Enrichment);
    }

    [Fact]
    public async Task Should_NeverCopySourceUrl_When_DownloadUrlAbsent()
    {
        // Registry page != download URI: author present but no dist.tarball ->
        // DownloadUrl stays null even though SourceUrl is set.
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                license = "MIT",
                author = "Some Author",
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new NpmLicenseResolver(http);

        var result = await resolver.ResolveAsync(new Dependency("npm", "express", "4.18.2", false));

        Assert.Equal("MIT", result.SpdxId);
        Assert.NotNull(result.Enrichment);
        Assert.Equal("Some Author", result.Enrichment.Supplier);
        Assert.Null(result.Enrichment.DownloadUrl);
        Assert.NotNull(result.SourceUrl);
        Assert.NotEqual(result.SourceUrl, result.Enrichment.DownloadUrl);
    }
}
