namespace Olaf.Tests.Resolvers;

/// <summary>
/// Shared factory for E2E tests that make real network calls.
/// Convention: E2E Facts use <c>using var http = E2ETestHelpers.CreateRealClient();</c>
/// instead of per-file <c>CreateRealClient</c> duplicates.
/// </summary>
internal static class E2ETestHelpers
{
    /// <summary>
    /// Creates a fresh real <see cref="HttpClient"/> for live E2E calls.
    /// </summary>
    /// <remarks>
    /// The caller owns disposal via <c>using var</c>.
    /// Pass <c>useUserAgent: true</c> for crates.io and api.github.com, which
    /// return 403 without a User-Agent header; all other E2E ecosystems
    /// (npm, Go, PyPI, NuGet, Maven Central, Packagist/RubyGems, vcpkg/Conan)
    /// need no User-Agent header.
    /// </remarks>
    internal static HttpClient CreateRealClient(bool useUserAgent = false)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (useUserAgent)
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "olaf-license-scanner");
        }

        return http;
    }
}
