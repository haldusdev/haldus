using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>Bir kaydın değiştiğini (oluşturuldu, güncellendi, silindi) söyleyen günlük satırı.</summary>
public sealed class HaldusChange
{
    /// <summary>Günlükteki sıra; akışın imleci budur.</summary>
    public long Id { get; set; }

    /// <summary>Kaydın tipinin tam adı.</summary>
    public string Resource { get; set; } = "";

    public string RecordId { get; set; } = "";

    // Silinen kaydın kimin olduğu sonradan sorulamaz; akışı okuyanın onu görüp göremeyeceği bu alanlardan anlaşılır.
    public Guid? TenantId { get; set; }

    public string? OwnerId { get; set; }

    public string? ParentId { get; set; }

    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>Günlükten okunan bir değişiklik penceresi: hangi kayıtlar değişti ve akış nereden sürecek.</summary>
public sealed record ChangeWindow(IReadOnlyList<(string RecordId, DateTimeOffset ChangedAt)> Items, long Next, bool HasMore);

/// <summary>
/// Değişiklik akışı (<c>.ChangeFeed()</c>). Değişikliği, kaydı hangi kod yazarsa yazsın, interceptor günlüğe
/// yazar; uç günlüğü imleçten itibaren okur ve kayıtların şimdiki halini döndürür.
/// </summary>
public static class HaldusChanges
{
    private static readonly ConcurrentDictionary<Type, string?> Parents = new();

    /// <summary>Bir istekte okunabilecek en çok günlük satırı.</summary>
    public const int MaxPageSize = 500;

    /// <summary>
    /// Akışın en yeni değişikliklerin ne kadar gerisinde kaldığı. Günlük sırası satır eklenirken, görünürlük ise
    /// transaction bitince belirlenir; uzun süren bir transaction'ın satırları, kendisinden sonra başlayıp önce
    /// bitenlerin arkasında görünür olur. Akış bu kadar beklemeden ilerlerse o satırları atlayabilir.
    /// </summary>
    public static TimeSpan SafetyLag { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Günlük tablosunu modele ekler; yalnızca değişiklik akışı olan bir kaynak varsa çağrılır.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusChange>(entity =>
        {
            entity.ToTable("HaldusChanges");
            entity.HasKey(change => change.Id);
            entity.Property(change => change.Resource).HasMaxLength(512);
            entity.Property(change => change.RecordId).HasMaxLength(128);
            entity.Property(change => change.OwnerId).HasMaxLength(256);
            entity.Property(change => change.ParentId).HasMaxLength(128);
            entity.HasIndex(change => new { change.Resource, change.Id });
        });
    }

    /// <summary>Bir kaynağı ve (bir ebeveynin altındaysa) ebeveynine işaret eden alanını bildirir. Üretilen modül kodu çağırır.</summary>
    public static void Register<TEntity>(string? parentProperty = null)
        where TEntity : class, IChangeTracked =>
        Parents[typeof(TEntity)] = parentProperty;

    /// <summary>Kaydedilmek üzere olan, akışı olan her kaydın değişikliğini bağlama ekler.</summary>
    internal static void Capture(DbContext context, DateTimeOffset now)
    {
        var entries = context.ChangeTracker.Entries().ToList();

        if (!entries.Any(entry => entry.Entity is IChangeTracked))
        {
            return;
        }

        // Önceki kaydetme başarısız olduysa onun eklediği satırlar hâlâ bekliyordur; yenileri yazılmadan bırakılır.
        foreach (var stale in entries.Where(entry => entry is { Entity: HaldusChange, State: EntityState.Added }))
        {
            stale.State = EntityState.Detached;
        }

        foreach (var entry in entries)
        {
            if (entry.Entity is not IChangeTracked
                || entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)
                || HaldusPublishing.IsPublishedCopy(entry))
            {
                continue;
            }

            var parent = Parents.GetValueOrDefault(entry.Metadata.ClrType);

            context.Set<HaldusChange>().Add(new HaldusChange
            {
                Resource = HaldusRevisions.ResourceOf(entry.Metadata.ClrType),
                RecordId = HaldusRevisions.KeyText(entry),
                TenantId = (entry.Entity as IMultiTenant)?.TenantId,
                OwnerId = (entry.Entity as IOwned)?.OwnerId,
                ParentId = parent is null ? null : Convert.ToString(entry.Property(parent).CurrentValue, CultureInfo.InvariantCulture),
                ChangedAt = now,
            });
        }
    }

    /// <summary>
    /// İmleçten sonraki değişiklikleri okur. Yalnızca isteği yapanın görebileceği kayıtların satırları döner:
    /// kiracıya ait kaynakta geçerli kiracının, kullanıcıya ait kaynakta geçerli kullanıcının, bir ebeveynin
    /// altındaki kaynakta o ebeveynin satırları. Bir kayıt sayfada birden çok kez değiştiyse bir kez yer alır.
    /// </summary>
    /// <param name="parentId">Kaynak bir ebeveynin altındaysa ebeveynin anahtarı; çağıran ebeveynin görünür olduğunu doğrular.</param>
    public static async Task<ChangeWindow> ReadAsync<TEntity>(
        DbContext context,
        long? since,
        int? pageSize,
        string? parentId,
        CancellationToken cancellationToken)
        where TEntity : class, IChangeTracked
    {
        ArgumentNullException.ThrowIfNull(context);

        var cursor = Math.Max(since ?? 0, 0);
        var size = Math.Clamp(pageSize ?? 100, 1, MaxPageSize);
        var resource = HaldusRevisions.ResourceOf(typeof(TEntity));
        var query = context.Set<HaldusChange>().AsNoTracking().Where(change => change.Resource == resource && change.Id > cursor);

        if (typeof(IMultiTenant).IsAssignableFrom(typeof(TEntity)))
        {
            var tenantId = HaldusScope.TenantId(context);
            query = query.Where(change => change.TenantId == tenantId);
        }

        if (typeof(IOwned).IsAssignableFrom(typeof(TEntity)))
        {
            var userId = HaldusScope.UserId(context);
            query = query.Where(change => change.OwnerId == userId);
        }

        if (Parents.GetValueOrDefault(typeof(TEntity)) is not null)
        {
            query = query.Where(change => change.ParentId == parentId);
        }

        var rows = await query.OrderBy(change => change.Id).Take(size + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > size;
        var threshold = HaldusScope.UtcNow(context) - SafetyLag;

        // Henüz yeterince eskimemiş ilk satırda durulur; ondan sonrakiler bir sonraki okumaya kalır.
        var page = rows.Take(size).TakeWhile(change => change.ChangedAt <= threshold).ToList();

        var latest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var change in page)
        {
            if (!latest.ContainsKey(change.RecordId))
            {
                order.Add(change.RecordId);
            }

            latest[change.RecordId] = change.ChangedAt;
        }

        return new ChangeWindow(
            [.. order.Select(recordId => (recordId, latest[recordId]))],
            page.Count == 0 ? cursor : page[^1].Id,
            hasMore && page.Count == size);
    }
}
