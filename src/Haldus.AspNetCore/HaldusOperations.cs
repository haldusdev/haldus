using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus;

/// <summary>
/// Entity'ye bağlı olmayan işlemler. <c>MapAction</c> yan etkili bir komutu <c>POST</c> ile,
/// <c>MapQuery</c> yan etkisiz bir hesabı <c>GET</c> ile sunar. İş mantığı ya bir handler sınıfında
/// (bağımlılıkları constructor'dan alır) ya da kısa işler için doğrudan verilen bir lambda'dadır.
/// </summary>
public static class HaldusOperationExtensions
{
    /// <summary>Komutu, kayıtlı <see cref="IActionHandler{TRequest, TResponse}"/> ile eşler.</summary>
    public static RouteHandlerBuilder MapAction<TRequest, TResponse>(this IEndpointRouteBuilder endpoints, string? route = null)
        where TRequest : class
    {
        RequireHandler<IActionHandler<TRequest, TResponse>>(endpoints, nameof(MapAction));

        return endpoints.MapAction<TRequest, TResponse>(
            static (request, services, cancellationToken) => services
                .GetRequiredService<IActionHandler<TRequest, TResponse>>()
                .HandleAsync(request, cancellationToken),
            route);
    }

    public static RouteHandlerBuilder MapAction<TRequest, TResponse>(
        this IEndpointRouteBuilder endpoints,
        Func<TRequest, CancellationToken, Task<TResponse>> handler,
        string? route = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return endpoints.MapAction<TRequest, TResponse>((request, _, cancellationToken) => handler(request, cancellationToken), route);
    }

    /// <summary>Lambda, bağımlılıklarını isteğin servis sağlayıcısından alır.</summary>
    public static RouteHandlerBuilder MapAction<TRequest, TResponse>(
        this IEndpointRouteBuilder endpoints,
        Func<TRequest, IServiceProvider, CancellationToken, Task<TResponse>> handler,
        string? route = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(handler);

        return endpoints
            .MapPost(
                HaldusOperationNaming.Route<TRequest>(route),
                async (TRequest request, HttpContext httpContext, CancellationToken cancellationToken) =>
                    TypedResults.Ok(await handler(request, httpContext.RequestServices, cancellationToken)))
            .WithName(HaldusOperationNaming.Name<TRequest>())
            .AddEndpointFilter<HaldusValidationFilter<TRequest>>()
            .ProducesValidationProblem();
    }

    /// <summary>Sonuç döndürmeyen komutu, kayıtlı <see cref="IActionHandler{TRequest}"/> ile eşler.</summary>
    public static RouteHandlerBuilder MapAction<TRequest>(this IEndpointRouteBuilder endpoints, string? route = null)
        where TRequest : class
    {
        RequireHandler<IActionHandler<TRequest>>(endpoints, nameof(MapAction));

        return endpoints.MapAction<TRequest>(
            static (request, services, cancellationToken) => services
                .GetRequiredService<IActionHandler<TRequest>>()
                .HandleAsync(request, cancellationToken),
            route);
    }

    public static RouteHandlerBuilder MapAction<TRequest>(
        this IEndpointRouteBuilder endpoints,
        Func<TRequest, CancellationToken, Task> handler,
        string? route = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return endpoints.MapAction<TRequest>((request, _, cancellationToken) => handler(request, cancellationToken), route);
    }

    public static RouteHandlerBuilder MapAction<TRequest>(
        this IEndpointRouteBuilder endpoints,
        Func<TRequest, IServiceProvider, CancellationToken, Task> handler,
        string? route = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(handler);

        return endpoints
            .MapPost(
                HaldusOperationNaming.Route<TRequest>(route),
                async (TRequest request, HttpContext httpContext, CancellationToken cancellationToken) =>
                {
                    await handler(request, httpContext.RequestServices, cancellationToken);
                    return TypedResults.NoContent();
                })
            .WithName(HaldusOperationNaming.Name<TRequest>())
            .AddEndpointFilter<HaldusValidationFilter<TRequest>>()
            .ProducesValidationProblem();
    }

    /// <summary>
    /// Hesabı, kayıtlı <see cref="IQueryHandler{TRequest, TResponse}"/> ile eşler. İstek tipinin herkese
    /// açık, yazılabilir skaler özellikleri camelCase sorgu parametresi olur.
    /// </summary>
    public static RouteHandlerBuilder MapQuery<TRequest, TResponse>(this IEndpointRouteBuilder endpoints, string? route = null)
        where TRequest : class
    {
        RequireHandler<IQueryHandler<TRequest, TResponse>>(endpoints, nameof(MapQuery));

        return endpoints.MapQuery<TRequest, TResponse>(
            static (request, services, cancellationToken) => services
                .GetRequiredService<IQueryHandler<TRequest, TResponse>>()
                .HandleAsync(request, cancellationToken),
            route);
    }

