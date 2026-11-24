using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haldus;

/// <summary>Üretilen kodun, bakım sırasında temizlenebilecek kaynakları kaydettiği yer.</summary>
public static class HaldusMaintenanceRegistry
{
    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, DateTimeOffset, CancellationToken, Task<int>>> Expiring = new();

    /// <summary><c>.Expiring()</c> taşıyan bir kaynağı kaydeder; süresi yeterince önce dolmuş kayıtları silinebilir.</summary>
    public static void RegisterExpiring<TEntity>()
        where TEntity : class =>
        Expiring[typeof(TEntity)] = static (services, cutoff, cancellationToken) =>
            services.GetRequiredService<IHaldusDbContextProvider>().For<TEntity>().Set<TEntity>()

                // Bakım bir isteğe ait değildir: kiracı ve süre filtreleri bilerek kaldırılır, yoksa silinecek
                // kayıtlar zaten görünmezdi.
                .IgnoreQueryFilters()
                .Where(entity => EF.Property<DateTimeOffset?>(entity, "ExpiresAt") <= cutoff)
                .ExecuteDeleteAsync(cancellationToken);

    internal static IEnumerable<KeyValuePair<Type, Func<IServiceProvider, DateTimeOffset, CancellationToken, Task<int>>>> ExpiringResources =>
        Expiring;
}

/// <summary>
/// Haldus'un biriktirdiği kayıtların temizliği: saklama süresi geçmiş idempotency yanıtları ve, istenmişse,
/// süresi dolmuş kayıtlar. Arka planda aralıklarla çalışır; bir test veya yönetim komutu da doğrudan çağırabilir.
/// </summary>
public static class HaldusMaintenance
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var options = provider.GetRequiredService<IOptions<HaldusOptions>>().Value;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HaldusMaintenance).FullName!);
        var now = HaldusTime.UtcNow(provider.GetRequiredService<TimeProvider>());

        try
        {
            var context = provider.GetRequiredService<IHaldusDbContextProvider>().For<HaldusIdempotencyRecord>();

            // Tablo yalnızca idempotent bir kaynak varsa modeldedir.
            if (context.Model.FindEntityType(typeof(HaldusIdempotencyRecord)) is not null)
            {
                var removed = await context.Set<HaldusIdempotencyRecord>()
                    .Where(record => record.ExpiresAt <= now)
                    .ExecuteDeleteAsync(cancellationToken);

                logger.LogDebug("Removed {Count} expired idempotency records.", removed);
            }
        }
        catch (DbException exception)
        {
            logger.LogWarning(exception, "Expired idempotency records could not be removed.");
        }

        // Süresi dolan kayıtlar kullanıcının verisidir; ancak açıkça istenirse silinir.
        if (options.PurgeExpiredAfter is not { } retention)
        {
            return;
        }

        foreach (var resource in HaldusMaintenanceRegistry.ExpiringResources)
        {
            try
            {
                var removed = await resource.Value(provider, now - retention, cancellationToken);
                logger.LogDebug("Purged {Count} expired '{Resource}' records.", removed, resource.Key.Name);
            }
            catch (DbException exception)
            {
                // Başka kayıtlar hâlâ referans veriyor olabilir; o kayıtlar bir sonraki turda yeniden denenir.
                logger.LogWarning(exception, "Expired '{Resource}' records could not be purged.", resource.Key.Name);
            }
        }
    }
}

internal sealed class HaldusMaintenanceService(
    IServiceProvider services,
    IOptions<HaldusOptions> options,
    TimeProvider timeProvider,
    ILogger<HaldusMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HaldusJobs.IsGeneratingOpenApiDocument)
        {
            // Build sırasındaki şema üretimi veritabanına bağlanmamalıdır.
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(options.Value.MaintenanceInterval, timeProvider, stoppingToken);

                try
                {
                    await HaldusMaintenance.RunAsync(services, stoppingToken);
                }
#pragma warning disable CA1031 // Bakımın bir turu başarısız olursa servis durmamalı, bir sonraki turda yeniden denemelidir.
                catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    logger.LogWarning(exception, "Haldus maintenance failed; it will run again later.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Süreç kapanıyor.
        }
    }
}
