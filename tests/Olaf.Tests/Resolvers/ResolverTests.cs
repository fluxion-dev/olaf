using System.Net;
using Olaf.Core;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// TDD Red: license resolvers with fully mocked HttpClient (no live network).
/// Resolvers are resolved via reflection; missing impl throws NotImplementedException
/// (the right Red failure). Contract under test:
///   ILicenseResolver.ResolveAsync(Dependency, CancellationToken) -> ResolvedLicense
///   success: SpdxId set, Status == "Resolved"; failure: Status == "Unknown" + Reason.
/// No resolver logic is implemented here — tests only.
/// </summary>
public sealed class NuGetResolverTests
{
    [Fact]
    public async Task Should_ResolveSpdx_When_NuspecHasLicenseExpression()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                licenseExpression = "MIT",
                licenseUrl = (string?)null,
                packageContent = "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg",
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("nuget", http);
        var dep = new Dependency("nuget", "Newtonsoft.Json", "13.0.3", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
    }

    [Fact]
    public async Task Should_MapSpdx_When_LegacyLicenseUrl()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                licenseExpression = (string?)null,
                licenseUrl = "https://opensource.org/licenses/Apache-2.0",
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("nuget", http);
        var dep = new Dependency("nuget", "LegacyPkg", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    public async Task Should_EmbedLicenseText_When_RegistryProvidesTextOrUrl()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("license", StringComparison.OrdinalIgnoreCase))
            {
                return StubHttpMessageHandler.Text("MIT License\nPermission is hereby granted, free of charge,");
            }

