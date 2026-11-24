using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haldus;

public static class HaldusServiceCollectionExtensions
{
    private static readonly string[] SupportedCultures = ["en", "tr"];

    /// <summary>
    /// Haldus servislerini kaydeder ve üretilen tüm entity'leri <typeparamref name="TContext"/> modeline ekler.
    /// </summary>
    public static IServiceCollection AddHaldus<TContext>(
        this IServiceCollection services,
        Action<HaldusOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<HaldusOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.ConfigureDbContext<TContext>(options => options.UseHaldus());
        services.AddScoped<IHaldusDbContextProvider, SingleDbContextProvider<TContext>>();

        // TryAdd: tüketen proje kendi uygulamalarını Haldus'tan önce veya sonra kaydedebilir.
        services.AddHttpContextAccessor();
        services.TryAddSingleton<ITenantProvider, ClaimsTenantProvider>();
        services.TryAddSingleton<ICurrentUser, ClaimsCurrentUser>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IHaldusMessages, ResourceHaldusMessages>();

        // İşlerin yürütücüsü altyapıdan bağımsızdır; işleri ona ulaştıran altyapı ayrı bir paketle seçilir.
        services.AddOptions<HaldusJobOptions>();
        services.TryAddSingleton<HaldusJobRunner>();

        // Generator'ın bulduğu handler sınıfları.
        HaldusServiceRegistry.Apply(services);

        services.AddProblemDetails();
        services.Configure<RequestLocalizationOptions>(options => options
            .SetDefaultCulture(SupportedCultures[0])
            .AddSupportedCultures(SupportedCultures)
            .AddSupportedUICultures(SupportedCultures));

        return services;
    }
}
