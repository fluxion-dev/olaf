using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Conan (conan-center-index recipe data) license resolver. All HTTP is
/// stubbed (no live network); unknown/offline paths use phantom names.
/// Live shape verified 2026-10-03: recipes/{name}/config.yml maps versions
/// to folders, recipes/{name}/{folder}/conanfile.py declares
/// `license = "..."`.
/// </summary>
public sealed class ConanResolverTests
{
    private const string Config = "versions:\n  \"12.2.0\":\n    folder: all\n  \"11.0.2\":\n    folder: all\n";
    private const string RecipeMit = "from conan import ConanFile\n\nclass FmtConan(ConanFile):\n    name = \"fmt\"\n    license = \"MIT\"\n";

    private static HttpClient ClientFor(string config, string recipe)
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            return StubHttpMessageHandler.Text(url.Contains("config.yml") ? config : recipe);
        });
        return ResolverTestHelpers.CreateClient(handler);
    }

    [Fact]
    public async Task Should_ResolveSpdx_When_ConfigAndRecipeHaveLicense()
    {
        using var http = ClientFor(Config, RecipeMit);
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "fmt", "11.0.2", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
        Assert.Equal(dep, result.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(result.LicenseText));
        Assert.NotNull(result.SourceUrl);
        Assert.Contains("conan.io", result.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_QueryConfigThenRecipe_When_Resolving()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Text(req.RequestUri?.ToString()!.Contains("config.yml") == true ? Config : RecipeMit));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "fmt", "11.0.2", false);

        await resolver.ResolveAsync(dep);

        Assert.Equal(2, handler.CallCount);
        var first = handler.RequestedUrls[0]?.ToString() ?? string.Empty;
        var second = handler.RequestedUrls[1]?.ToString() ?? string.Empty;
        Assert.Contains("conan-center-index", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recipes/fmt/config.yml", first, StringComparison.Ordinal);
        Assert.Contains("recipes/fmt/all/conanfile.py", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_UseLatestFolder_When_VersionMissingFromConfig()
    {
        // Old pins (e.g. poco/1.9.4) no longer appear in config.yml; the
        // license attribute is folder-level, so latest-folder wins.
        using var http = ClientFor(Config, RecipeMit);
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "fmt", "9.9.9", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("MIT", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    public async Task Should_ResolveTupleFirst_When_LicenseIsTuple()
    {
        using var http = ClientFor(Config, "class C(ConanFile):\n    license = (\"Apache-2.0\", \"LLVM-exception\")\n");
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "fmt", "11.0.2", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Apache-2.0", result.SpdxId);
        Assert.Equal("Resolved", result.Status);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_RecipeNotFound()
    {
        var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "this-recipe-definitely-does-not-exist-olaf-xyz", "*", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("not-found", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_OfflineTransportFails()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new HttpRequestException("offline mode: no network"));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "this-recipe-definitely-does-not-exist-olaf-xyz", "*", false);

        var result = await resolver.ResolveAsync(dep);

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
            StubHttpMessageHandler.Text(RecipeMit));
        using var http = ResolverTestHelpers.CreateClient(handler);
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("npm", "lodash", "4.17.21", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("ecosystem-mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Should_ReturnUnknown_When_RecipeHasNoUsableLicense()
    {
        using var http = ClientFor(Config, "class C(ConanFile):\n    name = \"fmt\"\n");
        var resolver = new ConanLicenseResolver(http);
        var dep = new Dependency("conan", "fmt", "11.0.2", false);

        var result = await resolver.ResolveAsync(dep);

        Assert.Equal("Unknown", result.Status);
        Assert.Null(result.SpdxId);
        Assert.NotNull(result.Reason);
        Assert.Contains("license-unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Should_PickExactFolder_When_VersionListed()
    {
        var config = "versions:\n  \"3.6.5\":\n    folder: \"3.x.x\"\n  \"4.0.3\":\n    folder: \"4.x.x\"\n";

        Assert.Equal("3.x.x", ConanLicenseResolver.PickFolder(config, "3.6.5"));
    }

    [Fact]
    public void Should_PickLatestFolder_When_VersionUnlisted()
    {
        var config = "versions:\n  \"4.0.3\":\n    folder: \"4.x.x\"\n  \"3.6.5\":\n    folder: \"3.x.x\"\n";

        Assert.Equal("4.x.x", ConanLicenseResolver.PickFolder(config, "1.1.1k"));
    }
}