            return StubHttpMessageHandler.Json(new
            {
                licenseExpression = "MIT",
                licenseUrl = "https://example.com/license.txt",
            });
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("nuget", http);
        var dep = new Dependency("nuget", "Newtonsoft.Json", "13.0.3", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_NuGet404()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("nuget", http);
        var dep = new Dependency("nuget", "MissingPkg", "0.0.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class NpmResolverTests
{
    [Fact]
    public async Task Should_ResolveSpdx_When_RegistryLicenseString()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                name = "lodash",
                version = "4.17.21",
                license = "MIT",
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    public async Task Should_PreserveDualOrExpression_When_NpmDualLicensed()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                name = "dual-pkg",
                version = "1.0.0",
                license = "(MIT OR Apache-2.0)",
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "dual-pkg", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("(MIT OR Apache-2.0)", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    public async Task Should_EmbedLicenseText_When_NpmProvidesFullTextUrl()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            if (url.EndsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
                || url.Contains("license", StringComparison.OrdinalIgnoreCase) && url.StartsWith("https://example.com", StringComparison.OrdinalIgnoreCase))
            {
                return StubHttpMessageHandler.Text("MIT License\nPermission is hereby granted, free of charge,");
            }

            return StubHttpMessageHandler.Json(new
            {
                name = "lodash",
                version = "4.17.21",
                license = "MIT",
            });
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_Npm404()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "missing-pkg", "0.0.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
    }
}

public sealed class PyPIResolverTests
{
    [Fact]
    public async Task Should_ResolveSpdx_When_PyPILicenseAndClassifiers()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                info = new
                {
                    name = "requests",
                    version = "2.31.0",
                    license = "Apache 2.0",
                    classifiers = new[] { "License :: OSI Approved :: Apache Software License" },
                },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "requests", "2.31.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    public async Task Should_EmbedLicenseText_When_PyPIProvidesText()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                info = new
                {
                    name = "requests",
                    version = "2.31.0",
                    license = "MIT",
                    classifiers = new[] { "License :: OSI Approved :: MIT License" },
                },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "requests", "2.31.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_PyPI404()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "missing-pkg", "0.0.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
    }
}

public sealed class FallbackResolverTests
{
    [Fact]
    public async Task Should_FallbackToClearlyDefined_When_RegistryMissing()
    {
        var handler = new StubHttpMessageHandler((req, ct) =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("clearlydefined", StringComparison.OrdinalIgnoreCase))
            {
                return StubHttpMessageHandler.Json(new
                {
                    licensed = new { declared = "MIT" },
                });
            }

            return StubHttpMessageHandler.NotFound();
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveFallbackResolver("npm", http);
        var dep = new Dependency("npm", "fallback-pkg", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("clearlydefined", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_BothRegistryAndClearlyDefinedMissing()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveFallbackResolver("npm", http);
        var dep = new Dependency("npm", "missing-pkg", "0.0.1", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
    }
}

public sealed class CacheResolverTests
{
    [Fact]
    public async Task Should_HitCache_When_SameDependencyResolvedTwice()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                name = "lodash",
                version = "4.17.21",
                license = "MIT",
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveCachingResolver("npm", http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var first = await resolver.ResolveAsync(dep);
        var second = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", first.SpdxId);
        Assert.Equal("MIT", second.SpdxId);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnCached_When_OfflineModeAndCacheHit()
    {
        var onlineHandler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                licenseExpression = "MIT",
            }));
        using var onlineHttp = ResolverTestHelpers.CreateClient(onlineHandler);
        var onlineResolver = ResolverTestHelpers.ResolveCachingResolver("nuget", onlineHttp);
        var dep = new Dependency("nuget", "Newtonsoft.Json", "13.0.3", false);

        var online = await onlineResolver.ResolveAsync(dep);
        Assert.Equal("MIT", online.SpdxId);

        // Offline: transport fails. A TTL/file-cache-backed resolver must still
        // serve the previously cached entry instead of throwing.
        var offlineHandler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException("offline mode: no network"));
        using var offlineHttp = ResolverTestHelpers.CreateClient(offlineHandler);
        var offlineResolver = ResolverTestHelpers.ResolveCachingResolver("nuget", offlineHttp);

        var offline = await offlineResolver.ResolveAsync(dep);

        Assert.Equal("MIT", offline.SpdxId);
        Assert.Equal("Resolved", offline.Status);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_OfflineAndNoCacheHit()
    {
        var offlineHandler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException("offline mode: no network"));
        using var http = ResolverTestHelpers.CreateClient(offlineHandler);
        var resolver = ResolverTestHelpers.ResolveCachingResolver("npm", http);
        var dep = new Dependency("npm", "never-cached", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
    }
}

public sealed class IsolationResolverTests
{
    [Fact]
    public async Task Should_NotAbortBatch_When_OneDependencyFails()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("bad-pkg", StringComparison.OrdinalIgnoreCase))
            {
                return StubHttpMessageHandler.ServerError();
            }

            return StubHttpMessageHandler.Json(new
            {
                name = "good-pkg",
                version = "1.0.0",
                license = "MIT",
            });
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var bad = new Dependency("npm", "bad-pkg", "1.0.0", false);
        var good = new Dependency("npm", "good-pkg", "1.0.0", false);

        var badResult = await resolver.ResolveAsync(bad);
        var goodResult = await resolver.ResolveAsync(good);

        Assert.Equal("Unknown", badResult.Status);
        Assert.Equal("MIT", goodResult.SpdxId);
        Assert.Equal("Resolved", goodResult.Status);
    }

    [Fact]
    public async Task Should_ReturnUnknownAndContinue_When_TimeoutOr500()
    {
        var handler = new StubHttpMessageHandler((req, ct) =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("timeout-pkg", StringComparison.OrdinalIgnoreCase))
            {
                throw new TaskCanceledException("simulated timeout");
            }

            return StubHttpMessageHandler.ServerError();
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);

        var timeoutResult = await resolver.ResolveAsync(new Dependency("npm", "timeout-pkg", "1.0.0", false));
        var errorResult = await resolver.ResolveAsync(new Dependency("npm", "error-pkg", "1.0.0", false));

        Assert.Equal("Unknown", timeoutResult.Status);
        Assert.Null(timeoutResult.SpdxId);
        Assert.Equal("Unknown", errorResult.Status);
        Assert.Null(errorResult.SpdxId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NOASSERTION")]
    public async Task Should_ReturnUnknown_When_LicenseNullOrEmpty(string? license)
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                name = "empty-license-pkg",
                version = "1.0.0",
                license,
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "empty-license-pkg", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.True(result.SpdxId is null || result.SpdxId == "NOASSERTION");
    }
}
