using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Haldus;

/// <summary>Bir bakiyeye karşı tutulan miktar.</summary>
public sealed class HaldusBalanceHold
{
    public Guid Id { get; set; }

    /// <summary>Bakiyenin kaynağı: entity'nin tam tip adı.</summary>
    public string Resource { get; set; } = "";

    /// <summary>Kaynak kiracıya bağlıysa bakiyenin kiracısı.</summary>
    public Guid? TenantId { get; set; }

    public string Key { get; set; } = "";

    public decimal Quantity { get; set; }

    public string? Reason { get; set; }

    public string Status { get; set; } = HaldusBalanceHolds.Active;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
}

/// <summary>Bir tutma işleminin neden yapılamadığı.</summary>
public enum HoldFailure
{
    None,

    /// <summary>Tutma bulunamadı.</summary>
    NotFound,

    /// <summary>Tutma artık açık değil: tahsil edildi, bırakıldı veya süresi doldu.</summary>
    Closed,

    /// <summary>Kovada yeterli miktar yok.</summary>
    Insufficient,

    /// <summary>Bakiyenin açık tutma sayısı sınırda.</summary>
    TooMany,
}

/// <param name="Entity">İşlemden sonraki bakiye.</param>
/// <param name="Balance">Yetersizlikte, kovanın o andaki bakiyesi.</param>
public readonly record struct HoldResult<TEntity>(TEntity? Entity, BalanceHold? Hold, HoldFailure Failure, decimal Balance)
    where TEntity : class;

/// <summary>
/// Bakiyeye karşı süreli tutma (<c>MapBalance().Holds(from, to)</c>): rezervasyon, ön provizyon. Tutma, miktarı
/// eldeki kovadan ayrılan kovaya taşır ve kapanana kadar orada bekletir. Her işlem bakiyenin kilidi altında ve tek
/// kaydetmededir: kova, defter ve tutma kaydı birlikte kalıcı olur.
/// </summary>
public static class HaldusBalanceHolds
{
    public const string Active = "Active";
    public const string Captured = "Captured";
    public const string Released = "Released";
    public const string Expired = "Expired";

    /// <summary>Defterdeki ve tutmadaki açıklamanın en çok uzunluğu.</summary>
    private const int MaxReasonLength = 500;

    /// <summary>Bir bakiyenin aynı anda açık olabilen en çok tutması.</summary>
    public const int MaxActive = 1000;

    /// <summary>Bakımın bir seferde okuduğu, süresi dolmuş tutma sayısı.</summary>
    public const int BatchSize = 100;

