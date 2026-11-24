using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Haldus;

/// <summary>
/// Başka kaynaklardan toplanan alanlar (<c>.Counts&lt;T&gt;()</c>, <c>.Sums&lt;T&gt;()</c>): bir kaydın, kendisine bağlı
/// kayıtlardan hesaplanan sayısı veya toplamı. Bağlı kayıt hangi koddan yazılırsa yazılsın, kaydedildikten hemen
/// sonra ve aynı transaction içinde üst kaydın alanı baştan hesaplanır; artırıp azaltmak yerine yeniden saymak,
/// kaçırılmış bir yazmanın sonsuza dek yanlış bir sayı bırakmasını önler.
/// </summary>
public static class HaldusRollups
{
    private static readonly ConcurrentDictionary<Type, List<Rollup>> ByChild = new();
    private static readonly ConditionalWeakTable<DbContext, State> Pending = [];

    // Bağlı kaydı sayıdan düşüren, kullanıcının yazmadığı alanlar: yumuşak silme ve süre dolumu.
    private static readonly string[] VisibilityFields = [nameof(ISoftDeletable.DeletedAt), "ExpiresAt"];

    // Sayı, isteği yapanın kim olduğuna göre değişmemelidir; kaydı yalnızca kimliğe göre gizleyen filtreler kalkar.
    // Kiracılar arası toplama derleme zamanında reddedilir; kullanıcılar arası toplama ise ancak açıkça
    // istendiyse (acrossOwners) derlenir. Burada kaldırılan filtreler o kararların çalışma zamanındaki karşılığıdır.
    private static readonly string[] IdentityFilters = [HaldusFilters.Tenant, HaldusFilters.Owner, HaldusFilters.Parent];

    /// <summary>
    /// Bir üst kaydın, bir alt kaynaktan toplanan alanlarını bildirir. Üretilen modül kodu çağırır.
    /// </summary>
    /// <param name="foreignKey">Alt kayıtta üst kayda işaret eden alan.</param>
    /// <param name="fields">Toplanan alanlar; alt kayıt güncellenince yalnızca bunlar değiştiyse yeniden hesaplanır.</param>
    /// <param name="recompute">Verilen anahtarlı üst kaydın alanlarını baştan hesaplayıp yazar.</param>
    public static void Register<TParent, TChild>(
        string foreignKey,
        string[] fields,
        Func<DbContext, object, CancellationToken, Task> recompute)
        where TParent : class
        where TChild : class
    {
        ArgumentException.ThrowIfNullOrEmpty(foreignKey);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(recompute);

        var rollups = ByChild.GetOrAdd(typeof(TChild), _ => []);

        lock (rollups)
        {
            rollups.RemoveAll(existing => existing.Parent == typeof(TParent));
            rollups.Add(new Rollup(typeof(TParent), foreignKey, [foreignKey, .. fields, .. VisibilityFields], recompute));
        }
    }

    /// <summary>
    /// Sayılacak kayıtlar. Silinmiş, süresi dolmuş ve yayın kopyası olan kayıtlar sayılmaz; kiracı, sahip ve ebeveyn
    /// filtreleri ise kalkar: sayı, alt kaydı yazan kullanıcının neyi görebildiğine bağlı olamaz.
    /// </summary>
    public static IQueryable<TChild> Children<TChild>(DbContext context)
        where TChild : class
    {
        ArgumentNullException.ThrowIfNull(context);

        var declared = context.Model.FindEntityType(typeof(TChild))?.GetDeclaredQueryFilters()
            .Select(filter => filter.Key)
            .OfType<string>()
            .Where(key => IdentityFilters.Contains(key))
            .ToArray() ?? [];

        var children = context.Set<TChild>().AsNoTracking();
        return declared.Length == 0 ? children : children.IgnoreQueryFilters(declared);
    }

