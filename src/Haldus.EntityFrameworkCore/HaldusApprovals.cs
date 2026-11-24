using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Haldus;

/// <summary>Bir kayıt için verilmiş karar; silinmez ve değişmez.</summary>
public sealed class HaldusApprovalDecision
{
    public long Id { get; set; }

    /// <summary>Kaydın tipinin tam adı; aynı kısa ada sahip iki kaynağın kararları karışmaz.</summary>
    public string Resource { get; set; } = "";

    /// <summary>Kaydın anahtarı, metin olarak.</summary>
    public string RecordId { get; set; } = "";

    /// <summary>Kaydın kiracısı; kiracıya bağlı olmayan kaynakta boştur.</summary>
    public Guid? TenantId { get; set; }

    public int Level { get; set; }

    public bool Approved { get; set; }

    public string UserId { get; set; } = "";

    public string? Note { get; set; }

    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary>Bir karar isteğinin sonucu.</summary>
public enum ApprovalOutcome
{
    /// <summary>Karar kayda yazıldı; kaydedilmeyi bekliyor.</summary>
    Recorded,

    /// <summary>Kararı verecek kullanıcı çözülemedi.</summary>
    UserRequired,

    /// <summary>Kayıt karar beklemiyor: zaten onaylandı veya reddedildi.</summary>
    NotPending,

    /// <summary>İçeriğin bir parçasını yazmış olan kişi ona karar veremez.</summary>
    OwnRequest,

    /// <summary>Bu kullanıcı, bu içerik için önceki bir düzeyde zaten onay verdi.</summary>
    AlreadyApproved,
}

/// <summary>
/// Onaya bağlı kayıtların (<c>.Approvable()</c>) kuralları. Üretilen uçlar yalnızca kaydı bulur ve kaydeder; kimin
/// karar verebileceği, kararın kayda nasıl yazıldığı ve içerik değişince onayın nasıl düştüğü buradadır.
/// </summary>
public static class HaldusApprovals
{
    private static readonly ConcurrentDictionary<Type, HashSet<string>> Content = new();
    private static readonly ConditionalWeakTable<object, object> Exemptions = [];