    /// <summary>Bakımın bir turda bir kaynaktan denediği en çok tutma.</summary>
    public const int MaxPerRun = 1000;

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusBalanceHold>(entity =>
        {
            entity.ToTable("HaldusBalanceHolds");
            entity.Property(hold => hold.Id).ValueGeneratedNever();
            entity.Property(hold => hold.Resource).HasMaxLength(400);
            entity.Property(hold => hold.Key).HasMaxLength(200);
            entity.Property(hold => hold.Reason).HasMaxLength(500);
            entity.Property(hold => hold.Status).HasMaxLength(16);
            entity.HasIndex(hold => new { hold.Resource, hold.TenantId, hold.Key, hold.Status });
            entity.HasIndex(hold => new { hold.Resource, hold.Status, hold.ExpiresAt });
        });
    }

    /// <summary>Tutma başlatır: miktar <paramref name="from"/> kovasından <paramref name="to"/> kovasına geçer.</summary>
    public static async Task<HoldResult<TEntity>> PlaceAsync<TEntity>(
        DbContext context,
        string resource,
        bool perTenant,
        string key,
        string from,
        string to,
        decimal quantity,
        TimeSpan? lifetime,
        string? reason,
        bool allowNegative,
        Func<DbContext, string, CancellationToken, Task<TEntity>> findOrStart,
        Func<TEntity, string, decimal> read,
        Action<TEntity, string, decimal> write,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(findOrStart);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        await using var scope = await HaldusBalances.BeginAsync(context, resource, key, cancellationToken);
        var tenantId = TenantOf(context, perTenant);

        if (await Holds(context, resource, tenantId, key).CountAsync(hold => hold.Status == Active, cancellationToken) >= MaxActive)
        {
            return new HoldResult<TEntity>(null, null, HoldFailure.TooMany, 0);
        }

        var entity = await findOrStart(context, key, cancellationToken);
        var source = read(entity, from) - quantity;

        if (source < 0 && !allowNegative)
        {
            return new HoldResult<TEntity>(null, null, HoldFailure.Insufficient, read(entity, from));
        }

        var target = read(entity, to) + quantity;
        var now = HaldusTime.UtcNow(context.GetService<TimeProvider>());

        write(entity, from, source);
        write(entity, to, target);
        HaldusBalances.Record(context, resource, perTenant, key, from, -quantity, source, reason);
        HaldusBalances.Record(context, resource, perTenant, key, to, quantity, target, reason);

        var hold = new HaldusBalanceHold
        {
            Id = Guid.CreateVersion7(),
            Resource = resource,
            TenantId = tenantId,
            Key = key,
            Quantity = quantity,
            Reason = reason,
            CreatedAt = now,
            ExpiresAt = lifetime is { } span ? now + span : null,
        };

        context.Add(hold);
        await context.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);

        return new HoldResult<TEntity>(entity, ToHold(hold), HoldFailure.None, source);
    }

    /// <summary>
    /// Açık bir tutmayı kapatır. Tahsilde miktar ayrılan kovadan düşer; bırakmada ve süre dolumunda eldeki kovaya
    /// geri döner. Süresi geçmiş tutma tahsil edilemez.
    /// </summary>
    /// <param name="outcome"><see cref="Captured"/>, <see cref="Released"/> veya <see cref="Expired"/>.</param>
    public static async Task<HoldResult<TEntity>> CloseAsync<TEntity>(
        DbContext context,
        string resource,
        bool perTenant,
        string key,
        Guid holdId,
        string outcome,
        string from,
        string to,
        bool allowNegative,
        Func<DbContext, string, CancellationToken, Task<TEntity>> findOrStart,
        Func<TEntity, string, decimal> read,
        Action<TEntity, string, decimal> write,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(findOrStart);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        await using var scope = await HaldusBalances.BeginAsync(context, resource, key, cancellationToken);
        var hold = await Holds(context, resource, TenantOf(context, perTenant), key)
            .FirstOrDefaultAsync(candidate => candidate.Id == holdId, cancellationToken);

        if (hold is null)
        {
            return new HoldResult<TEntity>(null, null, HoldFailure.NotFound, 0);
        }

        var now = HaldusTime.UtcNow(context.GetService<TimeProvider>());

        if (hold.Status != Active || (outcome == Captured && hold.ExpiresAt <= now))
        {
            return new HoldResult<TEntity>(null, ToHold(hold), HoldFailure.Closed, 0);
        }

        var entity = await findOrStart(context, key, cancellationToken);
        var quantity = hold.Quantity;
        var reserved = read(entity, to) - quantity;

        if (reserved < 0 && !allowNegative)
        {
            if (outcome != Expired)
            {
                return new HoldResult<TEntity>(null, ToHold(hold), HoldFailure.Insufficient, read(entity, to));
            }

            // Ayrılan kova bu sırada elle boşaltılmış. Süresi dolan tutma yine de kapanır (yoksa her turda yeniden
            // denenirdi); geri dönen miktar kovada kalanla sınırlıdır.
            quantity = Math.Max(read(entity, to), 0);
            reserved = read(entity, to) - quantity;
        }

        // Defterin açıklaması tutmanınkiyle aynı uzunlukla sınırlıdır; önek eklenince taşan kısım kesilir. Kesilmeseydi
        // uzun açıklamalı bir tutma hiç bırakılamazdı.
        var note = outcome == Captured ? hold.Reason : $"{outcome}: {hold.Reason}".TrimEnd(' ', ':');

        if (note is { Length: > MaxReasonLength })
        {
            note = note.Substring(0, MaxReasonLength);
        }

        write(entity, to, reserved);
        HaldusBalances.Record(context, resource, perTenant, key, to, -quantity, reserved, note);

        if (outcome != Captured)
        {
            var available = read(entity, from) + quantity;
            write(entity, from, available);
            HaldusBalances.Record(context, resource, perTenant, key, from, quantity, available, note);
        }

        hold.Status = outcome;
        hold.ClosedAt = now;

        await context.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);

        return new HoldResult<TEntity>(entity, ToHold(hold), HoldFailure.None, reserved);
    }

    /// <summary>Bir bakiyenin açık tutmaları, en eskisi başta.</summary>
    public static async Task<IReadOnlyList<BalanceHold>> ListAsync(
        DbContext context,
        string resource,
        bool perTenant,
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var holds = await Holds(context, resource, TenantOf(context, perTenant), key)
            .AsNoTracking()
            .Where(hold => hold.Status == Active)
            .OrderBy(hold => hold.CreatedAt)
            .ThenBy(hold => hold.Id)
            .Take(MaxActive)
            .ToListAsync(cancellationToken);

        return holds.ConvertAll(ToHold);
    }

    /// <summary>
    /// Süresi dolmuş, hâlâ açık tutmalar; süresi en eski olan başta. Bakım çağırır; bütün kiracıların tutmalarını
    /// görür ve her birini kendi kiracısı adına kapatır.
    /// </summary>
    /// <param name="skip">Bu turda denenip kapatılamamış tutmaların sayısı; sıranın başını tutmasınlar diye atlanır.</param>
    public static Task<List<HaldusBalanceHold>> ExpiredAsync(DbContext context, string resource, DateTimeOffset now, int skip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Set<HaldusBalanceHold>()
            .AsNoTracking()
            .Where(hold => hold.Resource == resource && hold.Status == Active && hold.ExpiresAt <= now)
            .OrderBy(hold => hold.ExpiresAt)
            .ThenBy(hold => hold.Id)
            .Skip(Math.Max(skip, 0))
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<HaldusBalanceHold> Holds(DbContext context, string resource, Guid? tenantId, string key) =>
        context.Set<HaldusBalanceHold>().Where(hold => hold.Resource == resource && hold.Key == key && hold.TenantId == tenantId);

    // Kiracının tutmaları yalnızca o kiracıya görünür; kiracısı çözülemeyen istek hiçbirini göremez.
    private static Guid? TenantOf(DbContext context, bool perTenant) =>
        perTenant ? HaldusScope.TenantId(context) ?? Guid.Empty : null;

    private static BalanceHold ToHold(HaldusBalanceHold hold) =>
        new(hold.Id, hold.Quantity, hold.Status, hold.CreatedAt, hold.ExpiresAt, hold.Reason);
}