    /// <summary>
    /// Alanları yazılacak üst kayıt. Anahtarı az önce kaydedilen alt kayıttan gelir; üst kayıt, alt kaydı yazana
    /// görünmese de (başkasının kaydına yapılan yorum) alanı güncellenmelidir.
    /// </summary>
    public static IQueryable<TParent> Parents<TParent>(DbContext context)
        where TParent : class
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Set<TParent>().IgnoreQueryFilters();
    }

    /// <summary>
    /// Alanlar yazıldıktan sonra üst kaydı tamamlar: kayıt bu bağlamda izleniyorsa yeni değerleri (ve satır sürümünü)
    /// görür; kaydın hesaplanan alanları varsa yeniden hesaplanır ve değiştiyse kaydedilir.
    /// </summary>
    /// <param name="computed">Üst kaydı, hesaplamanın ihtiyaç duyduğu satırlarıyla yükleyen sorgu; hesaplanan alan yoksa <c>null</c>.</param>
    public static async Task FinishAsync<TParent>(
        DbContext context,
        object key,
        IQueryable<TParent>? computed,
        CancellationToken cancellationToken)
        where TParent : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(key);

        if (context.Set<TParent>().Local.FindEntryUntyped([key]) is { State: EntityState.Unchanged } tracked)
        {
            await tracked.ReloadAsync(cancellationToken);
        }

        if (computed is null)
        {
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            if (await computed.FirstOrDefaultAsync(cancellationToken) is not { } parent)
            {
                return;
            }

            if (parent is IComputed calculated)
            {
                calculated.Compute();
            }

            try
            {
                // Hesaplama hiçbir şeyi değiştirmediyse bu kaydetme hiçbir şey yazmaz.
                await context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // Üst kaydı aynı anda başka bir istek yazdı; onun yazdığının üzerine yeniden hesaplanır.
                await context.Entry(parent).ReloadAsync(cancellationToken);
            }
        }
    }

    /// <summary>Kaydedilmek üzere olan alt kayıtların üst kayıtlarını not eder.</summary>
    internal static void Collect(DbContext context)
    {
        if (ByChild.IsEmpty)
        {
            return;
        }

        List<(Rollup, object)>? affected = null;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)
                || !ByChild.TryGetValue(entry.Metadata.ClrType, out var rollups))
            {
                continue;
            }

            Rollup[] candidates;

            lock (rollups)
            {
                candidates = [.. rollups];
            }

            foreach (var rollup in candidates)
            {
                // Toplamı etkilemeyen bir güncelleme (yorumun metni) üst kaydı yeniden saydırmaz.
                if (entry.State == EntityState.Modified
                    && !entry.Properties.Any(property => property.IsModified && rollup.Watched.Contains(property.Metadata.Name)))
                {
                    continue;
                }

                var foreignKey = entry.Property(rollup.ForeignKey);

                // Alt kayıt başka bir üst kayda taşındıysa ikisi de yeniden hesaplanır.
                foreach (var key in new[]
                {
                    entry.State == EntityState.Added ? null : foreignKey.OriginalValue,
                    entry.State == EntityState.Deleted ? null : foreignKey.CurrentValue,
                })
                {
                    if (key is not null)
                    {
                        (affected ??= []).Add((rollup, key));
                    }
                }
            }
        }

        if (affected is not null)
        {
            Pending.GetOrCreateValue(context).Work.AddRange(affected);
        }
    }

    /// <summary>
    /// Toplanacak bir alan varsa ve açık bir transaction yoksa yenisini açar: alt kayıt ile üst kaydın alanı ya
    /// birlikte kalıcı olur ya da hiçbiri olmaz.
    /// </summary>
    internal static async ValueTask BeginAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (Pending.TryGetValue(context, out var state) && state.Work.Count > 0 && context.Database.CurrentTransaction is null)
        {
            state.Transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        }
    }

    /// <inheritdoc cref="BeginAsync"/>
    internal static void Begin(DbContext context)
    {
        if (Pending.TryGetValue(context, out var state) && state.Work.Count > 0 && context.Database.CurrentTransaction is null)
        {
            state.Transaction = context.Database.BeginTransaction();
        }
    }

    /// <summary>Kaydetme bittikten sonra, not edilen üst kayıtların alanlarını baştan hesaplar.</summary>
    internal static async Task ApplyAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (!Pending.TryGetValue(context, out var state) || (state.Work.Count == 0 && state.Transaction is null))
        {
            return;
        }

        // Yeniden hesaplama da kaydedebilir; o kaydetme bu listeyi yeniden gezmesin ve transaction'ı kapatmasın
        // diye ikisi de önce alınır.
        var transaction = state.Transaction;
        state.Transaction = null;

        // Birden çok üst kaydı kilitleyen iki istek birbirini beklemesin diye kilitler hep aynı sırayla alınır.
        var work = state.Work
            .Distinct()
            .OrderBy(item => item.Rollup.Parent.FullName, StringComparer.Ordinal)
            .ThenBy(item => Convert.ToString(item.Key, System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .ToList();
        state.Work.Clear();

        try
        {
            foreach (var (rollup, key) in work)
            {
                // Aynı üst kaydın alt kayıtlarını aynı anda yazan iki istekten ikincisi, birincisi bitene kadar bekler
                // ve onun yazdığını da sayar; kilit olmadan ikisi de ötekinin kaydını göremezdi.
                await using var scope = await HaldusOrdering.BeginAsync(context, "Rollup:" + rollup.Parent.FullName, cancellationToken, key);
                await rollup.Recompute(context, key, cancellationToken);
                await scope.CommitAsync(cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>Kaydetme başarısız olduysa not edilenler bırakılır ve burada açılan transaction geri alınır.</summary>
    internal static void Forget(DbContext context)
    {
        if (!Pending.TryGetValue(context, out var state))
        {
            return;
        }

        state.Work.Clear();
        state.Transaction?.Dispose();
        state.Transaction = null;
    }

    private sealed record Rollup(Type Parent, string ForeignKey, string[] Watched, Func<DbContext, object, CancellationToken, Task> Recompute);

    private sealed class State
    {
        public List<(Rollup Rollup, object Key)> Work { get; } = [];

        public IDbContextTransaction? Transaction { get; set; }
    }
}
