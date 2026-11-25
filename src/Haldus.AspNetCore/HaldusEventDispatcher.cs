using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haldus;

/// <summary>
/// Bekleyen olayları uygulamanın yayıncılarına (<see cref="IHaldusEventPublisher"/>) teslim eder. Arka plandaki servis
/// bunu kendiliğinden yapar; aynı yöntem testlerden ve yönetim komutlarından doğrudan çağrılabilir.
/// </summary>
public static class HaldusEventDispatcher
{
    /// <summary>Bekleyen olayların bir turunu teslim eder.</summary>
    /// <returns>Teslim edilen olayların sayısı. Kayıtlı yayıncı yoksa hiçbir olaya dokunulmaz.</returns>
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var publishers = provider.GetServices<IHaldusEventPublisher>().ToList();

        if (publishers.Count == 0)
        {
            // Teslim edecek kimse yok; olaylar bir yayıncı kaydedilene kadar bekler.
            return 0;
        }

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HaldusEventDispatcher).FullName!);
        var delivered = 0;

        foreach (var context in provider.GetRequiredService<IHaldusDbContextProvider>().All)
        {
            // Tablo yalnızca o bağlamda olay yayınlayan bir kaynak varsa modeldedir.
            if (context.Model.FindEntityType(typeof(HaldusOutboxEvent)) is null)
            {
                continue;
            }

            delivered += await HaldusEvents.DeliverAsync(
                context,
                async (published, token) =>
                {
                    foreach (var publisher in publishers)
                    {
                        await publisher.PublishAsync(published, token);
                    }
                },
                (item, exception) => logger.LogWarning(
                    exception,
                    "The event {Type} #{Id} could not be published (attempt {Attempt}); {Outcome}.",
                    item.Type,
                    item.Id,
                    item.Attempts,
                    item.DeadAt is null ? "it will be retried" : "it has been given up"),
                cancellationToken);
        }

        return delivered;
    }
}

internal sealed class HaldusEventService(
    IServiceProvider services,
    IOptions<HaldusOptions> options,
    ILogger<HaldusEventService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HaldusJobs.IsGeneratingOpenApiDocument)
        {
            // Build sırasındaki şema üretimi veritabanına bağlanmamalıdır.
            return;
        }

        await using (var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            if (!scope.ServiceProvider.GetServices<IHaldusEventPublisher>().Any())
            {
                if (HaldusEvents.HasSources)
                {
                    // Olaylar yazılıyor ama teslim edecek kimse yok; bekleme tablosu büyür.
                    logger.LogWarning(
                        "Some resources publish events, but no IHaldusEventPublisher is registered. Events will wait in the outbox until one is.");
                }

                return;
            }
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Bir tur dolu geldiyse beklemeden devam edilir.
                    while (await HaldusEventDispatcher.RunAsync(services, stoppingToken) >= HaldusEvents.BatchSize)
                    {
                    }
                }
#pragma warning disable CA1031 // Bir tur başarısız olursa servis durmamalı, bir sonraki turda yeniden denemelidir.
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
                {
                    logger.LogWarning(exception, "Haldus event delivery failed; it will run again shortly.");
                }

                await HaldusEvents.WaitAsync(options.Value.EventPollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Süreç kapanıyor.
        }
    }
}
