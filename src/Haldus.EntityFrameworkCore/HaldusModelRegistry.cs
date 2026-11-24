using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>
/// Üretilen kodun EF Core model katkılarını kaydettiği yer. Katkılar modül (assembly) anahtarıyla
/// tutulur; böylece tek projeli ve modüler yapı aynı mekanizmayı kullanır.
/// </summary>
public static class HaldusModelRegistry
{
    private static readonly ConcurrentDictionary<Assembly, Action<ModelBuilder, DbContext>> Contributions = new();
    private static readonly ConcurrentDictionary<Assembly, Action<ModelBuilder, DbContext>> GlobalContributions = new();

    /// <param name="module">Katkıyı yapan assembly.</param>
    /// <param name="configure">
    /// Model yapılandırması. İkinci parametre modelin kurulduğu bağlamdır; sorgu filtreleri ona başvurur ve
    /// EF Core bu başvuruyu her sorguda geçerli bağlamla değiştirir.
    /// </param>
    public static void Register(Assembly module, Action<ModelBuilder, DbContext> configure)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(configure);
        Contributions[module] = configure;
    }

    /// <summary>
    /// Bir modüle ait olmayan, her bağlama sunulan katkı (ör. bir altyapı paketinin kendi tablosu). Hangi
    /// bağlama uygulanacağına katkının kendisi karar verir.
    /// </summary>
    public static void RegisterGlobal(Assembly owner, Action<ModelBuilder, DbContext> configure)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(configure);
        GlobalContributions[owner] = configure;
    }

    internal static void Apply(ModelBuilder modelBuilder, DbContext context)
    {
        var map = HaldusContextOptions.From(context);
        var contextType = context.GetType();

        foreach (var contribution in Contributions)
        {
            // Her bağlam yalnızca kendisine bağlı modüllerin entity'lerini alır.
            if (map is null || map.ContextOf(contribution.Key) == contextType)
            {
                contribution.Value(modelBuilder, context);
            }
        }

        foreach (var configure in GlobalContributions.Values)
        {
            configure(modelBuilder, context);
        }
    }
}
