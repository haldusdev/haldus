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

    private static readonly ConcurrentDictionary<(Type Left, Type Right), Func<IEndpointRouteBuilder, RouteGroupBuilder>> Associations = new();

    public static void RegisterAssociation<TLeft, TRight>(Func<IEndpointRouteBuilder, RouteGroupBuilder> map)
        where TLeft : class
        where TRight : class
    {
        ArgumentNullException.ThrowIfNull(map);
        Associations[(typeof(TLeft), typeof(TRight))] = map;
    }

    internal static RouteGroupBuilder MapAssociation<TLeft, TRight>(IEndpointRouteBuilder endpoints)
        where TLeft : class
        where TRight : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (!Associations.TryGetValue((typeof(TLeft), typeof(TRight)), out var map))
        {
            throw new InvalidOperationException(
                $"No Haldus association was generated for '{typeof(TLeft).Name}' and '{typeof(TRight).Name}'. " +
                "Both must be mapped with a Map call in the project that calls MapAssociation.");
        }

        return map(endpoints);
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
