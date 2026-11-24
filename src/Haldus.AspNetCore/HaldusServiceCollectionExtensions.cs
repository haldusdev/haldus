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
    public static IServiceCollection AddHaldus<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.ConfigureDbContext<TContext>(options => options.UseHaldus());
        services.AddScoped<IHaldusDbContextProvider, SingleDbContextProvider<TContext>>();
        services.TryAddSingleton<IHaldusMessages, ResourceHaldusMessages>();
        services.AddProblemDetails();
        services.Configure<RequestLocalizationOptions>(options => options
            .SetDefaultCulture(SupportedCultures[0])
            .AddSupportedCultures(SupportedCultures)
            .AddSupportedUICultures(SupportedCultures));

        return services;
    }
}
