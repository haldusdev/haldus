using System.Collections.Concurrent;
using Microsoft.AspNetCore.Routing;

namespace Haldus;

/// <summary>Üretilen kodun, her entity için endpoint eşleyicisini kaydettiği yer.</summary>
public static class HaldusEndpointRegistry
{
    private static readonly ConcurrentDictionary<Type, Func<IEndpointRouteBuilder, RouteGroupBuilder>> Mappers = new();

    public static void Register<TEntity>(Func<IEndpointRouteBuilder, RouteGroupBuilder> map) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(map);
        Mappers[typeof(TEntity)] = map;
    }

    internal static RouteGroupBuilder Map<TEntity>(IEndpointRouteBuilder endpoints, string archetype) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (!Mappers.TryGetValue(typeof(TEntity), out var map))
        {
            throw new InvalidOperationException(
                $"No Haldus endpoints were generated for '{typeof(TEntity).FullName}'. " +
                $"Make sure the Haldus source generator is referenced by the project that calls {archetype}<{typeof(TEntity).Name}>().");
        }

        return map(endpoints);
    }
}
