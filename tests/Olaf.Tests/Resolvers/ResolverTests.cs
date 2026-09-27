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

    [Fact]
    public async Task Should_ReturnUnknown_When_OfflineTransportFails()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException("offline mode: no network"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "requests", "2.31.0", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("transport-error", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount); // retried exactly once
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_PyPI500()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.ServerError());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "requests", "2.31.0", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("registry-error", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount); // 500 is transient: retried exactly once
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_PyPIBodyMalformed()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text("this is not json{{{"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "requests", "2.31.0", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("parse-error", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_PyPIHasNoUsableLicense()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                info = new
                {
                    name = "requests",
                    version = "2.31.0",
                    license = "",
                    classifiers = Array.Empty<string>(),
                },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "requests", "2.31.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Should_ResolveSpdx_When_PyPIProvidesLicenseExpression()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                info = new
                {
                    name = "expr-pkg",
                    version = "1.0.0",
                    license = "",
                    license_expression = "MIT",
                    classifiers = Array.Empty<string>(),
                },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pypi", "expr-pkg", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    public async Task Should_ResolveSpdx_When_PipAliasEcosystem()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new
            {
                info = new
                {
                    name = "requests",
                    version = "2.31.0",
                    license = "MIT",
                    classifiers = Array.Empty<string>(),
                },
            }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("pypi", http);
        var dep = new Dependency("pip", "requests", "2.31.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
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

/// <summary>
/// Phase D1: unknown-name offline must resolve to Unknown (never throw).
/// Handler throws HttpRequestException("offline"); the caching resolver has no
/// cached entry for npm/never-cached@9.9.9, so it must return Unknown with a
/// reason mentioning the offline miss (offline-cache-miss via the caching
/// decorator, or transport-error: offline bubbled from the inner resolver).
/// Unknown results are never cached, so the fixed name stays deterministic.
/// </summary>
public sealed class OfflineUnknownResolverTests
{
    [Fact]
    public async Task Should_ReturnUnknownWithOfflineReason_When_OfflineAndNoCacheHit()
    {
        var offlineHandler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException("offline"));
        using var http = ResolverTestHelpers.CreateClient(offlineHandler);
        var resolver = ResolverTestHelpers.ResolveCachingResolver("npm", http);
        var dep = new Dependency("npm", "never-cached", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.True(
            result.Reason.Contains("offline-cache-miss", StringComparison.OrdinalIgnoreCase)
                || result.Reason.Contains("offline", StringComparison.OrdinalIgnoreCase)
                || result.Reason.Contains("not-found", StringComparison.OrdinalIgnoreCase),
            $"Reason should mention offline-cache-miss/offline/not-found but was: {result.Reason}");
    }
}

/// <summary>
/// Phase D2: a throwing transport must surface as Unknown, never crash the
/// caller. Primary resolvers map InvalidOperationException to
/// "parse-error: ..." (Reason always non-null and echoes the message);
/// the caching/fallback decorators map unexpected errors to "resolver-error: ...".
/// </summary>
public sealed class ResolverErrorIsolationTests
{
    [Theory]
    [InlineData("nuget")]
    [InlineData("npm")]
    [InlineData("pypi")]
    public async Task Should_ReturnUnknown_When_ResolverThrows(string ecosystem)
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new InvalidOperationException("boom"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver(ecosystem, http);
        var dep = new Dependency(ecosystem, "any-pkg", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep); // must not throw

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("boom", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Phase D3: cooperative cancellation must propagate as
/// OperationCanceledException (never swallowed into Unknown). The stub honors
/// the token like a real transport would; resolvers rethrow when the caller's
/// token is canceled.
/// </summary>
public sealed class CancellationResolverTests
{
    private static StubHttpMessageHandler TokenHonoringHandler()
    {
        return new StubHttpMessageHandler((req, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return StubHttpMessageHandler.Json(new
            {
                name = "cancel-pkg",
                version = "1.0.0",
                license = "MIT",
            });
        });
    }

    [Theory]
    [InlineData("nuget")]
    [InlineData("npm")]
    [InlineData("pypi")]
    public async Task Should_ThrowOperationCanceled_When_TokenCanceled_Primary(string ecosystem)
    {
        using var http = ResolverTestHelpers.CreateClient(TokenHonoringHandler());
        var resolver = ResolverTestHelpers.ResolveResolver(ecosystem, http);
        var dep = new Dependency(ecosystem, "cancel-pkg", "1.0.0", false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(dep, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Should_ThrowOperationCanceled_When_TokenCanceled_Fallback()
    {
        using var http = ResolverTestHelpers.CreateClient(TokenHonoringHandler());
        var resolver = ResolverTestHelpers.ResolveFallbackResolver("npm", http);
        var dep = new Dependency("npm", "cancel-pkg", "1.0.0", false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(dep, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Should_ThrowOperationCanceled_When_TokenCanceled_Caching()
    {
        using var http = ResolverTestHelpers.CreateClient(TokenHonoringHandler());
        var resolver = ResolverTestHelpers.ResolveCachingResolver("npm", http);
        var dep = new Dependency("npm", "cancel-pkg-cached", "1.0.0", false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(dep, new CancellationToken(canceled: true)));
    }
}

/// <summary>
/// Phase D4: the shared retry helper retries exactly once. Fail-once
/// (HttpRequestException then valid payload) resolves; persistent 500 stays
/// Unknown. Both cases issue exactly 2 HTTP calls.
/// </summary>
public sealed class RetryResolverTests
{
    [Fact]
    public async Task Should_ResolveMit_When_TransientFailureThenSuccess()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("transient transport failure");
            }

            return StubHttpMessageHandler.Json(new
            {
                name = "retry-pkg",
                version = "1.0.0",
                license = "MIT",
            });
        });
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "retry-pkg", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Resolved", result.Status);
        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_Always500()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.ServerError());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = ResolverTestHelpers.ResolveResolver("npm", http);
        var dep = new Dependency("npm", "error-pkg", "1.0.0", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.Equal(2, handler.CallCount);
    }
}

/// <summary>
/// Regression test for issue #23: ResolverTestHelpers.ResolveResolver("go")
/// must resolve GoLicenseResolver, NOT CargoLicenseResolver via substring collision.
/// </summary>
public sealed class GoResolveHelperTests
{
    [Fact]
    public async Task Should_ResolveGoEcosystem_WithoutSubstringCollision()
    {
        // Arrange: stub a Go proxy response
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(new { License = "MIT" }));
        using var http = ResolverTestHelpers.CreateClient(handler);
        // Act: resolve via the helper (was returning CargoLicenseResolver before fix)
        var resolver = ResolverTestHelpers.ResolveResolver("go", http);
        var dep = new Dependency("go", "github.com/spf13/cobra", "v1.8.0", false);

        var result = await resolver.ResolveAsync(dep);

        // Assert: GoLicenseResolver resolves successfully, not CargoLicenseResolver
        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("pkg.go.dev", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }
}
