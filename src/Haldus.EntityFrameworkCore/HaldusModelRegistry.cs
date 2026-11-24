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

    internal static void Apply(ModelBuilder modelBuilder, DbContext context)
    {
        foreach (var configure in Contributions.Values)
        {
            configure(modelBuilder, context);
        }
    }
}
