using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>Bir kullanıcının bir kaydı okuduğunun işareti.</summary>
public sealed class HaldusReadReceipt
{
    public long Id { get; set; }

    /// <summary>Kaydın kaynağı: entity'nin tam tip adı.</summary>
    public string Resource { get; set; } = "";

    public string RecordId { get; set; } = "";

    public string UserId { get; set; } = "";

    public DateTimeOffset ReadAt { get; set; }
}

/// <summary>
/// Paylaşılan kayıtların kullanıcı başına okundu bilgisi (<c>.ReadReceipts()</c>): duyuru, ortak belge. Bilgi kayıtta
/// değil ayrı bir tabloda durur; kaydın kendisi değişmez. Bir işaret, kaydı göremeyen birine bir şey söylemez: uçlar
/// kaydı her zaman kendi filtrelerinden geçirerek bulur.
/// </summary>
public static class HaldusReadReceipts
{
    /// <summary>"Hepsini okundu işaretle" çağrısının bir istekte işaretlediği en çok kayıt.</summary>
    public const int MaxReadAll = 5000;

    private const string UniqueIndex = "UX_HaldusReadReceipts_Record";

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusReadReceipt>(entity =>
        {
            entity.ToTable("HaldusReadReceipts");
            entity.Property(item => item.Resource).HasMaxLength(400);
            entity.Property(item => item.RecordId).HasMaxLength(200);
            entity.Property(item => item.UserId).HasMaxLength(200);
            entity.HasIndex(item => new { item.Resource, item.UserId, item.RecordId }).IsUnique().HasDatabaseName(UniqueIndex);
        });
    }

    /// <summary>Sorguyu, kullanıcının okuduğu (veya okumadığı) kayıtlarla sınırlar.</summary>
    /// <param name="key">Kaydın anahtarının metin hali; işaretlerle bununla eşleşir.</param>
    public static IQueryable<TEntity> Filter<TEntity>(
        DbContext context,
        IQueryable<TEntity> query,
        Expression<Func<TEntity, string>> key,
        string? userId,
        bool unread)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(key);

        // Kullanıcısı çözülemeyen isteğin hiç işareti yoktur: her kayıt okunmamıştır.
        var receipts = Of<TEntity>(context, userId ?? string.Empty);
        var item = key.Parameters[0];
        Expression<Func<string, bool>> has = recordId => receipts.Any(receipt => receipt.RecordId == recordId);
        var read = Expression.Invoke(has, key.Body);
        var body = unread ? (Expression)Expression.Not(read) : read;

        return query.Where(Expression.Lambda<Func<TEntity, bool>>(new Inliner().Visit(body), item));
    }

    /// <summary>Kaydı kullanıcı için okundu işaretler; zaten işaretliyse bir şey değişmez.</summary>
    public static async Task MarkAsync<TEntity>(DbContext context, string recordId, string userId, DateTimeOffset now, CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);

        if (await Of<TEntity>(context, userId).AnyAsync(receipt => receipt.RecordId == recordId, cancellationToken))
        {
            return;
        }

        var added = context.Add(new HaldusReadReceipt { Resource = ResourceOf<TEntity>(), RecordId = recordId, UserId = userId, ReadAt = now });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (HaldusDb.IsUniqueViolation(exception, UniqueIndex))
        {
            // Aynı anda gelen ikinci istek işareti zaten yazdı.
            added.State = EntityState.Detached;
        }
    }

    public static Task<int> UnmarkAsync<TEntity>(DbContext context, string recordId, string userId, CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);

        return Of<TEntity>(context, userId).Where(receipt => receipt.RecordId == recordId).ExecuteDeleteAsync(cancellationToken);
    }

    public static async Task<ReadState> StateAsync<TEntity>(DbContext context, string recordId, string userId, CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);

        var readAt = await Of<TEntity>(context, userId)
            .Where(receipt => receipt.RecordId == recordId)
            .Select(receipt => (DateTimeOffset?)receipt.ReadAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new ReadState(readAt is not null, readAt);
    }

    /// <summary>
    /// Verilen (isteği yapana görünen) kayıtlardan okunmamış olanları okundu işaretler; bir istekte en çok
    /// <see cref="MaxReadAll"/> kayıt. Daha çoğu varsa çağrı yinelenir.
    /// </summary>
    public static async Task<int> MarkAllAsync<TEntity>(
        DbContext context,
        IQueryable<TEntity> visible,
        Expression<Func<TEntity, string>> key,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);

        var resource = ResourceOf<TEntity>();

        for (var attempt = 0; ; attempt++)
        {
            var ids = await Filter(context, visible, key, userId, unread: true).Select(key).Take(MaxReadAll).ToListAsync(cancellationToken);

            if (ids.Count == 0)
            {
                return 0;
            }

            var added = ids.ConvertAll(id => context.Add(new HaldusReadReceipt { Resource = resource, RecordId = id, UserId = userId, ReadAt = now }));

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return ids.Count;
            }
            catch (DbUpdateException exception) when (attempt == 0 && HaldusDb.IsUniqueViolation(exception, UniqueIndex))
            {
                // Bu sırada kayıtlardan biri tek tek işaretlendi; liste yeniden okunur.
                added.ForEach(entry => entry.State = EntityState.Detached);
            }
        }
    }

    private static IQueryable<HaldusReadReceipt> Of<TEntity>(DbContext context, string userId)
    {
        var resource = ResourceOf<TEntity>();
        return context.Set<HaldusReadReceipt>().Where(receipt => receipt.Resource == resource && receipt.UserId == userId);
    }

    private static string ResourceOf<TEntity>() => typeof(TEntity).FullName ?? typeof(TEntity).Name;

    /// <summary>İç içe çağrılan lambda'yı gövdesiyle değiştirir; sorgu sağlayıcısı <c>Invoke</c> düğümünü çeviremez.</summary>
    private sealed class Inliner : ExpressionVisitor
    {
        protected override Expression VisitInvocation(InvocationExpression node)
        {
            if (node.Expression is LambdaExpression lambda)
            {
                var body = lambda.Body;

                for (var index = 0; index < lambda.Parameters.Count; index++)
                {
                    body = new Replacer(lambda.Parameters[index], node.Arguments[index]).Visit(body);
                }

                return Visit(body);
            }

            return base.VisitInvocation(node);
        }
    }

    private sealed class Replacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
