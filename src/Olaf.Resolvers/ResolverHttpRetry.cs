using System.Net;

namespace Olaf.Resolvers;

internal static class ResolverHttpRetry
{
    private static bool IsTransientStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return code == 408 || code == 429 || (code >= 500 && code <= 599);
    }

    public static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        HttpResponseMessage first;
        try
        {
            first = await http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (e.g. HttpClient.Timeout): retry exactly once.
            return await http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            // Transient transport failure: retry exactly once.
            return await http.GetAsync(url, ct).ConfigureAwait(false);
        }

        if (!IsTransientStatus(first.StatusCode))
        {
            return first;
        }

        first.Dispose();
        return await http.GetAsync(url, ct).ConfigureAwait(false);
    }

    public static async Task<HttpResponseMessage> GetAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        HttpResponseMessage first;
        try
        {
            first = await http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return await http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            return await http.GetAsync(url, ct).ConfigureAwait(false);
        }

        if (!IsTransientStatus(first.StatusCode))
        {
            return first;
        }

        first.Dispose();
        return await http.GetAsync(url, ct).ConfigureAwait(false);
    }
}
