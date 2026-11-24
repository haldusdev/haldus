using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Haldus;

/// <summary>
/// Sıralı kaynakların (<c>.Orderable()</c>) konumlarını yönetir. Konumlar bir kapsam içinde (aynı kiracı, sahip
/// ve ebeveyn) 0'dan başlar ve boşluksuzdur; bunu korumak için kapsamın üyelerini değiştiren her yazma (ekleme,
/// silme, taşıma) aynı kilidin altında yapılır.
/// </summary>
public static class HaldusOrdering
{
    /// <summary>
    /// Kapsamı kilitler ve, açık bir transaction yoksa, yenisini açar. Kilit transaction bitene kadar tutulur;
    /// <see cref="OrderingScope.CommitAsync"/> çağrılmadan atılırsa burada açılan transaction geri alınır.
    /// Kilit PostgreSQL'e özgüdür (<c>pg_advisory_xact_lock</c>).
    /// </summary>
    /// <param name="name">Kaynağın adı; farklı kaynakların kilitleri birbirini beklemesin diye.</param>
    /// <param name="scope">Kapsamı ayıran değerler: kiracı, sahip, ebeveyn.</param>
    public static async Task<OrderingScope> BeginAsync(
        DbContext context,
        string name,
        CancellationToken cancellationToken,
        params object?[] scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(scope);

        var owned = context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            await LockAsync(context, name, cancellationToken, scope);
        }
        catch
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }

            throw;
        }

        return new OrderingScope(owned);
    }

    /// <summary>
    /// Açık transaction içinde adlandırılmış bir kilit alır; kilit transaction bitince bırakılır. Bekleyenler geliş
    /// sırasıyla ilerler.
    /// </summary>
    internal static Task LockAsync(DbContext context, string name, CancellationToken cancellationToken, params object?[] scope)
    {
        var key = LockKey(name, scope);
        return context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    /// <summary>Kapsamın sonuna eklenecek kaydın konumu.</summary>
    public static async Task<int> NextAsync<T>(IQueryable<T> siblings, CancellationToken cancellationToken)
        where T : class, IOrderable
    {
        ArgumentNullException.ThrowIfNull(siblings);
        return (await siblings.Select(item => (int?)item.Position).MaxAsync(cancellationToken) ?? -1) + 1;
    }

    /// <summary>Kapsamdan bir kayıt çıktıktan sonra, arkasındaki kayıtları bir öne alır.</summary>
    public static Task CloseGapAsync<T>(IQueryable<T> siblings, int position, CancellationToken cancellationToken)
        where T : class, IOrderable
    {
        ArgumentNullException.ThrowIfNull(siblings);

        return siblings
            .Where(item => item.Position > position)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Position, item => item.Position - 1), cancellationToken);
    }

    /// <summary>
    /// Kaydı kapsamdaki yeni yerine koyar ve herkesi yeniden numaralar. Değişiklikler bağlama yazılır; kaydetmek
    /// çağıranın işidir. İstenen konum listenin dışındaysa kayıt sona gider.
    /// </summary>
    /// <param name="siblings">Kapsamın, konuma göre sıralanmış ve izlenen kayıtları (taşınan kayıt dahil).</param>
    public static async Task MoveAsync<T>(IQueryable<T> siblings, T entity, int position, CancellationToken cancellationToken)
        where T : class, IOrderable
    {
        ArgumentNullException.ThrowIfNull(siblings);
        ArgumentNullException.ThrowIfNull(entity);

        var ordered = await siblings.ToListAsync(cancellationToken);
        ordered.Remove(entity);
        ordered.Insert(Math.Clamp(position, 0, ordered.Count), entity);

        for (var index = 0; index < ordered.Count; index++)
        {
            if (ordered[index].Position != index)
            {
                ordered[index].Position = index;
            }
        }
    }

    private static long LockKey(string name, object?[] scope)
    {
        var text = new StringBuilder(name);

        foreach (var part in scope)
        {
            text.Append('|').Append(Convert.ToString(part, System.Globalization.CultureInfo.InvariantCulture));
        }

        return BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0);
    }
}

/// <summary>Bir sıralama işleminin sınırı; açtığı transaction'ı yalnızca kendisi sonuçlandırır.</summary>
public sealed class OrderingScope : IAsyncDisposable
{
    private readonly IDbContextTransaction? _transaction;

    internal OrderingScope(IDbContextTransaction? transaction) => _transaction = transaction;

    public Task CommitAsync(CancellationToken cancellationToken) =>
        _transaction is null ? Task.CompletedTask : _transaction.CommitAsync(cancellationToken);

    public ValueTask DisposeAsync() => _transaction is null ? ValueTask.CompletedTask : _transaction.DisposeAsync();
}
