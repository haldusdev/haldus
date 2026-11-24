using System.Linq.Expressions;

namespace Haldus;

/// <summary>
/// Bir liste endpoint'inin sorgu parametrelerinden doldurulan filtre. Herkese açık, yazılabilir skaler
/// (veya skaler dizisi) özellikleri sorgu parametresi olur; <see cref="Apply"/> onları sorguya çevirir.
/// </summary>
public interface IFilter<TEntity>
    where TEntity : class
{
    IQueryable<TEntity> Apply(IQueryable<TEntity> query);
}

public static class HaldusQueryableExtensions
{
    /// <summary>Koşul yalnızca <paramref name="condition"/> doğruysa uygulanır.</summary>
    public static IQueryable<T> WhereIf<T>(this IQueryable<T> query, bool condition, Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(query);
        return condition ? query.Where(predicate) : query;
    }
}
