using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace Haldus;

/// <summary>
/// Silinen dosya kayıtlarının içeriğini depodan kaldırır. Kural kaydetmeye bağlıdır, uçlara değil: dosyanın kendi
/// silme ucu, toplu silme, uygulamanın kendi kodu ve dosyanın üst kaydıyla birlikte zincirleme silinmesi aynı yoldan
/// geçer. Veritabanının kendisinin zincirleme sildiği (bağlamda izlenmeyen) dosya kayıtları, silme kaydedilmeden önce
/// yabancı anahtarlar izlenerek bulunur.
/// </summary>
/// <remarks>
/// Veritabanı deposunda içerik, kayıtla aynı kaydetmede silinir. Başka bir depoda (dosya sistemi, nesne deposu) silme
/// geri alınamadığı için içerik ancak silme kalıcı olunca kaldırılır: açık bir transaction varsa o commit edilince,
/// geri alınırsa (veya silmeyi içeren kayıt noktasına dönülürse) hiç kaldırılmaz.
/// </remarks>
internal static class HaldusFileCleanup
{
    // Zincirleme silmenin en çok kaç ilişki boyunca izleneceği; gerçek modellerde yol bundan çok kısadır.
    private const int MaxDepth = 8;

    private static readonly ConcurrentDictionary<IEntityType, ImmutableArray<ImmutableArray<IForeignKey>>> Paths = new();
    private static readonly ConditionalWeakTable<DbContext, State> States = [];

    /// <summary>Kaydetmeden önce, bu kaydetmenin silecekleri dosya kayıtlarının içerik anahtarlarını toplar.</summary>
    internal static async Task CollectAsync(DbContext context, CancellationToken cancellationToken)
    {
        var state = States.GetOrCreateValue(context);
        state.Starts.Push(state.Saving.Count);

        var deleted = context.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Deleted).ToList();

        if (deleted.Count == 0)
        {
            return;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var surviving = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in context.ChangeTracker.Entries<IHaldusFile>())
        {
            var target = entry.State == EntityState.Deleted ? keys : surviving;

            // Kaydedilmemiş bir yükleme içeriği yeni anahtara çoktan yazmış olabilir; iki anahtar da kaldırılır.
            Add(target, entry.Property(nameof(IHaldusFile.StorageKey)).OriginalValue as string);
            Add(target, entry.Entity.StorageKey);
        }

        foreach (var group in deleted.GroupBy(entry => entry.Metadata))
        {
            foreach (var path in Paths.GetOrAdd(group.Key, Discover))
            {
                if (KeyValues(path[0], group) is not { } values)
                {
                    continue;
                }

                var found = await context.Database
                    .SqlQueryRaw<string>(Sql(path), values)
                    .ToListAsync(cancellationToken);

                keys.UnionWith(found);
            }
        }

        // Bağlamda izlenen ve silinmeyen bir dosya kaydı (ör. aynı kaydetmede başka bir üst kayda taşınan) kalır.
        keys.ExceptWith(surviving);

        if (keys.Count == 0)
        {
            return;
        }

