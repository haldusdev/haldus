using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Haldus;

/// <summary>Üretilen oluşturma uçlarının, yeni kayıttan türeyen kaynakların izdüşümlerini çağırdığı yer.</summary>
public static class HaldusDerivations
{
    /// <summary>
    /// Türetmeyi sıraya sokar: izdüşüm, türeyen kaydı okuyup değiştirir; aynı anda çalışan iki izdüşüm aynı kaydı
    /// okursa biri ötekinin yazdığını ezerdi. Kilit transaction bitene kadar tutulur ve yalnızca aynı kaynaktan
    /// türeyenleri bekletir. Dönen sınır, kayıt kaydedildikten sonra <see cref="OrderingScope.CommitAsync"/> ile
    /// kapatılır; kapatılmadan atılırsa burada açılan transaction geri alınır.
    /// </summary>
    /// <param name="source">Kaynağın adı; kilidi ayırır.</param>
    public static Task<OrderingScope> BeginAsync(DbContext context, string source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        return HaldusOrdering.BeginAsync(context, "Haldus.Derived:" + source, cancellationToken);
    }

    /// <summary>
    /// <typeparamref name="TSource"/> kaydından türeyen <typeparamref name="TTarget"/> kayıtlarını günceller.
    /// Değişiklikler kaynağın bağlamına yazılır ve onunla birlikte kaydedilir; bu yüzden ikisi aynı bağlamda
    /// olmalıdır.
    /// </summary>
    public static Task ApplyAsync<TSource, TTarget>(
        DbContext context,
        TSource source,
        IHaldusDbContextProvider contexts,
        CancellationToken cancellationToken)
        where TSource : class
        where TTarget : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(contexts);

        if (!ReferenceEquals(contexts.For<TTarget>(), context))
        {
            throw new InvalidOperationException(
                $"'{typeof(TTarget).Name}' is derived from '{typeof(TSource).Name}', but they live in different DbContexts. " +
                "A derived resource is written in the same transaction as its source, so both must share a context.");
        }

        IDerivation<TSource, TTarget> derivation;

        try
        {
            // İzdüşüm uygulamanın servisidir; bağlam onu, kendisini oluşturan kapsamdan çözer.
            derivation = context.GetService<IDerivation<TSource, TTarget>>();
        }
        catch (InvalidOperationException missing)
        {
            throw new InvalidOperationException(
                $"'{typeof(TTarget).Name}' is declared as derived from '{typeof(TSource).Name}', but no " +
                $"IDerivation<{typeof(TSource).Name}, {typeof(TTarget).Name}> is registered. Write a class that implements it.",
                missing);
        }

        return derivation.ApplyAsync(source, new DerivedSet<TTarget>(context.Set<TTarget>()), cancellationToken);
    }

    private sealed class DerivedSet<TTarget>(DbSet<TTarget> set) : IDerivedSet<TTarget>
        where TTarget : class
    {
        public IQueryable<TTarget> Records => set;

        public void Add(TTarget record) => set.Add(record);

        public void Remove(TTarget record) => set.Remove(record);
    }
}
