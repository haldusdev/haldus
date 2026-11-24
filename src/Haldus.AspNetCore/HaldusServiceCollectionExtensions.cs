using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haldus;

public static class HaldusServiceCollectionExtensions
{
    private static readonly string[] SupportedCultures = ["en", "tr"];

    /// <summary>Bir assembly'nin Haldus modülü olabilmesi için başvurması gereken çalışma zamanı kütüphaneleri.</summary>
    private static readonly HashSet<string> RuntimeAssemblies =
        ["Haldus.Abstractions", "Haldus.EntityFrameworkCore", "Haldus.AspNetCore"];

    /// <summary>
    /// Haldus servislerini kaydeder ve <typeparamref name="TContext"/> bağlamını ortak bağlam yapar: kendi
    /// bağlamı olmayan bütün modüllerin entity'leri onun modeline eklenir. Tek projeli uygulamada bütün
    /// entity'ler bu bağlamdadır.
    /// </summary>
    public static IServiceCollection AddHaldus<TContext>(
        this IServiceCollection services,
        Action<HaldusOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        AddCore<TContext>(services, configure);
        services.Configure<HaldusContextOptions>(options => options.DefaultContext = typeof(TContext));

        return services;
    }

    /// <summary>
    /// Bir modüle kendi bağlamını verir: <typeparamref name="TContext"/> ile aynı assembly'de üretilen
    /// entity'ler yalnızca bu bağlamın modeline eklenir. Modül böylece kendi şemasına ve migration'larına
    /// sahip olur. Ayrı bağlamlardaki entity'ler arasında yabancı anahtar kısıtı ve ortak transaction olmaz;
    /// <c>References&lt;T&gt;()</c> başka modüldeki kayda yalnızca kimliğiyle bağlanır.
    /// </summary>
    /// <summary>
    /// Dosya içeriklerini (<c>MapFile</c>) veritabanı yerine verilen klasörde saklar. Birden çok sunucu aynı klasörü
    /// görmelidir.
    /// </summary>
    public static IServiceCollection AddHaldusFileSystemStorage(this IServiceCollection services, string root)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<IHaldusFileStorage>();
        services.AddSingleton<IHaldusFileStorage>(new HaldusFileSystemStorage(root));
        return services;
    }

    public static IServiceCollection AddHaldusModule<TContext>(
        this IServiceCollection services,
        Action<HaldusOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        AddCore<TContext>(services, configure);
        services.Configure<HaldusContextOptions>(options => options.ModuleContexts[typeof(TContext).Assembly] = typeof(TContext));

        return services;
    }

    private static void AddCore<TContext>(IServiceCollection services, Action<HaldusOptions>? configure)
        where TContext : DbContext
    {
        services.AddOptions<HaldusOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddOptions<HaldusContextOptions>();
        services.ConfigureDbContext<TContext>(options => options.UseHaldus());
        services.TryAddScoped<IHaldusDbContextProvider, HaldusDbContextProvider>();

        // TryAdd: tüketen proje kendi uygulamalarını Haldus'tan önce veya sonra kaydedebilir.
        services.AddHttpContextAccessor();

        // Geri okunabilen sırlar (SecretOnce(recoverable: true)) bununla şifrelenir; çağrı yinelenebilir.
        services.AddDataProtection();
        services.TryAddSingleton<ITenantProvider, ClaimsTenantProvider>();
        services.TryAddSingleton<ICurrentUser, ClaimsCurrentUser>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IHaldusMessages, ResourceHaldusMessages>();

        // Dosya içerikleri varsayılan olarak dosya kayıtlarıyla aynı veritabanında tutulur.
        services.TryAddSingleton<IHaldusFileStorage, HaldusDatabaseFileStorage>();

        // İşlerin yürütücüsü altyapıdan bağımsızdır; işleri ona ulaştıran altyapı ayrı bir paketle seçilir.
        services.AddOptions<HaldusJobOptions>();
        services.TryAddSingleton<HaldusJobRunner>();

        if (!services.Any(service => service.ImplementationType == typeof(HaldusMaintenanceService)))
        {
            services.AddHostedService<HaldusMaintenanceService>();
        }

        // Modüllerin üretilen kayıt kodu, assembly'leri ilk kullanıldığında çalışır. Bir modül henüz
        // kullanılmadıysa handler sınıfları burada görünmezdi; bu yüzden bağlamın assembly'sinden ulaşılan
        // modüller şimdi yüklenir.
        var modules = new List<Assembly>();
        LoadModules(typeof(TContext).Assembly, [], modules);

        services.Configure<HaldusContextOptions>(options => options.Modules.UnionWith(modules));

        // Generator'ın bulduğu handler sınıfları. TryAdd kullanıldığı için yinelenen çağrı zararsızdır.
        HaldusServiceRegistry.Apply(services, modules);

        services.AddProblemDetails();
        services.Configure<RequestLocalizationOptions>(options => options
            .SetDefaultCulture(SupportedCultures[0])
            .AddSupportedCultures(SupportedCultures)
            .AddSupportedUICultures(SupportedCultures));
    }

    /// <summary>
    /// Verilen assembly'nin ve ondan başvurulan Haldus modüllerinin üretilen kayıt kodunu çalıştırır.
    /// Yalnızca Haldus'un çalışma zamanına başvuran assembly'ler izlenir; çerçeve kütüphanelerine girilmez.
    /// </summary>
    private static void LoadModules(Assembly assembly, HashSet<string> visited, List<Assembly> modules)
    {
        if (!visited.Add(assembly.FullName ?? assembly.GetName().Name ?? string.Empty))
        {
            return;
        }

        var references = assembly.GetReferencedAssemblies();
        if (!references.Any(reference => RuntimeAssemblies.Contains(reference.Name ?? string.Empty)))
        {
            return;
        }

        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        modules.Add(assembly);

        foreach (var reference in references)
        {
            if (RuntimeAssemblies.Contains(reference.Name ?? string.Empty) || IsFramework(reference))
            {
                continue;
            }

            Assembly loaded;

            try
            {
                loaded = Assembly.Load(reference);
            }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                // Yüklenemeyen bir başvuru modül değildir.
                continue;
            }

            LoadModules(loaded, visited, modules);
        }
    }

    private static bool IsFramework(AssemblyName name) =>
        name.Name is { } simple
        && (simple.StartsWith("System.", StringComparison.Ordinal)
            || simple.StartsWith("Microsoft.", StringComparison.Ordinal)
            || simple is "System" or "netstandard" or "mscorlib");
}