        if (context.GetService<IHaldusFileStorage>() is HaldusDatabaseFileStorage database)
        {
            await database.RemoveWithAsync(context, keys, cancellationToken);
        }
        else
        {
            state.Saving.AddRange(keys);
        }
    }

    /// <inheritdoc cref="CollectAsync"/>
    internal static void Collect(DbContext context) => CollectAsync(context, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Kaydetme bitti. En dıştaki kaydetmenin topladıkları, sürmekte olan transaction'a bağlanıp onun commit edilmesini
    /// bekler; açık bir transaction yoksa silme zaten kalıcıdır ve yalnızca bu kaydetmenin topladıkları hemen kaldırılır.
    /// </summary>
    internal static async Task SavedAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (!States.TryGetValue(context, out var state) || !state.Starts.TryPop(out _) || state.Starts.Count > 0)
        {
            return;
        }

        var saved = state.Saving.ToList();
        state.Saving.Clear();

        if (context.Database.CurrentTransaction is { } transaction)
        {
            Bind(state, transaction.TransactionId);
            state.Pending.AddRange(saved);
            return;
        }

        // Bekleyen başka anahtar kalmışsa onların transaction'ı commit görülmeden kapanmıştır (ör. commit edilmeden
        // atıldı); kayıtları geri gelmiştir ve içeriklerine dokunulmaz.
        Reset(state);
        await FlushAsync(context, saved, cancellationToken);
    }

    /// <summary>Başarısız veya iptal edilen kaydetmenin topladıkları bırakılır: kayıtlar silinmedi.</summary>
    internal static void Failed(DbContext context)
    {
        if (States.TryGetValue(context, out var state) && state.Starts.TryPop(out var start))
        {
            state.Saving.RemoveRange(start, state.Saving.Count - start);
        }
    }

    /// <summary>Yalnızca anahtarların bağlı olduğu transaction'ın commit'i içeriği kaldırır.</summary>
    internal static async Task CommittedAsync(DbContext? context, Guid transactionId, CancellationToken cancellationToken)
    {
        if (context is null || !States.TryGetValue(context, out var state) || state.Transaction != transactionId)
        {
            return;
        }

        var keys = state.Pending.ToList();
        Reset(state);
        await FlushAsync(context, keys, cancellationToken);
    }

    internal static void RolledBack(DbContext? context, Guid transactionId)
    {
        if (context is not null && States.TryGetValue(context, out var state) && state.Transaction == transactionId)
        {
            Reset(state);
        }
    }

    /// <summary>
    /// Yeni bir transaction başlıyor. Bekleyen anahtarlar kalmışsa önceki transaction commit edilmeden kapatılmıştır
    /// (geri alınmıştır); onların içeriği kaldırılmaz.
    /// </summary>
    internal static void Started(DbContext? context, Guid transactionId)
    {
        if (context is not null && States.TryGetValue(context, out var state))
        {
            Bind(state, transactionId);
        }
    }

    internal static void SavepointCreated(DbContext? context, Guid transactionId)
    {
        if (context is not null && States.TryGetValue(context, out var state))
        {
            Bind(state, transactionId);
            state.Savepoints.Push(state.Pending.Count);
        }
    }

    internal static void SavepointReleased(DbContext? context, Guid transactionId)
    {
        if (context is not null && States.TryGetValue(context, out var state) && state.Transaction == transactionId)
        {
            state.Savepoints.TryPop(out _);
        }
    }

    /// <summary>
    /// Kayıt noktasına dönüldü: ondan sonra kaydedilen silmeler geri alındı. Kayıt noktasını açan kod (EF Core'un
    /// kaydetmesi, toplu işlemlerin öğesi) ona döndükten sonra onu bırakmaz; kayıt noktasının ömrü burada biter.
    /// </summary>
    internal static void SavepointRolledBack(DbContext? context, Guid transactionId)
    {
        if (context is not null
            && States.TryGetValue(context, out var state)
            && state.Transaction == transactionId
            && state.Savepoints.TryPop(out var mark))
        {
            state.Pending.RemoveRange(mark, state.Pending.Count - mark);
        }
    }

    /// <summary>
    /// Durumu verilen transaction'a bağlar. Başka bir transaction'a ait bekleyenler atılır: o transaction'ın commit'i
    /// görülmedi, yani geri alındı (veya bağlam havuzdan yeniden kiralandı).
    /// </summary>
    private static void Bind(State state, Guid transactionId)
    {
        if (state.Transaction != transactionId)
        {
            Reset(state);
            state.Transaction = transactionId;
        }
    }

    private static void Reset(State state)
    {
        state.Pending.Clear();
        state.Savepoints.Clear();
        state.Transaction = null;
    }

    private static async Task FlushAsync(DbContext context, List<string> pending, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var storage = context.GetService<IHaldusFileStorage>();

        foreach (var key in pending.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await storage.DeleteAsync(context, key, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Kayıtlar silindi ve bu geri alınamaz; içeriğin kalması isteği başarısız saymaktan iyidir.
                context.GetService<ILoggerFactory>()
                    .CreateLogger(typeof(HaldusFileCleanup))
                    .LogWarning(exception, "The content of a deleted Haldus file could not be removed from the storage; it is left behind.");
            }
        }
    }

    private static void Add(HashSet<string> keys, string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            keys.Add(key);
        }
    }

    /// <summary>
    /// Silinen bir tipten, zincirleme silme ile bir dosya kaynağına giden yollar. Her yol, silinen tipe işaret eden
    /// yabancı anahtardan başlar ve dosya kaynağında biter.
    /// </summary>
    private static ImmutableArray<ImmutableArray<IForeignKey>> Discover(IEntityType principal)
    {
        var paths = ImmutableArray.CreateBuilder<ImmutableArray<IForeignKey>>();
        Walk(principal, [], [principal]);
        return paths.ToImmutable();

        void Walk(IEntityType current, ImmutableArray<IForeignKey> path, ImmutableHashSet<IEntityType> visited)
        {
            foreach (var foreignKey in current.GetReferencingForeignKeys())
            {
                var dependent = foreignKey.DeclaringEntityType;

                // Yalnızca veritabanının kendisinin zincirleme sildiği, tek kolonlu ilişkiler izlenir.
                if (foreignKey.DeleteBehavior != DeleteBehavior.Cascade
                    || foreignKey.Properties.Count != 1
                    || dependent.IsOwned()
                    || dependent.GetTableName() is null
                    || visited.Contains(dependent))
                {
                    continue;
                }

                var next = path.Add(foreignKey);

                if (typeof(IHaldusFile).IsAssignableFrom(dependent.ClrType))
                {
                    paths.Add(next);
                }
                else if (next.Length < MaxDepth)
                {
                    Walk(dependent, next, visited.Add(dependent));
                }
            }
        }
    }

    /// <summary>
    /// Yolun sonundaki dosya kayıtlarının içerik anahtarlarını, yolun başındaki silinen kayıtların anahtarlarına göre
    /// bulan sorgu. Sorgu filtreleri bilerek uygulanmaz: veritabanı da zincirleme silerken onlara bakmaz.
    /// </summary>
    private static string Sql(ImmutableArray<IForeignKey> path)
    {
        var file = path[^1].DeclaringEntityType;
        var sql = new StringBuilder();
        sql.Append($"SELECT t0.{Column(file, file.FindProperty(nameof(IHaldusFile.StorageKey))!)} AS \"Value\" FROM {Table(file)} AS t0");

        var alias = 0;

        // Dosyadan başlayıp silinen kayda doğru, her ilişkinin üst tablosu birleştirilir.
        for (var index = path.Length - 1; index > 0; index--)
        {
            var foreignKey = path[index];
            var parent = foreignKey.PrincipalEntityType;
            sql.Append($" JOIN {Table(parent)} AS t{alias + 1} ON t{alias}.{Column(foreignKey.DeclaringEntityType, foreignKey.Properties[0])}");
            sql.Append($" = t{alias + 1}.{Column(parent, foreignKey.PrincipalKey.Properties[0])}");
            alias++;
        }

        var first = path[0];
        sql.Append($" WHERE t{alias}.{Column(first.DeclaringEntityType, first.Properties[0])} = ANY({{0}})");
        sql.Append($" AND t0.{Column(file, file.FindProperty(nameof(IHaldusFile.StorageKey))!)} IS NOT NULL");
        return sql.ToString();
    }

    /// <summary>
    /// Silinen kayıtların, yolun ilk ilişkisinin işaret ettiği anahtar değerleri; sorguya tipli bir dizi olarak verilir.
    /// Haldus'un anahtar tipleri dışında bir tip yolu izlenmez kılar.
    /// </summary>
    private static object[]? KeyValues(IForeignKey foreignKey, IEnumerable<EntityEntry> entries)
    {
        var property = foreignKey.PrincipalKey.Properties[0];
        var values = entries.Select(entry => entry.Property(property.Name).OriginalValue).OfType<object>().ToList();

        object? array = values.Count == 0
            ? null
            : property.ClrType switch
            {
                var type when type == typeof(Guid) => values.Cast<Guid>().ToArray(),
                var type when type == typeof(int) => values.Cast<int>().ToArray(),
                var type when type == typeof(long) => values.Cast<long>().ToArray(),
                var type when type == typeof(string) => values.Cast<string>().ToArray(),
                _ => null,
            };

        return array is null ? null : [array];
    }

    private static string Table(IEntityType entityType)
    {
        var name = Quote(entityType.GetTableName()!);
        return entityType.GetSchema() is { } schema ? Quote(schema) + "." + name : name;
    }

    private static string Column(IEntityType entityType, IProperty property) =>
        Quote(property.GetColumnName(StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema()))!);

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed class State
    {
        /// <summary>Sürmekte olan kaydetmelerin topladıkları; iç içe kaydetmelerin başlangıçları <see cref="Starts"/>'tadır.</summary>
        public List<string> Saving { get; } = [];

        public Stack<int> Starts { get; } = new();

        /// <summary>Kaydedilmiş, <see cref="Transaction"/>'ın commit edilmesini bekleyen anahtarlar.</summary>
        public List<string> Pending { get; } = [];

        /// <summary>Bekleyen anahtarların ve kayıt noktalarının ait olduğu transaction.</summary>
        public Guid? Transaction { get; set; }

        public Stack<int> Savepoints { get; } = new();
    }
}

