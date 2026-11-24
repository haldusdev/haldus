using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Haldus;

/// <summary>Bir bakiyeyi değiştiren yazmanın defter kaydı. Kayıtlar yalnızca eklenir.</summary>
public sealed class HaldusBalanceEntry
{
    public long Id { get; set; }

    /// <summary>Bakiyenin kaynağı: entity'nin tam tip adı.</summary>
    public string Resource { get; set; } = "";

    /// <summary>Kaynak kiracıya bağlıysa bakiyenin kiracısı.</summary>
    public Guid? TenantId { get; set; }

    public string Key { get; set; } = "";

    public string Bucket { get; set; } = "";

    public decimal Delta { get; set; }

    public decimal Balance { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? CreatedBy { get; set; }
}

/// <summary>
/// <c>MapBalance</c> kaynaklarının paylaştığı altyapı: bakiye başına kilit ve defter. Üretilen uçlar bakiyeyi
/// yalnızca bu kilidin altında okuyup yazar; böylece "yeterli bakiye var mı" kontrolü ile yazma arasına başka bir
/// istek giremez.
/// </summary>
public static class HaldusBalances
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusBalanceEntry>(entity =>
        {
            entity.ToTable("HaldusBalanceEntries");
            entity.Property(entry => entry.Resource).HasMaxLength(400);

            entity.Property(entry => entry.Bucket).HasMaxLength(64);
            entity.Property(entry => entry.Reason).HasMaxLength(500);
            entity.Property(entry => entry.CreatedBy).HasMaxLength(256);
            entity.HasIndex(entry => new { entry.Resource, entry.TenantId, entry.Key, entry.Id });
        });
    }

    /// <summary>
    /// Bakiyeyi kilitler ve, açık bir transaction yoksa, yenisini açar. Kilit transaction bitene kadar tutulur.
    /// </summary>
    /// <param name="resource">Bakiyenin kaynağı: entity'nin tam tip adı.</param>
    public static Task<OrderingScope> BeginAsync(DbContext context, string resource, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return HaldusOrdering.BeginAsync(context, "Balance:" + resource, cancellationToken, HaldusScope.TenantId(context), key);
    }

    /// <summary>Bakiyedeki bir değişimi deftere ekler; kayıt, bakiyenin kendisiyle aynı kaydetmede kalıcı olur.</summary>
    /// <param name="balance">Kovanın bu değişimden sonraki bakiyesi.</param>
    public static void Record(
        DbContext context,
        string resource,
        bool perTenant,
        string key,
        string bucket,
        decimal delta,
        decimal balance,
        string? reason)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Add(new HaldusBalanceEntry
        {
            Resource = resource,
            TenantId = perTenant ? HaldusScope.TenantId(context) : null,
            Key = key,
            Bucket = bucket,
            Delta = delta,
            Balance = balance,
            Reason = reason,
            CreatedAt = HaldusTime.UtcNow(context.GetService<TimeProvider>()),
            CreatedBy = HaldusScope.UserId(context),
        });
    }

    /// <summary>Bir bakiyenin defteri; en yeni kayıt başta.</summary>
    public static async Task<PagedResult<BalanceEntry>> EntriesAsync(
        DbContext context,
        string resource,
        bool perTenant,
        string key,
        string? bucket,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        // Kiracının defteri yalnızca o kiracıya gösterilir; kiracısı çözülemeyen istek hiçbir kayıt göremez.
        var tenantId = perTenant ? HaldusScope.TenantId(context) ?? Guid.Empty : (Guid?)null;
        var query = context.Set<HaldusBalanceEntry>()
            .AsNoTracking()
            .Where(entry => entry.Resource == resource && entry.Key == key && entry.TenantId == tenantId);

        if (!string.IsNullOrEmpty(bucket))
        {
            query = query.Where(entry => entry.Bucket == bucket);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(entry => entry.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(entry => new BalanceEntry(entry.Id, entry.Bucket, entry.Delta, entry.Balance, entry.Reason, entry.CreatedAt, entry.CreatedBy))
            .ToListAsync(cancellationToken);

        return new PagedResult<BalanceEntry>(items, page, pageSize, totalCount);
    }
}
