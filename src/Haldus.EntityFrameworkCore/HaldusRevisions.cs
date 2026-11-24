using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Haldus;

/// <summary>Bir kaydın, bir yazmadan hemen sonraki anlık görüntüsü.</summary>
public sealed class HaldusRevision
{
    public long Id { get; set; }

    /// <summary>Kaydın tipinin tam adı.</summary>
    public string Resource { get; set; } = "";

    public string RecordId { get; set; } = "";

    public Guid? TenantId { get; set; }

    /// <summary>Kaydın alanları ve gömülü satırları, JSON olarak.</summary>
    public string Snapshot { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public string? CreatedBy { get; set; }
}

/// <summary>
/// Sürümleri saklanan kayıtların (<c>.Revisioned()</c>) anlık görüntüleri. Görüntüyü, kaydı hangi kod yazarsa
/// yazsın, interceptor alır; okurken görüntüden izlenmeyen bir kayıt kurulur ve yanıt ondan üretilir.
/// </summary>
public static class HaldusRevisions
{
    private static readonly ConcurrentDictionary<Type, string[]> Details = new();

    /// <summary>Anlık görüntülerin tablosunu modele ekler; yalnızca sürümleri saklanan bir kaynak varsa çağrılır.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusRevision>(entity =>
        {
            entity.ToTable("HaldusRevisions");
            entity.HasKey(revision => revision.Id);
            entity.Property(revision => revision.Resource).HasMaxLength(512);
            entity.Property(revision => revision.RecordId).HasMaxLength(128);
            entity.Property(revision => revision.Snapshot).HasColumnType("jsonb");
            entity.Property(revision => revision.CreatedBy).HasMaxLength(256);
            entity.HasIndex(revision => new { revision.Resource, revision.RecordId, revision.Id });
        });
    }

    /// <summary>Bir kaynağın, anlık görüntüye girecek gömülü satır koleksiyonlarını bildirir. Üretilen modül kodu çağırır.</summary>
    public static void Register<TEntity>(params string[] details)
        where TEntity : class, IRevisioned =>
        Details[typeof(TEntity)] = details;

    /// <summary>Kaydedilmek üzere olan, sürümleri saklanan her kaydın anlık görüntüsünü bağlama ekler.</summary>
    internal static void Capture(DbContext context, DateTimeOffset now, Lazy<string?> users)
    {
        var entries = context.ChangeTracker.Entries().ToList();

        if (!entries.Any(entry => entry.Entity is IRevisioned))
        {
            return;
        }

        // Önceki kaydetme başarısız olduysa onun eklediği görüntüler hâlâ bekliyordur; yenileri alınmadan bırakılır.
        foreach (var stale in entries.Where(entry => entry is { Entity: HaldusRevision, State: EntityState.Added }))
        {
            stale.State = EntityState.Detached;
        }

        foreach (var entry in entries)
        {
            if (entry.Entity is not IRevisioned
                || entry.State is not (EntityState.Added or EntityState.Modified)
                || HaldusPublishing.IsPublishedCopy(entry))
            {
                continue;
            }

            context.Set<HaldusRevision>().Add(new HaldusRevision
            {
                Resource = ResourceOf(entry.Metadata.ClrType),
                RecordId = KeyText(entry),
                TenantId = (entry.Entity as IMultiTenant)?.TenantId,
                Snapshot = JsonSerializer.Serialize(Snapshot(context, entry, Details.GetValueOrDefault(entry.Metadata.ClrType) ?? [])),
                CreatedAt = now,
                CreatedBy = users.Value,
            });
        }
    }

    /// <summary>Kaydın sürümleri, en yenisi başta. Kaydı, isteği yapana görünür olarak çağıran yükler.</summary>
    public static async Task<PagedResult<RevisionInfo>> ListAsync<TEntity>(
        DbContext context,
        TEntity record,
        string recordId,
        int skip,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
        where TEntity : class, IRevisioned
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = Revisions(context, record, recordId).AsNoTracking();
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(revision => revision.Id)
            .Skip(skip)
            .Take(pageSize)
            .Select(revision => new RevisionInfo(revision.Id, revision.CreatedAt, revision.CreatedBy))
            .ToListAsync(cancellationToken);

        return new PagedResult<RevisionInfo>(items, page, pageSize, total);
    }

    /// <summary>
    /// Kaydın verilen sürümünü, izlenmeyen bir kayıt olarak kurar; sürüm bu kayda ait değilse <c>null</c> döner.
    /// Dönen kayıt bağlama eklenmez: yanıt üretmek ve alanlarını geri yüklemek için okunur.
    /// </summary>
    public static async Task<TEntity?> FindAsync<TEntity>(
        DbContext context,
        TEntity record,
        string recordId,
        long revisionId,
        CancellationToken cancellationToken)
        where TEntity : class, IRevisioned, new()
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = await Revisions(context, record, recordId)
            .AsNoTracking()
            .Where(revision => revision.Id == revisionId)
            .Select(revision => revision.Snapshot)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(snapshot);
        var restored = new TEntity();
        Fill(context, restored, document.RootElement, Details.GetValueOrDefault(typeof(TEntity)) ?? []);
        return restored;
    }

    // Sürümler tam olarak bu kayda aittir: tipin tam adı, anahtar ve (varsa) kiracı birlikte eşleşmelidir.
    private static IQueryable<HaldusRevision> Revisions<TEntity>(DbContext context, TEntity record, string recordId)
        where TEntity : class
    {
        var resource = ResourceOf(typeof(TEntity));
        var tenantId = (record as IMultiTenant)?.TenantId;

        return context.Set<HaldusRevision>()
            .Where(revision => revision.Resource == resource && revision.RecordId == recordId && revision.TenantId == tenantId);
    }

    private static Dictionary<string, JsonElement> Snapshot(DbContext context, EntityEntry entry, string[] details)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var property in entry.Properties.Where(property => !property.Metadata.IsShadowProperty()))
        {
            values[property.Metadata.Name] = JsonSerializer.SerializeToElement(property.CurrentValue, property.Metadata.ClrType);
        }

        foreach (var complex in entry.ComplexProperties)
        {
            values[complex.Metadata.Name] = JsonSerializer.SerializeToElement(complex.CurrentValue, complex.Metadata.ClrType);
        }

        foreach (var detail in details)
        {
            var collection = entry.Collection(detail);

            // Üretilen uçlar satırları her zaman yükler. Kaydı başka bir kod satırlarını yüklemeden değiştirdiyse
            // görüntünün eksik kalmaması için burada yüklenir.
            if (!collection.IsLoaded && entry.State != EntityState.Added)
            {
                collection.Load();
            }

            var rows = (collection.CurrentValue ?? Array.Empty<object>())
                .Cast<object>()
                .Select(row => context.Entry(row))
                .Where(row => row.State != EntityState.Deleted)
                .Select(row => Snapshot(context, row, []))
                .ToList();

            values[detail] = JsonSerializer.SerializeToElement(rows);
        }

        return values;
    }

    private static void Fill(DbContext context, object instance, JsonElement snapshot, string[] details)
    {
        var entry = context.Entry(instance);

        foreach (var value in snapshot.EnumerateObject())
        {
            // Görüntü alındıktan sonra modelden çıkarılmış alanlar atlanır; sonradan eklenenler varsayılan değerinde kalır.
            if (entry.Metadata.FindProperty(value.Name) is { } property && !property.IsShadowProperty())
            {
                entry.Property(value.Name).CurrentValue = value.Value.Deserialize(property.ClrType);
            }
            else if (entry.Metadata.FindComplexProperty(value.Name) is { } complex)
            {
                entry.ComplexProperty(value.Name).CurrentValue = value.Value.Deserialize(complex.ClrType);
            }
            else if (details.Contains(value.Name)
                && entry.Metadata.FindNavigation(value.Name) is { IsCollection: true } navigation
                && value.Value.ValueKind == JsonValueKind.Array)
            {
                var accessor = navigation.GetCollectionAccessor()!;

                foreach (var row in value.Value.EnumerateArray())
                {
                    var restored = Activator.CreateInstance(navigation.TargetEntityType.ClrType)!;
                    Fill(context, restored, row, []);
                    accessor.Add(instance, restored, forMaterialization: true);
                }
            }
        }
    }

    internal static string KeyText(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey()!.Properties.Single();
        return Convert.ToString(entry.Property(key.Name).CurrentValue, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    internal static string ResourceOf(Type type) => type.FullName ?? type.Name;
}

/// <summary>Yayın kopyalarının (<c>.Publishable()</c>) tanınması; günlükler ve anlık görüntüler kopyaları atlar.</summary>
internal static class HaldusPublishing
{
    public const string CopyOf = "PublishedOfId";

    /// <summary>Kayıt, başka bir kaydın yayındaki kopyası mı.</summary>
    public static bool IsPublishedCopy(EntityEntry entry) =>
        entry.Metadata.FindProperty(CopyOf) is not null && entry.Property(CopyOf).CurrentValue is not null;
}
