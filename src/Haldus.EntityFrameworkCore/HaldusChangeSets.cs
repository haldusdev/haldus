using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Haldus;

/// <summary>Bir kayda uygulanmak üzere biriktirilen değişiklikler.</summary>
public sealed class HaldusChangeSet
{
    public Guid Id { get; set; }

    /// <summary>Kaydın kaynağı: entity'nin tam tip adı.</summary>
    public string Resource { get; set; } = "";

    public string RecordId { get; set; } = "";

    /// <summary>Biriken değişiklikler; kaydın kısmi güncelleme gövdesiyle aynı biçimde bir JSON nesnesi.</summary>
    public string Changes { get; set; } = "{}";

    public ChangeSetStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public string? ClosedBy { get; set; }
}

/// <summary>
/// Değişiklik kümelerinin saklanması. Kümenin kayda nasıl uygulanacağı üretilen uçtadır: kaydın kendi kısmi
/// güncellemesinin bütün kuralları (doğrulama, referanslar, hesaplanan alanlar, sürüm) uygulamada da geçerlidir.
/// </summary>
public static class HaldusChangeSets
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusChangeSet>(entity =>
        {
            entity.ToTable("HaldusChangeSets");
            entity.Property(set => set.Id).ValueGeneratedNever();
            entity.Property(set => set.Resource).HasMaxLength(400);
            entity.Property(set => set.RecordId).HasMaxLength(200);
            entity.Property(set => set.Changes).HasColumnType("jsonb");
            entity.Property(set => set.CreatedBy).HasMaxLength(256);
            entity.Property(set => set.ClosedBy).HasMaxLength(256);

            // Aynı kümeyi birlikte uygulamak isteyen iki istekten yalnızca biri geçer.
            entity.Property(set => set.Status).HasConversion<string>().HasMaxLength(16).IsConcurrencyToken();
            entity.HasIndex(set => new { set.Resource, set.RecordId });
        });
    }

    /// <summary>Kayıt için boş bir değişiklik kümesi açar ve kaydeder.</summary>
    public static async Task<HaldusChangeSet> BeginAsync<TEntity>(DbContext context, string recordId, CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(recordId);

        var set = new HaldusChangeSet
        {
            Id = Guid.CreateVersion7(),
            Resource = typeof(TEntity).FullName!,
            RecordId = recordId,
            Status = ChangeSetStatus.Open,
            CreatedAt = HaldusTime.UtcNow(context.GetService<TimeProvider>()),
            CreatedBy = HaldusScope.UserId(context),
        };

        context.Add(set);
        await context.SaveChangesAsync(cancellationToken);
        return set;
    }

    /// <summary>
    /// Kaydın, verilen kimlikli değişiklik kümesi. Başka bir kaydın kümesi bulunamaz sayılır; kaydın isteği yapana
    /// görünür olduğunu çağıran, kaydı kendi filtrelerinden geçirerek okuyup doğrular.
    /// </summary>
    public static Task<HaldusChangeSet?> FindAsync<TEntity>(DbContext context, string recordId, Guid id, CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        var resource = typeof(TEntity).FullName!;

        return context.Set<HaldusChangeSet>()
            .FirstOrDefaultAsync(set => set.Id == id && set.Resource == resource && set.RecordId == recordId, cancellationToken);
    }

    /// <summary>Kaydın değişiklik kümeleri; en yeni başta.</summary>
    public static async Task<List<ChangeSetInfo>> ListAsync<TEntity>(DbContext context, string recordId, CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        var resource = typeof(TEntity).FullName!;

        var sets = await context.Set<HaldusChangeSet>()
            .AsNoTracking()
            .Where(set => set.Resource == resource && set.RecordId == recordId)
            .OrderByDescending(set => set.Id)
            .Take(100)
            .ToListAsync(cancellationToken);

        return sets.ConvertAll(ToInfo);
    }

    /// <summary>Kümeyi kapatır: değişiklikler uygulandı ya da onlardan vazgeçildi. Kaydetmek çağıranın işidir.</summary>
    public static void Close(DbContext context, HaldusChangeSet set, bool committed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(set);

        set.Status = committed ? ChangeSetStatus.Committed : ChangeSetStatus.Discarded;
        set.ClosedAt = HaldusTime.UtcNow(context.GetService<TimeProvider>());
        set.ClosedBy = HaldusScope.UserId(context);
    }

    public static ChangeSetInfo ToInfo(HaldusChangeSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        using var changes = JsonDocument.Parse(set.Changes);
        return new ChangeSetInfo(set.Id, set.Status, changes.RootElement.Clone(), set.CreatedAt, set.CreatedBy, set.ClosedAt, set.ClosedBy);
    }
}
