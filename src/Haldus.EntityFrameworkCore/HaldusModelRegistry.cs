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
    private static readonly ConcurrentDictionary<Assembly, Action<ModelBuilder>> Contributions = new();

    public static void Register(Assembly module, Action<ModelBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(configure);
        Contributions[module] = configure;
    }

    internal static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var configure in Contributions.Values)
        {
            configure(modelBuilder);
        }
    }
}
