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

    /// <summary>Hiç oluş vermeyen bir kuralın (ör. "ayın beşinci pazartesisi") en çok kaç dönem gezileceği.</summary>
    private const int MaxPeriods = 20_000;

    /// <summary>
    /// Kuralın, verilen aralığa düşen oluşlarını hesaplar. Oluşlar dönem dönem bulunur (gün, hafta, ay, yıl); her
    /// dönem ilk oluştan sayılır, bu yüzden ayın 31'inde başlayan aylık seri şubatta ayın son gününe düşer, martta
    /// yine 31'ine döner. Günler verildiyse (<see cref="IRecurring.RecurrenceByDay"/>) bir dönem birden çok oluş
    /// verebilir. Saat dilimi verildiyse oluşlar o dilimin duvar saatini korur.
    /// </summary>
    public static IEnumerable<DateTimeOffset> Expand(IRecurring record, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(record);

        var frequency = record.RecurrenceFrequency;
        var interval = Math.Max(record.RecurrenceInterval ?? 1, 1);
        var limit = frequency == RecurrenceFrequency.None ? 1 : record.RecurrenceCount ?? int.MaxValue;
        var until = record.RecurrenceUntil ?? DateTimeOffset.MaxValue;
        var end = to < until ? to : until;

        // Geçersiz bir gün listesi veya saat dilimi yazılamaz (doğrulanır); yine de bozuk bir kayıt okumayı düşürmesin.
        RecurrenceDays.TryParse(record.RecurrenceByDay, out var days);
        var zone = record.RecurrenceTimeZone is { Length: > 0 } zoneId && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found)
            ? found
            : null;

        // Hesap duvar saatiyle yapılır: dilim verildiyse o dilimde, verilmediyse ilk oluşun UTC farkında.
        var first = zone is null ? record.StartsAt.DateTime : TimeZoneInfo.ConvertTime(record.StartsAt, zone).DateTime;
        var offset = record.StartsAt.Offset;

        DateTimeOffset At(DateTime local)
        {
            if (zone is null)
            {
                return new DateTimeOffset(local, offset);
            }

            // Saatin ileri alındığı gece o duvar saati yoktur; oluş, saatin atladığı kadar ileri kayar.
            while (zone.IsInvalidTime(local))
            {
                local = local.AddMinutes(30);
            }

            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        // Dönem başına tam bir oluş varsa (gün listesi yok) dönemin sırası oluşun sırasıdır ve aralığın başına
        // doğrudan atlanabilir. Açık uçlu seride (sayı sınırı yok) sıra önemli olmadığı için yine atlanır. Günleri de
        // sayısı da olan seri baştan gezilir: sayı en çok 1000'dir.
        var single = days.Count == 0;
        var period = single || record.RecurrenceCount is null ? FirstPeriod(frequency, interval, record.StartsAt, from) : 0;
        var produced = single ? period : 0;
        var returned = 0;

        for (var visited = 0; visited < MaxPeriods && produced < limit && returned < MaxOccurrences; visited++, period++)
        {
            List<DateTime> candidates;

            try
            {
                candidates = Candidates(frequency, interval, first, days, period);
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
            {
                // Takvimin sonuna gelindi.
                yield break;
            }

            foreach (var local in candidates)
            {
                // Seri ilk oluştan önce başlamaz (ilk haftanın, ilk oluştan önceki günleri sayılmaz).
                if (local < first)
                {
                    continue;
                }

                var occurrence = At(local);

                if (occurrence > end)
                {
                    yield break;
                }

                if (produced >= limit)
                {
                    yield break;
                }

                produced++;

                if (occurrence >= from)
                {
                    returned++;
                    yield return occurrence;

                    if (returned >= MaxOccurrences)
                    {
                        yield break;
                    }
                }
            }

            if (frequency == RecurrenceFrequency.None)
            {
                yield break;
            }
        }
    }

    /// <summary>Aralığın başından önceki, güvenle atlanabilecek dönem sayısı (bir dönem pay bırakılır).</summary>
    private static int FirstPeriod(RecurrenceFrequency frequency, int interval, DateTimeOffset start, DateTimeOffset from)
    {
        if (from <= start)
        {
            return 0;
        }

        var elapsed = from - start;

        var periods = frequency switch
        {
            RecurrenceFrequency.Daily => elapsed.Ticks / TimeSpan.FromDays(interval).Ticks,
            RecurrenceFrequency.Weekly => elapsed.Ticks / TimeSpan.FromDays(7L * interval).Ticks,
            RecurrenceFrequency.Monthly => (long)(elapsed.TotalDays / 31) / interval,
            RecurrenceFrequency.Yearly => (long)(elapsed.TotalDays / 366) / interval,
            _ => 0,
        };

        // Günlük ve haftalık seride (saat dilimi yokken) dönem tam olarak hesaplanır; ötekilerde tahmin aşağıdandır.
        // Her durumda bir dönem geriden başlanır: saat dilimi ve hafta başı farkı bir dönemi aşmaz.
        return (int)Math.Clamp(periods - 1, 0, int.MaxValue - MaxPeriods - 1);
    }

    /// <summary>Bir dönemin oluşları, duvar saatiyle ve zamana göre sıralı.</summary>
    private static List<DateTime> Candidates(RecurrenceFrequency frequency, int interval, DateTime first, IReadOnlyList<RecurrenceDay> days, int period)
    {
        var result = new List<DateTime>();
        var time = first.TimeOfDay;

        switch (frequency)
        {
            case RecurrenceFrequency.Daily:
            {
                var day = first.AddDays((double)period * interval);

                if (days.Count == 0 || days.Any(entry => entry.Day == day.DayOfWeek))
                {
                    result.Add(day);
                }

                break;
            }

            case RecurrenceFrequency.Weekly:
            {
                if (days.Count == 0)
                {
                    result.Add(first.AddDays(7d * period * interval));
                    break;
                }

                // Hafta pazartesi başlar (iCalendar'ın varsayılanı).
                var monday = first.Date.AddDays(-(((int)first.DayOfWeek + 6) % 7)).AddDays(7d * period * interval);

                foreach (var weekday in days.Select(entry => ((int)entry.Day + 6) % 7).Distinct().OrderBy(index => index))
                {
                    result.Add(monday.AddDays(weekday) + time);
                }

                break;
            }

            case RecurrenceFrequency.Monthly:
            case RecurrenceFrequency.Yearly:
            {
                var month = frequency == RecurrenceFrequency.Monthly
                    ? new DateTime(first.Year, first.Month, 1).AddMonths(checked(period * interval))
                    : new DateTime(first.Year, first.Month, 1).AddYears(checked(period * interval));

                var length = DateTime.DaysInMonth(month.Year, month.Month);

                if (days.Count == 0)
                {
                    // Ayın o günü yoksa (31 Şubat, 29 Şubat) ayın son günü.
                    result.Add(month.AddDays(Math.Min(first.Day, length) - 1) + time);
                    break;
                }

                foreach (var entry in days)
                {
                    // Ayın o güne düşen günleri, sırayla.
                    var matches = Enumerable.Range(0, length)
                        .Select(index => month.AddDays(index))
                        .Where(day => day.DayOfWeek == entry.Day)
                        .ToList();

                    if (entry.Ordinal == 0)
                    {
                        result.AddRange(matches.Select(day => day + time));
                    }
                    else if (Math.Abs(entry.Ordinal) <= matches.Count)
                    {
                        // Eksi sıra sondan sayılır: -1 ayın son o günüdür. Ayda o sıra yoksa (beşinci pazartesi) oluş da yoktur.
                        result.Add(matches[entry.Ordinal > 0 ? entry.Ordinal - 1 : matches.Count + entry.Ordinal] + time);
                    }
                }

                result = [.. result.Distinct().OrderBy(day => day)];
                break;
            }

            default:
                if (period == 0)
                {
                    result.Add(first);
                }

                break;
        }

        return result;
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
    /// değişmez. Değişiklik, kuraldan sapma kalmadıysa (zaman aynı, iptal değil) istisnayı kaldırır. Kaydetmek çağıranın
    /// işidir: istisna, serinin kendisiyle aynı kaydetmede ve onun yazma kurallarından geçerek kalıcı olur.
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

        return new Occurrence(originalStart, moved ?? originalStart, change.Cancelled);
    }
}