    /// <summary>Karar kayıtlarının tablosunu modele ekler; yalnızca onaya bağlı bir kaynak varsa çağrılır.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusApprovalDecision>(entity =>
        {
            entity.ToTable("HaldusApprovalDecisions");
            entity.HasKey(decision => decision.Id);
            entity.Property(decision => decision.Resource).HasMaxLength(512);
            entity.Property(decision => decision.RecordId).HasMaxLength(128);
            entity.Property(decision => decision.UserId).HasMaxLength(256);
            entity.Property(decision => decision.Note).HasMaxLength(1000);
            entity.HasIndex(decision => new { decision.Resource, decision.RecordId, decision.Id });
        });
    }

    /// <summary>
    /// Bir kaynağın içerik alanlarını bildirir: bunlardan biri değişince toplanan onaylar düşer. Haldus'un yönettiği
    /// alanlar (durum, numara, zaman damgaları, onay alanlarının kendisi) içerik değildir. Üretilen modül kodu çağırır.
    /// </summary>
    public static void Register<TEntity>(params string[] content)
        where TEntity : class, IApprovable =>
        Content[typeof(TEntity)] = [.. content];

    /// <summary>
    /// Kaydın içeriği yazıldı (oluşturuldu veya değişti): toplanan onaylar geçersiz olur ve kayıt yeniden karar
    /// bekler. İçeriği yazan kişi, kayda bundan sonra hiç karar veremeyecek olanlara eklenir.
    /// </summary>
    public static void Reset(IApprovable record, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(context);

        var userId = HaldusScope.UserId(context);

        record.ApprovalStatus = ApprovalStatus.Pending;
        record.ApprovalLevel = 0;
        record.RequestedBy = userId;
        record.RequestedAt = Advance(record.RequestedAt, HaldusScope.UtcNow(context));
        record.DecidedBy = null;
        record.DecidedAt = null;
        record.DecisionNote = null;

        if (userId is { Length: > 0 } && !record.Contributors.Contains(userId))
        {
            // Yeni bir liste atanır ki değişiklik, kaydetmeden hemen önce de olsa kesin olarak algılansın.
            record.Contributors = [.. record.Contributors, userId];
        }
    }

    /// <summary>
    /// İzlenen kaydın içeriği değiştiyse onayı düşürür. Kaydeden her yol için bunu interceptor yapar; kaydetmeyen
    /// yollar (önizleme) sonucu doğru göstermek için kendileri çağırır.
    /// </summary>
    public static void ResetIfChanged(IApprovable record, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(context);

        context.ChangeTracker.DetectChanges();

        if (ContentChanged(context.Entry(record)))
        {
            Reset(record, context);
        }
    }

    /// <summary>
    /// Kaydın, onayın zaten izin verdiği bir işlemle (onay isteyen bir geçiş veya aksiyon) değiştiğini bildirir: o
    /// işlemin yazdığı alanlar onayı düşürmez. Yalnızca bir sonraki kaydetme için geçerlidir.
    /// </summary>
    public static void Exempt(IApprovable record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Exemptions.AddOrUpdate(record, record);
    }

    internal static bool TakeExemption(IApprovable record) => Exemptions.Remove(record);

    /// <summary>Kaydın içerik alanlarından biri değişti mi.</summary>
    internal static bool ContentChanged(EntityEntry entry)
    {
        if (entry.ComplexProperties.Any(property => property.IsModified))
        {
            return true;
        }

        var content = Content.GetValueOrDefault(entry.Metadata.ClrType);

        // Alanları bildirilmemiş (elle yazılmış) bir kaynakta, onay alanları dışındaki her değişiklik içeriktir.
        return entry.Properties.Any(property => property.IsModified
            && (content?.Contains(property.Metadata.Name) ?? !IsApprovalField(property.Metadata.Name)));
    }

    /// <summary>
    /// Geçerli kullanıcının kararını kayda yazar ve karar kaydını bağlama ekler; kaydetmek çağıranın işidir.
    /// Kayıt son düzey de onaylayınca onaylanmış olur; herhangi bir düzeydeki ret kaydı reddeder.
    /// </summary>
    /// <param name="levels">Kaydın onaylanması için gereken, birbirinden farklı kişilerin vereceği onay sayısı.</param>
    public static async Task<ApprovalOutcome> DecideAsync<TEntity>(
        DbContext context,
        TEntity record,
        string recordId,
        bool approve,
        string? note,
        int levels,
        CancellationToken cancellationToken)
        where TEntity : class, IApprovable
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(record);

        if (HaldusScope.UserId(context) is not { Length: > 0 } userId)
        {
            return ApprovalOutcome.UserRequired;
        }

        if (record.ApprovalStatus != ApprovalStatus.Pending)
        {
            return ApprovalOutcome.NotPending;
        }

        // İçeriğin herhangi bir parçasını yazmış olan kişi karar veremez; yalnızca son yazan değil.
        if (record.RequestedBy == userId || record.Contributors.Contains(userId))
        {
            return ApprovalOutcome.OwnRequest;
        }

        if (record.ApprovalLevel > 0)
        {
            // Bu içerik için verilmiş onaylar, kaydın son onaylarıdır: içerik değişince düzey sıfırlanır.
            var approvers = await Decisions(context, record, recordId)
                .Where(decision => decision.Approved)
                .OrderByDescending(decision => decision.Id)
                .Take(record.ApprovalLevel)
                .Select(decision => decision.UserId)
                .ToListAsync(cancellationToken);

            if (approvers.Contains(userId))
            {
                return ApprovalOutcome.AlreadyApproved;
            }
        }

        var now = HaldusScope.UtcNow(context);
        var level = record.ApprovalLevel + 1;

        if (approve)
        {
            record.ApprovalLevel = level;

            if (level >= levels)
            {
                record.ApprovalStatus = ApprovalStatus.Approved;
            }
        }
        else
        {
            record.ApprovalStatus = ApprovalStatus.Rejected;
        }

        record.DecidedBy = userId;
        record.DecidedAt = now;
        record.DecisionNote = note;

        context.Set<HaldusApprovalDecision>().Add(new HaldusApprovalDecision
        {
            Resource = ResourceOf<TEntity>(),
            RecordId = recordId,
            TenantId = (record as IMultiTenant)?.TenantId,
            Level = level,
            Approved = approve,
            UserId = userId,
            Note = note,
            DecidedAt = now,
        });

        return ApprovalOutcome.Recorded;
    }

    /// <summary>Kayıt için verilmiş kararlar, veriliş sırasıyla. Kaydı, isteği yapana görünür olarak çağıran yükler.</summary>
    public static async Task<IReadOnlyList<ApprovalDecision>> ListAsync<TEntity>(
        DbContext context,
        TEntity record,
        string recordId,
        CancellationToken cancellationToken)
        where TEntity : class, IApprovable
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(record);

        return await Decisions(context, record, recordId)
            .AsNoTracking()
            .OrderBy(decision => decision.Id)
            .Select(decision => new ApprovalDecision(decision.Level, decision.Approved, decision.UserId, decision.Note, decision.DecidedAt))
            .ToListAsync(cancellationToken);
    }

    // Kararlar tam olarak bu kayda aittir: tipin tam adı, anahtar ve (varsa) kiracı birlikte eşleşmelidir.
    private static IQueryable<HaldusApprovalDecision> Decisions<TEntity>(DbContext context, TEntity record, string recordId)
        where TEntity : class, IApprovable
    {
        var resource = ResourceOf<TEntity>();
        var tenantId = (record as IMultiTenant)?.TenantId;

        return context.Set<HaldusApprovalDecision>()
            .Where(decision => decision.Resource == resource && decision.RecordId == recordId && decision.TenantId == tenantId);
    }

    private static string ResourceOf<TEntity>() => typeof(TEntity).FullName ?? typeof(TEntity).Name;

    private static bool IsApprovalField(string name) => name is nameof(IApprovable.ApprovalStatus)
        or nameof(IApprovable.ApprovalLevel)
        or nameof(IApprovable.RequestedBy)
        or nameof(IApprovable.Contributors)
        or nameof(IApprovable.RequestedAt)
        or nameof(IApprovable.DecidedBy)
        or nameof(IApprovable.DecidedAt)
        or nameof(IApprovable.DecisionNote);

    // Yazma anı eşzamanlılık belirtecidir: aynı mikrosaniyeye düşen iki yazma da onu ilerletmelidir.
    private static DateTimeOffset Advance(DateTimeOffset previous, DateTimeOffset now) =>
        now > previous ? now : previous.AddTicks(10);
}
