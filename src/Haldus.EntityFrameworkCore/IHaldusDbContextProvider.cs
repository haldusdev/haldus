using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Haldus;

/// <summary>
/// Üretilen endpoint'lerin bir entity için kullanacağı bağlamı verir. Üretilen kod somut
/// <see cref="DbContext"/> tipini bilmez; ortak ve modül başına bağlam modları bu sayede aynı kodu kullanır.
/// </summary>
public interface IHaldusDbContextProvider
{
    DbContext For<TEntity>() where TEntity : class;

    /// <summary>Haldus'un kullandığı bütün bağlamlar; bir entity'ye bağlı olmayan işler (bakım gibi) için.</summary>
    IReadOnlyList<DbContext> All { get; }
}

/// <summary>
/// Hangi modülün (assembly'nin) hangi bağlamı kullandığı. <c>AddHaldus&lt;TContext&gt;()</c> ortak bağlamı,
/// <c>AddHaldusModule&lt;TContext&gt;()</c> o bağlamın bulunduğu modülün kendi bağlamını bildirir; ikisi
/// birlikte de kullanılabilir.
/// </summary>
public sealed class HaldusContextOptions
{
    /// <summary>Kendi bağlamı olmayan modüllerin kullandığı ortak bağlam.</summary>
    public Type? DefaultContext { get; set; }

    /// <summary>Kendi bağlamı olan modüller.</summary>
    public IDictionary<Assembly, Type> ModuleContexts { get; } = new Dictionary<Assembly, Type>();

    /// <summary>
    /// Uygulamanın parçası olan modüller: bağlamların assembly'lerinden başvurularla ulaşılanlar. Ortak bağlam
    /// yalnızca bunları sahiplenir; süreçte yüklü ama uygulamanın başvurmadığı bir modülün entity'leri hiçbir
    /// bağlama girmez.
    /// </summary>
    public ISet<Assembly> Modules { get; } = new HashSet<Assembly>();

    public IEnumerable<Type> Contexts =>
        ModuleContexts.Values.Concat(DefaultContext is null ? [] : [DefaultContext]).Distinct();

    /// <summary>Verilen modülün entity'lerinin yaşadığı bağlam; modül hiçbir bağlama bağlı değilse <c>null</c>.</summary>
    public Type? ContextOf(Assembly module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return ModuleContexts.TryGetValue(module, out var context) ? context : Modules.Contains(module) ? DefaultContext : null;
    }

    /// <summary>
    /// Bağlamın, uygulamanın servislerinden okunan eşlemesi. Bağlam Haldus servisleri olmadan kurulduysa
    /// (ör. yalnızca <c>UseHaldus()</c> ile) <c>null</c> döner ve tek bağlam varsayılır.
    /// </summary>
    internal static HaldusContextOptions? From(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
            ?.GetService<IOptions<HaldusContextOptions>>()?.Value;
}

/// <summary>Her entity'yi, modülünün bağlı olduğu bağlama yönlendirir.</summary>
public sealed class HaldusDbContextProvider(IServiceProvider services, IOptions<HaldusContextOptions> options) : IHaldusDbContextProvider
{
    public IReadOnlyList<DbContext> All => [.. options.Value.Contexts.Select(type => (DbContext)services.GetRequiredService(type))];

    public DbContext For<TEntity>()
        where TEntity : class
    {
        var module = typeof(TEntity).Assembly;

        return options.Value.ContextOf(module) is { } context
            ? (DbContext)services.GetRequiredService(context)
            : throw new InvalidOperationException(
                $"No DbContext is registered for '{typeof(TEntity).FullName}'. Call AddHaldus<TContext>() to use one shared context, " +
                $"or AddHaldusModule<TContext>() with a context declared in '{module.GetName().Name}'.");
    }
}
