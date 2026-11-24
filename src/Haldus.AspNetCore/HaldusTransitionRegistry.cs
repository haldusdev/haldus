using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus;

/// <summary>
/// Üretilen kodun, her geçiş için endpoint eşleyicisini kaydettiği yer. Geçişin durum kontrolü, kaydetmesi
/// ve yanıtı derleme zamanında üretilir; <c>MapTransition</c> çağrısı onu çalışma zamanında bağlar ve varsa
/// lambda'yı ona verir.
/// </summary>
public static class HaldusTransitionRegistry
{
    private static readonly ConcurrentDictionary<(Type Entity, Type? Request, string Name), Delegate> Mappers = new();

    /// <summary>Gövdesiz geçiş.</summary>
    public static void Register<TEntity>(
        string name,
        Func<IEndpointRouteBuilder, Func<TEntity, IServiceProvider, CancellationToken, Task>?, RouteHandlerBuilder> map)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(map);
        Mappers[(typeof(TEntity), null, name)] = map;
    }

    /// <summary>İstek gövdesi alan geçiş.</summary>
    /// <param name="name">Çağrıda açıkça verilen ad; verilmediyse <c>null</c>.</param>
    public static void Register<TEntity, TRequest>(
        string? name,
        Func<IEndpointRouteBuilder, Func<TEntity, TRequest, IServiceProvider, CancellationToken, Task>?, RouteHandlerBuilder> map)
        where TEntity : class
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(map);
        Mappers[(typeof(TEntity), typeof(TRequest), name ?? string.Empty)] = map;
    }

    /// <summary>
    /// İş mantığı ne istek tipinde ne de lambda'da ise bir handler sınıfı gerekir; eksikse hata ilk istekte
    /// değil, uygulama açılırken verilir.
    /// </summary>
    public static void RequireHandler<TEntity, TRequest>(IEndpointRouteBuilder endpoints)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (endpoints.ServiceProvider.GetService<IServiceProviderIsService>() is { } services
            && !services.IsService(typeof(ITransitionHandler<TEntity, TRequest>)))
        {
            throw new InvalidOperationException(
                $"MapTransition<{typeof(TRequest).Name}> needs its logic, but '{typeof(TRequest).Name}' does not implement " +
                $"ITransition<{typeof(TEntity).Name}>, no lambda was passed and no '{typeof(ITransitionHandler<TEntity, TRequest>)}' is registered. " +
                "Add a class that implements the handler (Haldus registers it automatically), implement ITransition on the request, or pass a lambda.");
        }
    }

    /// <summary>Kendi sonucunu döndüren entity aksiyonu.</summary>
    public static void Register<TEntity, TRequest, TResponse>(
        string? name,
        Func<IEndpointRouteBuilder, Func<TEntity, TRequest, IServiceProvider, CancellationToken, Task<TResponse>>?, RouteHandlerBuilder> map)
        where TEntity : class
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(map);
        Mappers[(typeof(TEntity), typeof(TRequest), name ?? string.Empty)] = map;
    }

    /// <summary>Entity aksiyonunun mantığı ne istek tipinde ne de lambda'da ise bir handler sınıfı gerekir.</summary>
    public static void RequireActionHandler<TEntity, TRequest>(IEndpointRouteBuilder endpoints)
        where TEntity : class =>
        RequireService<IEntityActionHandler<TEntity, TRequest>>(
            endpoints, $"MapAction<{typeof(TRequest).Name}>", $"implement IEntityAction<{typeof(TEntity).Name}> on the request");

    /// <inheritdoc cref="RequireActionHandler{TEntity, TRequest}"/>
    public static void RequireActionHandler<TEntity, TRequest, TResponse>(IEndpointRouteBuilder endpoints)
        where TEntity : class =>
        RequireService<IEntityActionHandler<TEntity, TRequest, TResponse>>(
            endpoints, $"MapAction<{typeof(TRequest).Name}, {typeof(TResponse).Name}>", alternative: null);

    private static void RequireService<THandler>(IEndpointRouteBuilder endpoints, string call, string? alternative)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (endpoints.ServiceProvider.GetService<IServiceProviderIsService>() is { } services
            && !services.IsService(typeof(THandler)))
        {
            throw new InvalidOperationException(
                $"{call} needs its logic, but no lambda was passed and no '{typeof(THandler)}' is registered. " +
                "Add a class that implements the handler (Haldus registers it automatically)" +
                (alternative is null ? string.Empty : $", {alternative}") + " or pass a lambda.");
        }
    }

    internal static RouteHandlerBuilder Map<TEntity, TRequest, TResponse>(
        IEndpointRouteBuilder group,
        string? name,
        Func<TEntity, TRequest, IServiceProvider, CancellationToken, Task<TResponse>>? apply)
        where TEntity : class
        where TRequest : class
    {
        return Mappers.TryGetValue((typeof(TEntity), typeof(TRequest), name ?? string.Empty), out var map)
            && map is Func<IEndpointRouteBuilder, Func<TEntity, TRequest, IServiceProvider, CancellationToken, Task<TResponse>>?, RouteHandlerBuilder> typed
                ? typed(group, apply)
                : throw NotGenerated<TEntity>($"MapAction<{typeof(TRequest).Name}, {typeof(TResponse).Name}>(...)");
    }

    internal static RouteHandlerBuilder Map<TEntity>(
        IEndpointRouteBuilder group,
        string name,
        Func<TEntity, IServiceProvider, CancellationToken, Task>? apply)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return Mappers.TryGetValue((typeof(TEntity), null, name), out var map)
            ? ((Func<IEndpointRouteBuilder, Func<TEntity, IServiceProvider, CancellationToken, Task>?, RouteHandlerBuilder>)map)(group, apply)
            : throw NotGenerated<TEntity>($"MapTransition(\"{name}\", ...)");
    }

    internal static RouteHandlerBuilder Map<TEntity, TRequest>(
        IEndpointRouteBuilder group,
        string? name,
        Func<TEntity, TRequest, IServiceProvider, CancellationToken, Task>? apply)
        where TEntity : class
        where TRequest : class
    {
        return Mappers.TryGetValue((typeof(TEntity), typeof(TRequest), name ?? string.Empty), out var map)
            ? ((Func<IEndpointRouteBuilder, Func<TEntity, TRequest, IServiceProvider, CancellationToken, Task>?, RouteHandlerBuilder>)map)(group, apply)
            : throw NotGenerated<TEntity>($"MapTransition<{typeof(TRequest).Name}>(...)");
    }

    private static InvalidOperationException NotGenerated<TEntity>(string call) => new(
        $"No Haldus endpoint was generated for '{typeof(TEntity).FullName}' and {call}. " +
        "The name must be a constant, a transition needs Workflow<TStatus>() on the resource, and the Haldus source generator must be referenced by the calling project.");
}