    public static RouteHandlerBuilder MapQuery<TRequest, TResponse>(
        this IEndpointRouteBuilder endpoints,
        Func<TRequest, CancellationToken, Task<TResponse>> handler,
        string? route = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return endpoints.MapQuery<TRequest, TResponse>((request, _, cancellationToken) => handler(request, cancellationToken), route);
    }

    /// <summary>Lambda, bağımlılıklarını isteğin servis sağlayıcısından alır.</summary>
    public static RouteHandlerBuilder MapQuery<TRequest, TResponse>(
        this IEndpointRouteBuilder endpoints,
        Func<TRequest, IServiceProvider, CancellationToken, Task<TResponse>> handler,
        string? route = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(handler);

        // İsteğin sorgu parametrelerinden doldurulması ve doğrulanması derleme zamanında üretilir.
        return HaldusQueryRegistry
            .Map(endpoints, HaldusOperationNaming.Route<TRequest>(route), handler)
            .WithName(HaldusOperationNaming.Name<TRequest>());
    }

    /// <summary>Handler eksikse hata ilk istekte değil, uygulama açılırken verilir.</summary>
    private static void RequireHandler<THandler>(IEndpointRouteBuilder endpoints, string method)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (endpoints.ServiceProvider.GetService<IServiceProviderIsService>() is { } services
            && !services.IsService(typeof(THandler)))
        {
            throw new InvalidOperationException(
                $"{method} needs a handler, but no '{typeof(THandler)}' is registered. " +
                "Add a class that implements it (Haldus registers it automatically) or pass the logic as a lambda.");
        }
    }
}

/// <summary>Üretilen kodun, her sorgu isteği için endpoint eşleyicisini kaydettiği yer.</summary>
public static class HaldusQueryRegistry
{
    private static readonly ConcurrentDictionary<(Type Request, Type Response), Delegate> Mappers = new();

    public static void Register<TRequest, TResponse>(
        Func<IEndpointRouteBuilder, string, Func<TRequest, IServiceProvider, CancellationToken, Task<TResponse>>, RouteHandlerBuilder> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        Mappers[(typeof(TRequest), typeof(TResponse))] = map;
    }

    internal static RouteHandlerBuilder Map<TRequest, TResponse>(
        IEndpointRouteBuilder endpoints,
        string route,
        Func<TRequest, IServiceProvider, CancellationToken, Task<TResponse>> handler)
    {
        if (!Mappers.TryGetValue((typeof(TRequest), typeof(TResponse)), out var map))
        {
            throw new InvalidOperationException(
                $"No Haldus query endpoint was generated for '{typeof(TRequest).FullName}'. " +
                "Make sure the Haldus source generator is referenced by the project that calls MapQuery.");
        }

        return ((Func<IEndpointRouteBuilder, string, Func<TRequest, IServiceProvider, CancellationToken, Task<TResponse>>, RouteHandlerBuilder>)map)(
            endpoints, route, handler);
    }
}

/// <summary>Üretilen kodun, bulduğu handler sınıflarını bağımlılık enjeksiyonuna eklediği yer.</summary>
public static class HaldusServiceRegistry
{
    private static readonly ConcurrentDictionary<Assembly, Action<IServiceCollection>> Contributions = new();

    public static void Register(Assembly module, Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(configure);
        Contributions[module] = configure;
    }

    internal static void Apply(IServiceCollection services)
    {
        foreach (var configure in Contributions.Values)
        {
            configure(services);
        }
    }
}

internal static class HaldusOperationNaming
{
    private static readonly string[] Suffixes = ["Request", "Command", "Query"];

    /// <summary><c>ShippingQuoteRequest</c> → <c>ShippingQuote</c>.</summary>
    public static string Name<TRequest>()
    {
        var name = typeof(TRequest).Name;
        var suffix = Suffixes.FirstOrDefault(candidate =>
            name.Length > candidate.Length && name.EndsWith(candidate, StringComparison.Ordinal));

        return suffix is null ? name : name[..^suffix.Length];
    }

    /// <summary>Verilmediyse rota istek tipinin adından türetilir: <c>ShippingQuoteRequest</c> → <c>/shipping-quote</c>.</summary>
    public static string Route<TRequest>(string? route)
    {
        if (!string.IsNullOrWhiteSpace(route))
        {
            return route.StartsWith('/') ? route : "/" + route;
        }

        var name = Name<TRequest>();
        var builder = new System.Text.StringBuilder("/");

        for (var index = 0; index < name.Length; index++)
        {
            if (char.IsUpper(name[index]) && index > 0 && !char.IsUpper(name[index - 1]))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(name[index]));
        }

        return builder.ToString();
    }
}
