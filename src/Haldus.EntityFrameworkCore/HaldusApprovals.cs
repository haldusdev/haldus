using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>Bir kayıt için verilmiş karar; silinmez ve değişmez.</summary>
public sealed class HaldusApprovalDecision
{
    public long Id { get; set; }

    /// <summary>Kaynağın adı: <c>LeaveRequest</c>.</summary>
    public string Resource { get; set; } = "";

    /// <summary>Kaydın anahtarı, metin olarak.</summary>
    public string RecordId { get; set; } = "";

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

    /// <summary>İçeriği son yazan kişi kendi yazdığına karar veremez.</summary>
    OwnRequest,

    /// <summary>Bu kullanıcı, bu içerik için önceki bir düzeyde zaten onay verdi.</summary>
    AlreadyApproved,
}

/// <summary>
/// Onaya bağlı kayıtların (<c>.Approvable()</c>) kuralları. Üretilen uçlar yalnızca kaydı bulur ve kaydeder; kimin
/// karar verebileceği ve kararın kayda nasıl yazıldığı buradadır.
/// </summary>
public static class HaldusApprovals
{
    /// <summary>Karar kayıtlarının tablosunu modele ekler; yalnızca onaya bağlı bir kaynak varsa çağrılır.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusApprovalDecision>(entity =>
        {
            entity.ToTable("HaldusApprovalDecisions");
            entity.HasKey(decision => decision.Id);
            entity.Property(decision => decision.Resource).HasMaxLength(128);
            entity.Property(decision => decision.RecordId).HasMaxLength(128);
            entity.Property(decision => decision.UserId).HasMaxLength(256);
            entity.Property(decision => decision.Note).HasMaxLength(1000);
            entity.HasIndex(decision => new { decision.Resource, decision.RecordId, decision.Id });
        });
    }

    /// <summary>
    /// Kaydın içeriği yazıldı (oluşturuldu veya değişti): toplanan onaylar geçersiz olur ve kayıt yeniden karar
    /// bekler. İçeriği yazan kişi, ona karar veremeyecek olan kişidir.
    /// </summary>
    public static void Reset(IApprovable record, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(context);

        record.ApprovalStatus = ApprovalStatus.Pending;
        record.ApprovalLevel = 0;
        record.RequestedBy = HaldusScope.UserId(context);
        record.RequestedAt = Advance(record.RequestedAt, HaldusScope.UtcNow(context));
        record.DecidedBy = null;
        record.DecidedAt = null;
        record.DecisionNote = null;
    }

    /// <summary>
    /// Geçerli kullanıcının kararını kayda yazar ve karar kaydını bağlama ekler; kaydetmek çağıranın işidir.
    /// Kayıt son düzey de onaylayınca onaylanmış olur; herhangi bir düzeydeki ret kaydı reddeder.
    /// </summary>
    /// <param name="resource">Kaynağın adı; karar kayıtları bununla ve <paramref name="recordId"/> ile bulunur.</param>
    /// <param name="levels">Kaydın onaylanması için gereken, birbirinden farklı kişilerin vereceği onay sayısı.</param>
    public static async Task<ApprovalOutcome> DecideAsync<TEntity>(
        DbContext context,
        TEntity record,
        string resource,
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

        if (record.RequestedBy == userId)
        {
            return ApprovalOutcome.OwnRequest;
        }

        if (record.ApprovalLevel > 0)
        {
            // Bu içerik için verilmiş onaylar, kaydın son onaylarıdır: içerik değişince düzey sıfırlanır.
            var approvers = await context.Set<HaldusApprovalDecision>()
                .Where(decision => decision.Resource == resource && decision.RecordId == recordId && decision.Approved)
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
            Resource = resource,
            RecordId = recordId,
            Level = level,
            Approved = approve,
            UserId = userId,
            Note = note,
            DecidedAt = now,
        });

        return ApprovalOutcome.Recorded;
    }

    /// <summary>Kayıt için verilmiş kararlar, veriliş sırasıyla. Kaydın isteği yapana görünür olduğunu çağıran doğrular.</summary>
    public static async Task<IReadOnlyList<ApprovalDecision>> ListAsync(
        DbContext context,
        string resource,
        string recordId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await context.Set<HaldusApprovalDecision>()
            .AsNoTracking()
            .Where(decision => decision.Resource == resource && decision.RecordId == recordId)
            .OrderBy(decision => decision.Id)
            .Select(decision => new ApprovalDecision(decision.Level, decision.Approved, decision.UserId, decision.Note, decision.DecidedAt))
            .ToListAsync(cancellationToken);
    }

    // Yazma anı eşzamanlılık belirtecidir: aynı mikrosaniyeye düşen iki yazma da onu ilerletmelidir.
    private static DateTimeOffset Advance(DateTimeOffset previous, DateTimeOffset now) =>
        now > previous ? now : previous.AddTicks(10);
}
