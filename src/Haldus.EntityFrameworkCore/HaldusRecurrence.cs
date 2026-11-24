using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>Bir serinin tek bir oluşunun kuraldan sapması: ertelendi veya iptal edildi.</summary>
public sealed class HaldusOccurrenceException
{
    public long Id { get; set; }

    /// <summary>Serinin kaynağı: entity'nin tam tip adı.</summary>
    public string Resource { get; set; } = "";

    public string RecordId { get; set; } = "";

    /// <summary>Kuralın verdiği zaman (UTC).</summary>
    public DateTimeOffset OriginalStart { get; set; }

    /// <summary>Ertelendiyse yeni zaman.</summary>
    public DateTimeOffset? Start { get; set; }

    public bool Cancelled { get; set; }
}

/// <summary>
/// Yinelenen kayıtların (<c>.Recurring()</c>) oluşlarının hesaplanması ve istisnalarının saklanması. Oluşlar hiçbir
/// zaman saklanmaz: her okumada, istenen aralık için kuraldan hesaplanır ve istisnalarla birleştirilir.
/// </summary>
public static class HaldusRecurrence
{
    /// <summary>Bir istekte döndürülen en çok oluş.</summary>
    public const int MaxOccurrences = 500;

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusOccurrenceException>(entity =>
        {
            entity.ToTable("HaldusOccurrenceExceptions");
            entity.Property(item => item.Resource).HasMaxLength(400);
            entity.Property(item => item.RecordId).HasMaxLength(200);
            entity.HasIndex(item => new { item.Resource, item.RecordId, item.OriginalStart }).IsUnique();
        });
    }

    /// <summary>
    /// Kuralın, verilen aralığa düşen oluşlarını hesaplar. Her oluş ilk oluştan sayılarak bulunur (ayın 31'inde başlayan
    /// aylık seri şubatta ayın son gününe düşer, martta yine 31'ine döner).
    /// </summary>
    public static IEnumerable<DateTimeOffset> Expand(IRecurring record, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(record);

        var start = record.StartsAt;
        var interval = Math.Max(record.RecurrenceInterval ?? 1, 1);
        var limit = record.RecurrenceFrequency == RecurrenceFrequency.None ? 1 : record.RecurrenceCount ?? int.MaxValue;
        var until = record.RecurrenceUntil ?? DateTimeOffset.MaxValue;
        var end = to < until ? to : until;

        // Günlük ve haftalık serilerde aralığın başına doğrudan atlanır; uzun süredir süren bir seri baştan gezilmez.
        var step = record.RecurrenceFrequency switch
        {
            RecurrenceFrequency.Daily => TimeSpan.FromDays(interval),
            RecurrenceFrequency.Weekly => TimeSpan.FromDays(7L * interval),
            _ => TimeSpan.Zero,
        };

        var index = step > TimeSpan.Zero && from > start ? (int)Math.Min((from - start).Ticks / step.Ticks, int.MaxValue - 1) : 0;
        var produced = 0;

        for (; index < limit && produced < MaxOccurrences; index++)
        {
            DateTimeOffset occurrence;

            try
            {
                occurrence = record.RecurrenceFrequency switch
                {
                    RecurrenceFrequency.Daily => start.AddDays((double)index * interval),
                    RecurrenceFrequency.Weekly => start.AddDays(7d * index * interval),
                    RecurrenceFrequency.Monthly => start.AddMonths(checked(index * interval)),
                    RecurrenceFrequency.Yearly => start.AddYears(checked(index * interval)),
                    _ => start,
                };
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
            {
                // Takvimin sonuna gelindi.
                yield break;
            }

            if (occurrence > end)
            {
                yield break;
            }

            if (occurrence >= from)
            {
                produced++;
                yield return occurrence;
            }
        }
    }

    /// <summary>Serinin verilen aralıktaki oluşları, istisnalarıyla birlikte; zamana göre sıralı.</summary>
    public static async Task<List<Occurrence>> OccurrencesAsync<TEntity>(
        DbContext context,
        TEntity record,
        string recordId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
        where TEntity : class, IRecurring
    {
        ArgumentNullException.ThrowIfNull(context);

        var starts = Expand(record, from, to).ToList();

        if (starts.Count == 0)
        {
            return [];
        }

        var resource = typeof(TEntity).FullName!;
        var first = starts[0].ToUniversalTime();
        var last = starts[^1].ToUniversalTime();

        var exceptions = await context.Set<HaldusOccurrenceException>()
            .AsNoTracking()
            .Where(item => item.Resource == resource && item.RecordId == recordId && item.OriginalStart >= first && item.OriginalStart <= last)
            .ToDictionaryAsync(item => item.OriginalStart.UtcTicks, cancellationToken);

        return starts.ConvertAll(start => exceptions.TryGetValue(start.UtcTicks, out var exception)
            ? new Occurrence(start, exception.Start ?? start, exception.Cancelled)
            : new Occurrence(start, start, false));
    }

    /// <summary>
    /// Tek bir oluşu erteler veya iptal eder. Verilen zaman kuralın bir oluşu değilse <c>null</c> döner ve hiçbir şey
    /// yazılmaz. Değişiklik, kuraldan sapma kalmadıysa (zaman aynı, iptal değil) istisnayı kaldırır.
    /// </summary>
    public static async Task<Occurrence?> ChangeAsync<TEntity>(
        DbContext context,
        TEntity record,
        string recordId,
        DateTimeOffset originalStart,
        OccurrenceChange change,
        CancellationToken cancellationToken)
        where TEntity : class, IRecurring
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(change);

        if (!Expand(record, originalStart, originalStart).Any(start => start.UtcTicks == originalStart.UtcTicks))
        {
            return null;
        }

        var resource = typeof(TEntity).FullName!;
        var key = originalStart.ToUniversalTime();
        var existing = await context.Set<HaldusOccurrenceException>()
            .FirstOrDefaultAsync(item => item.Resource == resource && item.RecordId == recordId && item.OriginalStart == key, cancellationToken);

        var moved = change.Start is { } start && start.UtcTicks != originalStart.UtcTicks ? change.Start : null;

        if (moved is null && !change.Cancelled)
        {
            if (existing is not null)
            {
                context.Remove(existing);
            }
        }
        else
        {
            existing ??= context.Add(new HaldusOccurrenceException { Resource = resource, RecordId = recordId, OriginalStart = key }).Entity;
            existing.Start = moved?.ToUniversalTime();
            existing.Cancelled = change.Cancelled;
        }

        await context.SaveChangesAsync(cancellationToken);
        return new Occurrence(originalStart, moved ?? originalStart, change.Cancelled);
    }
}
