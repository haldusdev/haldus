using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>Zamanlanmış yayının (<c>.ScheduledPublishing()</c>) bakım sırasında kullandığı sorgular.</summary>
public static class HaldusScheduledPublishing
{
    /// <summary>Bir seferde okunan taslak sayısı.</summary>
    public const int BatchSize = 100;

    /// <summary>Bir bakım turunda bir kaynaktan denenen en çok taslak.</summary>
    public const int MaxPerRun = 1000;

    /// <summary>
    /// Yayın zamanı gelmiş taslaklar, zamanı en eski olan başta. Bakım bir isteğe ait değildir: kiracı ve sahiplik
    /// filtreleri bilerek kaldırılır, yoksa hiçbir taslak görünmezdi. Taslağı gerçekten yayınlayan kod onu kendi
    /// kiracısı ve sahibi adına, filtrelerden geçirerek yeniden okur.
    /// </summary>
    /// <param name="skip">
    /// Bu turda denenip yayınlanamamış (hâlâ zamanı gelmiş görünen) taslakların sayısı. Onlar atlanır ki sürekli
    /// başarısız olan birkaç taslak sıranın başını tutup ötekileri bekletmesin.
    /// </param>
    public static Task<List<TEntity>> DueAsync<TEntity>(
        DbContext context,
        Expression<Func<TEntity, bool>> due,
        Expression<Func<TEntity, DateTimeOffset?>> scheduledAt,
        int skip,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Set<TEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(due)
            .OrderBy(scheduledAt)
            .Skip(Math.Max(skip, 0))
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Bir taslağın zamanlamasını düşürür. Zamanı gelmiş ama artık kendi filtrelerinden geçemeyen taslak (silinmiş
    /// sayılan, üst kaydı görünmeyen) hiçbir zaman yayınlanamaz; zamanlaması kalsaydı her turda yeniden denenirdi.
    /// </summary>
    public static Task<int> ClearAsync<TEntity>(
        DbContext context,
        Expression<Func<TEntity, bool>> match,
        Expression<Func<TEntity, DateTimeOffset?>> scheduledAt,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Set<TEntity>()
            .IgnoreQueryFilters()
            .Where(match)
            .ExecuteUpdateAsync(setters => setters.SetProperty(scheduledAt, (DateTimeOffset?)null), cancellationToken);
    }
}