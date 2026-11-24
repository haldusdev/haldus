using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haldus;

public sealed class InMemoryJobOptions
{
    /// <summary>Aynı anda en fazla kaç iş çalışır.</summary>
    public int MaxConcurrency { get; set; } = 4;
}

public static class HaldusInMemoryJobsExtensions
{
    /// <summary>
    /// İşleri sürecin belleğindeki bir kuyrukta bekletir ve aynı süreçte çalıştırır. Geliştirme ve tek
    /// sunuculu küçük kurulumlar içindir: süreç yeniden başlarsa bekleyen ve çalışan işler kaybolur (kayıtları
    /// <c>Queued</c> veya <c>Running</c> olarak kalır) ve kuyruk birden çok sunucu arasında paylaşılmaz.
    /// </summary>
    public static IServiceCollection AddHaldusInMemoryJobs(this IServiceCollection services, Action<InMemoryJobOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<InMemoryJobOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<InMemoryJobQueue>();
        services.Replace(ServiceDescriptor.Singleton<IHaldusJobDispatcher>(provider => provider.GetRequiredService<InMemoryJobQueue>()));
        services.AddHostedService<InMemoryJobWorker>();

        return services;
    }
}

internal sealed class InMemoryJobQueue : IHaldusJobDispatcher
{
    private readonly Channel<HaldusJobTicket> _channel = Channel.CreateUnbounded<HaldusJobTicket>();

    public ChannelReader<HaldusJobTicket> Reader => _channel.Reader;

    public void Prepare(DbContext context, HaldusJobTicket ticket)
    {
        // Kuyruk veritabanında değil; kayıtla birlikte yazılacak bir şey yok.
    }

    public ValueTask DispatchAsync(HaldusJobTicket ticket, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(ticket, cancellationToken);
}

internal sealed class InMemoryJobWorker(
    InMemoryJobQueue queue,
    HaldusJobRunner runner,
    IOptions<InMemoryJobOptions> options,
    ILogger<InMemoryJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HaldusJobs.IsGeneratingOpenApiDocument)
        {
            return;
        }

        using var slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrency));
        var running = new List<Task>();

        try
        {
            await foreach (var ticket in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await slots.WaitAsync(stoppingToken);
                running.RemoveAll(task => task.IsCompleted);
                running.Add(RunAsync(ticket, slots, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Süreç kapanıyor.
        }

        await Task.WhenAll(running);
    }

    private async Task RunAsync(HaldusJobTicket ticket, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            await runner.RunAsync(ticket, stoppingToken);
        }
#pragma warning disable CA1031 // Tek bir işin altyapı hatası kuyruğu durdurmamalıdır.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            logger.LogError(exception, "Job {JobId} of type '{JobType}' could not be run.", ticket.JobId, ticket.JobType);
        }
        finally
        {
            slots.Release();
        }
    }
}
