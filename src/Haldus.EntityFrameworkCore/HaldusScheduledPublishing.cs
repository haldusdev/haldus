using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>Zamanlanmış yayının (<c>.ScheduledPublishing()</c>) bakım sırasında kullandığı sorgu.</summary>
public static class HaldusScheduledPublishing
{
    /// <summary>Bir bakım turunda bir kaynaktan yayınlanan en çok taslak.</summary>
    public const int BatchSize = 100;

    /// <summary>
    /// Yayın zamanı gelmiş taslaklar, zamanı en eski olan başta. Bakım bir isteğe ait değildir: kiracı ve sahiplik
    /// filtreleri bilerek kaldırılır, yoksa hiçbir taslak görünmezdi. Taslağı gerçekten yayınlayan kod onu kendi
    /// kiracısı ve sahibi adına, filtrelerden geçirerek yeniden okur.
    /// </summary>
    public static Task<List<TEntity>> DueAsync<TEntity>(
        DbContext context,
        Expression<Func<TEntity, bool>> due,
        Expression<Func<TEntity, DateTimeOffset?>> scheduledAt,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Set<TEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(due)
            .OrderBy(scheduledAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
    }
}