/// <summary>Dosya içeriklerinin, silmenin kalıcı olduğu anda kaldırılması için transaction olaylarını izler.</summary>
internal sealed class HaldusFileTransactionInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
{
    public override System.Data.Common.DbTransaction TransactionStarted(
        System.Data.Common.DbConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData,
        System.Data.Common.DbTransaction result)
    {
        HaldusFileCleanup.Started(eventData.Context, eventData.TransactionId);
        return base.TransactionStarted(connection, eventData, result);
    }

    public override ValueTask<System.Data.Common.DbTransaction> TransactionStartedAsync(
        System.Data.Common.DbConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData,
        System.Data.Common.DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        HaldusFileCleanup.Started(eventData.Context, eventData.TransactionId);
        return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
    }

    public override void TransactionCommitted(System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData) =>
        HaldusFileCleanup.CommittedAsync(eventData.Context, eventData.TransactionId, CancellationToken.None).GetAwaiter().GetResult();

    public override Task TransactionCommittedAsync(
        System.Data.Common.DbTransaction transaction,
        Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        HaldusFileCleanup.CommittedAsync(eventData.Context, eventData.TransactionId, cancellationToken);

    public override void TransactionRolledBack(System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData) =>
        HaldusFileCleanup.RolledBack(eventData.Context, eventData.TransactionId);

    public override Task TransactionRolledBackAsync(
        System.Data.Common.DbTransaction transaction,
        Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        HaldusFileCleanup.RolledBack(eventData.Context, eventData.TransactionId);
        return Task.CompletedTask;
    }

    public override void CreatedSavepoint(System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData) =>
        HaldusFileCleanup.SavepointCreated(eventData.Context, eventData.TransactionId);

    public override Task CreatedSavepointAsync(
        System.Data.Common.DbTransaction transaction,
        Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
        CancellationToken cancellationToken = default)
    {
        HaldusFileCleanup.SavepointCreated(eventData.Context, eventData.TransactionId);
        return Task.CompletedTask;
    }

    public override void ReleasedSavepoint(System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData) =>
        HaldusFileCleanup.SavepointReleased(eventData.Context, eventData.TransactionId);

    public override Task ReleasedSavepointAsync(
        System.Data.Common.DbTransaction transaction,
        Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
        CancellationToken cancellationToken = default)
    {
        HaldusFileCleanup.SavepointReleased(eventData.Context, eventData.TransactionId);
        return Task.CompletedTask;
    }

    public override void RolledBackToSavepoint(System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData) =>
        HaldusFileCleanup.SavepointRolledBack(eventData.Context, eventData.TransactionId);

    public override Task RolledBackToSavepointAsync(
        System.Data.Common.DbTransaction transaction,
        Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
        CancellationToken cancellationToken = default)
    {
        HaldusFileCleanup.SavepointRolledBack(eventData.Context, eventData.TransactionId);
        return Task.CompletedTask;
    }
}
