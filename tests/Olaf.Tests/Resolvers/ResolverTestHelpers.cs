using System.Net;
using System.Reflection;
using System.Text;
using Olaf.Core;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// TDD Red helpers for license resolvers.
/// Test assembly compiles while Olaf.Resolvers is still empty:
/// resolution is via reflection, missing impl throws NotImplementedException
/// (the *right* Red failure, not a compile error).
/// Http is fully mocked via <see cref="StubHttpMessageHandler"/> — no live network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public int CallCount { get; private set; }

    public List<Uri?> RequestedUrls { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        RequestedUrls.Add(request.RequestUri);
        var response = _responder(request, cancellationToken);
        return Task.FromResult(response);
    }

    public static HttpResponseMessage Json(object? payload, HttpStatusCode status = HttpStatusCode.OK)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
    }

    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    public static HttpResponseMessage ServerError() => new(HttpStatusCode.InternalServerError);
}

internal static class ResolverTestHelpers
{
    internal static void EnsureResolversLoaded()
    {
        try
        {
            AppDomain.CurrentDomain.Load("Olaf.Resolvers");
        }
        catch
        {
        }
    }

    /// <summary>
    /// Resolve an ILicenseResolver for the given ecosystem by naming convention
    /// (type name contains "nuget"/"npm"/"pypi"/"pip"). Prefers constructors
    /// accepting HttpClient so tests inject the mocked client. Throws
    /// NotImplementedException when the resolver is missing (Red).
    /// </summary>
    internal static ILicenseResolver ResolveResolver(string ecosystem, HttpClient httpClient)
    {
        EnsureResolversLoaded();
        var candidates = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => typeof(ILicenseResolver).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .Where(t => 
                // Issue #23: prefer name-starts-with match, then substring fallback.
                // This prevents "go" from matching "CargoLicenseResolver" (Cargo contains "go").
                t.Name.StartsWith(ecosystem, StringComparison.OrdinalIgnoreCase)
                || (ecosystem.Equals("pypi", StringComparison.OrdinalIgnoreCase)
                    && t.Name.Contains("pip", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Fallback/caching decorators are matched by their own helpers below;
        // here we want the ecosystem-specific resolver only.
        candidates = candidates
            .Where(t => !t.Name.Contains("cach", StringComparison.OrdinalIgnoreCase)
                && !t.Name.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                && !t.Name.Contains("clearly", StringComparison.OrdinalIgnoreCase)
                && !t.Name.Contains("composite", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            throw new NotImplementedException($"No ILicenseResolver for '{ecosystem}' (missing resolver).");
        }

        foreach (var type in candidates)
        {
            var instance = TryCreateWithHttpClient(type, httpClient);
            if (instance is not null)
            {
                return instance;
            }
        }

        throw new NotImplementedException(
            $"No usable constructor for '{ecosystem}' resolver (expected injectable HttpClient).");
    }

    /// <summary>
    /// Resolve a fallback/composite resolver (registry + ClearlyDefined) if present,
    /// otherwise fall back to the ecosystem resolver. Throws NotImplementedException
    /// when neither exists.
    /// </summary>
    internal static ILicenseResolver ResolveFallbackResolver(string ecosystem, HttpClient httpClient)
    {
        EnsureResolversLoaded();
        var decorators = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => typeof(ILicenseResolver).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .Where(t => t.Name.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains("clearly", StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains("composite", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var type in decorators)
        {
            var instance = TryCreateWithHttpClient(type, httpClient);
            if (instance is not null)
            {
                return instance;
            }
        }

        // No dedicated fallback type yet — use the ecosystem resolver so the
        // fallback-behavior tests still exercise the right Red path.
        return ResolveResolver(ecosystem, httpClient);
    }

    /// <summary>
    /// Resolve a caching resolver if present, otherwise the ecosystem resolver.
    /// Cache-hit tests assert HttpClient is called once for two identical resolves.
    /// </summary>
    internal static ILicenseResolver ResolveCachingResolver(string ecosystem, HttpClient httpClient)
    {
        EnsureResolversLoaded();
        var decorators = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => typeof(ILicenseResolver).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .Where(t => t.Name.Contains("cach", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var type in decorators)
        {
            var instance = TryCreateWithHttpClient(type, httpClient);
            if (instance is not null)
            {
                return instance;
            }
        }

        return ResolveResolver(ecosystem, httpClient);
    }

    internal static HttpClient CreateClient(StubHttpMessageHandler handler)
    {
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    private static ILicenseResolver? TryCreateWithHttpClient(Type type, HttpClient httpClient)
    {
        // Prefer (HttpClient), then any ctor starting with HttpClient (e.g. + IClock/options),
        // then parameterless. Fill remaining args with null/default.
        foreach (var ctor in type.GetConstructors().OrderBy(c => c.GetParameters().Length))
        {
            var parameters = ctor.GetParameters();
            if (parameters.Length == 0)
            {
                try
                {
                    return Activator.CreateInstance(type) as ILicenseResolver;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is NotImplementedException)
                {
                    throw ex.InnerException;
                }
                catch
                {
                    continue;
                }
            }

            if (parameters[0].ParameterType == typeof(HttpClient))
            {
                var args = new object?[parameters.Length];
                args[0] = httpClient;
                for (var i = 1; i < parameters.Length; i++)
                {
                    args[i] = DefaultFor(parameters[i].ParameterType);
                }

                try
                {
                    return Activator.CreateInstance(type, args) as ILicenseResolver;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is NotImplementedException)
                {
                    throw ex.InnerException;
                }
                catch
                {
                    continue;
                }
            }
        }

        return null;
    }

    private static object? DefaultFor(Type type)
    {
        if (!type.IsValueType)
        {
            return null;
        }

        return Activator.CreateInstance(type);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
    }
}
