using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Haldus;

/// <summary>Yayınlanmayı bekleyen bir olay. Kayıtla aynı kaydetmede yazılır; teslim edilince silinir.</summary>
public sealed class HaldusOutboxEvent
{
    public long Id { get; set; }

    public string Type { get; set; } = "";

    public string Resource { get; set; } = "";

    public string RecordId { get; set; } = "";

    public Guid? TenantId { get; set; }

    public string? UserId { get; set; }

    /// <summary>Kaydın olay anındaki hali; kaynağın yanıt biçiminde JSON.</summary>
    public string Data { get; set; } = "{}";

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Başarısız teslimat denemelerinin sayısı.</summary>
    public int Attempts { get; set; }

    /// <summary>Bir sonraki denemenin en erken zamanı.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>Olaydan vazgeçildiği zaman; doluysa olay artık denenmez ve arkasındakileri bekletmez.</summary>
    public DateTimeOffset? DeadAt { get; set; }
}

/// <summary>
/// Olay yayını (<c>.PublishesEvents()</c>). Olay, kaydı hangi kod yazarsa yazsın interceptor tarafından, kayıtla aynı
/// kaydetmede bir bekleme tablosuna yazılır; böylece kayıt kalıcı olmadıysa olay da yoktur, olduysa olay kaybolmaz.
/// Teslimat ayrıdır ve kayıt kalıcı olduktan sonra yapılır (<see cref="DeliverAsync"/>).
/// </summary>
public static class HaldusEvents
{
    private static readonly ConcurrentDictionary<Type, (string Resource, Func<object, object> Describe)> Sources = new();
    private static readonly ConditionalWeakTable<DbContext, List<(object Entity, string Name)>> Raised = [];
    private static readonly SemaphoreSlim Signal = new(0, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Bir olaydan vazgeçilmeden önce yapılan en çok teslimat denemesi.</summary>
    public static int MaxAttempts { get; set; } = 10;

    /// <summary>Bir turda teslim edilen en çok olay.</summary>
    public const int BatchSize = 100;

    /// <summary>Bu süreçte olay yayınlayan en az bir kaynak var mı.</summary>
    public static bool HasSources => !Sources.IsEmpty;

    /// <summary>Bekleme tablosunu modele ekler; yalnızca olay yayınlayan bir kaynak varsa çağrılır.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusOutboxEvent>(entity =>
        {
            entity.ToTable("HaldusOutbox");
            entity.Property(item => item.Type).HasMaxLength(200);
            entity.Property(item => item.Resource).HasMaxLength(100);
            entity.Property(item => item.RecordId).HasMaxLength(200);
            entity.Property(item => item.UserId).HasMaxLength(256);
            entity.Property(item => item.Data).HasColumnType("jsonb");
            entity.Property(item => item.LastError).HasMaxLength(1000);
        });
    }

    /// <summary>
    /// Olay yayınlayan bir kaynağı bildirir. Üretilen modül kodu çağırır.
    /// </summary>
    /// <param name="resource">Olay türlerinin öneki: <c>order</c>.</param>
    /// <param name="describe">Kaydı, yanıt biçimine çevirir; olayın verisi budur.</param>
    public static void Register<TEntity>(string resource, Func<TEntity, object> describe)
        where TEntity : class, IPublishesEvents
    {
        ArgumentException.ThrowIfNullOrEmpty(resource);
        ArgumentNullException.ThrowIfNull(describe);

        Sources[typeof(TEntity)] = (resource, entity => describe((TEntity)entity));
    }

    /// <summary>
    /// Kayıt kaydedilirken adı verilen olayın da yayınlanmasını ister (bir geçişin olayı). Olay, kaydın kaydedildiği
    /// andaki halini taşır ve kayıt kaydedilmezse hiç yazılmaz.
    /// </summary>
    public static void Raise(DbContext context, object entity, string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrEmpty(name);

        Raised.GetOrCreateValue(context).Add((entity, name));
    }

    /// <summary>Kaydedilmek üzere olan, olay yayınlayan her kaydın olaylarını bağlama ekler.</summary>
    internal static void Capture(DbContext context, DateTimeOffset now, Lazy<string?> users)
    {
        Raised.TryGetValue(context, out var raised);

        if (Sources.IsEmpty)
        {
            raised?.Clear();
            return;
        }

        var entries = context.ChangeTracker.Entries().ToList();

        // Önceki kaydetme başarısız olduysa onun eklediği olaylar hâlâ bekliyordur; yenileri yazılmadan bırakılır.
        foreach (var stale in entries.Where(entry => entry is { Entity: HaldusOutboxEvent, State: EntityState.Added }))
        {
            stale.State = EntityState.Detached;
        }

        foreach (var entry in entries)
        {
            if (entry.Entity is not IPublishesEvents
                || !Sources.TryGetValue(entry.Metadata.ClrType, out var source)
                || HaldusPublishing.IsPublishedCopy(entry))
            {
                continue;
            }

            var kind = entry.State switch
            {
                EntityState.Added => "created",
                EntityState.Deleted => "deleted",

                // Yumuşak silme bir güncellemeye çevrilmiştir; olay yine de silinmedir.
                EntityState.Modified when entry.Entity is ISoftDeletable
                    && entry.Property(nameof(ISoftDeletable.DeletedAt)) is { IsModified: true, CurrentValue: not null } => "deleted",
                EntityState.Modified => "updated",
                _ => null,
            };

            if (kind is null)
            {
                continue;
            }

            Add(context, entry, source, kind, now, users);

            foreach (var (_, name) in raised?.Where(item => ReferenceEquals(item.Entity, entry.Entity)) ?? [])
            {
                Add(context, entry, source, name, now, users);
            }
        }

        raised?.Clear();
    }

    /// <summary>Yeni olay yazıldıysa bekleyen dağıtıcıyı uyandırır.</summary>
    internal static void Notify()
    {
        if (!Sources.IsEmpty && Signal.CurrentCount == 0)
        {
            try
            {
                Signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Başka bir kaydetme aynı anda uyandırdı.
            }
        }
    }

    /// <summary>Yeni bir olay yazılana veya süre dolana kadar bekler.</summary>
    public static async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await Signal.WaitAsync(timeout, cancellationToken);

    /// <summary>
    /// Bekleyen olayları, yazıldıkları sırayla teslim eder ve teslim edilenleri siler. Aynı anda tek bir dağıtıcı
    /// çalışır (veritabanı kilidi); bir olay teslim edilemezse arkasındakiler de bekler ki sıra bozulmasın. Olay
    /// <see cref="MaxAttempts"/> denemeden sonra bırakılır ve sıradakilere geçilir.
    /// </summary>
    /// <param name="publish">Olayı bütün yayıncılara teslim eder; hata fırlatırsa olay sonra yeniden denenir.</param>
    /// <param name="failed">Teslim edilemeyen olayı ve nedenini bildirir (günlük için).</param>
    /// <returns>Teslim edilen olayların sayısı.</returns>
    public static async Task<int> DeliverAsync(
        DbContext context,
        Func<HaldusEvent, CancellationToken, Task> publish,
        Action<HaldusOutboxEvent, Exception>? failed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(publish);

        await using var scope = await HaldusOrdering.BeginAsync(context, "Events", cancellationToken);
        var now = HaldusScope.UtcNow(context);
        var delivered = 0;

        var batch = await context.Set<HaldusOutboxEvent>()
            .Where(item => item.DeadAt == null)
            .OrderBy(item => item.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var item in batch)
        {
            if (item.NextAttemptAt > now)
            {
                // Bekleyen olayın arkasındakiler de bekler.
                break;
            }

            try
            {
                using var data = JsonDocument.Parse(item.Data);

                await publish(
                    new HaldusEvent(item.Id, item.Type, item.Resource, item.RecordId, item.TenantId, item.UserId, data.RootElement.Clone(), item.OccurredAt),
                    cancellationToken);

                context.Remove(item);
                delivered++;
            }
#pragma warning disable CA1031 // Yayıncı ne fırlatırsa fırlatsın olay kaybolmamalı, sonra yeniden denenmelidir.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                item.Attempts++;
                item.LastError = exception.Message.Length <= 1000 ? exception.Message : exception.Message.Substring(0, 1000);

                if (item.Attempts >= MaxAttempts)
                {
                    item.DeadAt = now;
                }
                else
                {
                    // 2, 4, 8... saniye; en çok beş dakika.
                    item.NextAttemptAt = now + TimeSpan.FromSeconds(Math.Min(Math.Pow(2, item.Attempts), 300));
                }

                failed?.Invoke(item, exception);
                break;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return delivered;
    }

    private static void Add(
        DbContext context,
        EntityEntry entry,
        (string Resource, Func<object, object> Describe) source,
        string kind,
        DateTimeOffset now,
        Lazy<string?> users)
    {
        var payload = source.Describe(entry.Entity);

        context.Set<HaldusOutboxEvent>().Add(new HaldusOutboxEvent
        {
            Type = $"{source.Resource}.{kind}",
            Resource = source.Resource,
            RecordId = HaldusRevisions.KeyText(entry),
            TenantId = (entry.Entity as IMultiTenant)?.TenantId,
            UserId = users.Value,
            Data = JsonSerializer.Serialize(payload, payload.GetType(), Json),
            OccurredAt = now,
            NextAttemptAt = now,
        });
    }
}
