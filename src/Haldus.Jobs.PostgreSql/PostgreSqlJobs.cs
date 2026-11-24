using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haldus;

public sealed class PostgreSqlJobOptions
{
    /// <summary>Bu sunucuda aynı anda en fazla kaç iş çalışır.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Kuyruğa ne sıklıkla bakılır. Aynı sunucuda oluşturulan iş beklemeden alınır.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Bir işi alan sunucunun onu ne kadar süre elinde tuttuğu. Sunucu çalıştıkça süreyi uzatır; çökerse süre
    /// dolar ve iş başka bir sunucuya (veya yeniden başlayınca aynısına) yeniden teslim edilir.
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Çalıştırılamayan bir kuyruk kaydının en fazla kaç kez teslim edileceği; sonra bırakılır.</summary>
    public int MaxDeliveries { get; set; } = 10;
}

/// <summary>Çalıştırılmayı bekleyen bir işin kuyruk kaydı. İş sonuçlanınca silinir.</summary>
public sealed class HaldusQueuedJob
{
    public Guid Id { get; set; }

    public string JobType { get; set; } = "";

    public Guid JobId { get; set; }

    public DateTimeOffset AvailableAt { get; set; }

    /// <summary>Kaydı alan sunucunun onu elinde tuttuğu son an; boşsa veya geçmişteyse kayıt alınabilir.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public int Deliveries { get; set; }
}

public static class HaldusPostgreSqlJobsExtensions
{
    /// <summary>
    /// İşleri <typeparamref name="TContext"/> veritabanındaki bir kuyruk tablosunda bekletir. Kuyruk kaydı iş
    /// kaydıyla aynı transaction'da yazılır; yeniden başlatmada işler kaybolmaz ve birden çok sunucu aynı
    /// kuyruğu güvenle paylaşır (<c>FOR UPDATE SKIP LOCKED</c>). İş entity'leri bu bağlamda olmalıdır.
    /// </summary>
    public static IServiceCollection AddHaldusPostgreSqlJobs<TContext>(
        this IServiceCollection services,
        Action<PostgreSqlJobOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<PostgreSqlJobOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        // Kuyruk tablosu yalnızca seçilen bağlamın modeline girer.
        HaldusModelRegistry.Register(typeof(HaldusQueuedJob).Assembly, static (modelBuilder, context) =>
        {
            if (context is TContext)
            {
                modelBuilder.Entity<HaldusQueuedJob>(entity =>
                {
                    entity.ToTable("HaldusJobQueue");
                    entity.Property(job => job.Id).ValueGeneratedNever();
                    entity.Property(job => job.JobType).HasMaxLength(500);
                    entity.HasIndex(job => job.AvailableAt);
                });
            }
        });

        services.TryAddSingleton<PostgreSqlJobQueue<TContext>>();
        services.Replace(ServiceDescriptor.Singleton<IHaldusJobDispatcher>(
            provider => provider.GetRequiredService<PostgreSqlJobQueue<TContext>>()));
        services.AddHostedService<PostgreSqlJobWorker<TContext>>();

        return services;
    }
}

internal sealed class PostgreSqlJobQueue<TContext>(TimeProvider timeProvider) : IHaldusJobDispatcher, IDisposable
    where TContext : DbContext
{
    private readonly SemaphoreSlim _signal = new(0);

    public void Prepare(DbContext context, HaldusJobTicket ticket)
    {
        if (context is not TContext)
        {
            throw new InvalidOperationException(
                $"The job '{ticket.JobType}' is stored in '{context.GetType().Name}', but the job queue lives in '{typeof(TContext).Name}'. " +
                "Jobs must be stored in the DbContext passed to AddHaldusPostgreSqlJobs so that the job and its queue entry are saved together.");
        }

        context.Add(new HaldusQueuedJob
        {
            Id = Guid.CreateVersion7(),
            JobType = ticket.JobType,
            JobId = ticket.JobId,
            AvailableAt = HaldusTime.UtcNow(timeProvider),
        });
    }

    public ValueTask DispatchAsync(HaldusJobTicket ticket, CancellationToken cancellationToken)
    {
        // Aynı sunucudaki yürütücü bir sonraki yoklamayı beklemeden uyanır; diğerleri yoklamayla görür.
        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }

        return ValueTask.CompletedTask;
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _signal.Dispose();
}

internal sealed class PostgreSqlJobWorker<TContext>(
    PostgreSqlJobQueue<TContext> queue,
    HaldusJobRunner runner,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    IOptions<PostgreSqlJobOptions> options,
    ILogger<PostgreSqlJobWorker<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HaldusJobs.IsGeneratingOpenApiDocument)
        {
            // Build sırasındaki şema üretimi veritabanına bağlanmamalıdır.
            return;
        }

        var settings = options.Value;
        var running = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                running.RemoveAll(task => task.IsCompleted);

                var free = Math.Max(1, settings.MaxConcurrency) - running.Count;
                var claimed = free > 0 ? await ClaimAsync(free, stoppingToken) : [];

                foreach (var entry in claimed)
                {
                    running.Add(ProcessAsync(entry, stoppingToken));
                }

                if (claimed.Count == 0)
                {
                    await queue.WaitAsync(settings.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Geçici bir veritabanı hatası yürütücüyü durdurmamalıdır.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogWarning(exception, "The Haldus job queue could not be read; retrying.");

                try
                {
                    await Task.Delay(settings.PollInterval, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        await Task.WhenAll(running);
    }

    /// <summary>
    /// Alınabilir kayıtları tek ifadeyle sahiplenir. <c>SKIP LOCKED</c> sayesinde aynı anda yoklayan sunucular
    /// birbirini beklemez ve aynı kaydı almaz.
    /// </summary>
    private async Task<List<HaldusQueuedJob>> ClaimAsync(int limit, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = HaldusTime.UtcNow(timeProvider);
        var claimed = new List<HaldusQueuedJob>();

        await context.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = ClaimSql(context);
            AddParameter(command, "now", now);
            AddParameter(command, "until", now + options.Value.Lease);
            AddParameter(command, "limit", limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new HaldusQueuedJob
                {
                    Id = reader.GetGuid(0),
                    JobType = reader.GetString(1),
                    JobId = reader.GetGuid(2),
                    Deliveries = reader.GetInt32(3),
                });
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        return claimed;
    }

    private static string ClaimSql(DbContext context)
    {
        var entityType = context.Model.FindEntityType(typeof(HaldusQueuedJob))
            ?? throw new InvalidOperationException(
                $"The Haldus job queue table is not part of the model of '{typeof(TContext).Name}'. Make sure the context is configured with UseHaldus().");

        var helper = context.GetService<ISqlGenerationHelper>();
        var tableName = entityType.GetTableName()!;
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);
        var table = helper.DelimitIdentifier(tableName, schema);

        string Column(string property) =>
            helper.DelimitIdentifier(entityType.FindProperty(property)!.GetColumnName(storeObject)!);

        var id = Column(nameof(HaldusQueuedJob.Id));
        var available = Column(nameof(HaldusQueuedJob.AvailableAt));
        var locked = Column(nameof(HaldusQueuedJob.LockedUntil));
        var deliveries = Column(nameof(HaldusQueuedJob.Deliveries));

        return $"UPDATE {table} SET {locked} = @until, {deliveries} = {deliveries} + 1 "
            + $"WHERE {id} IN (SELECT {id} FROM {table} WHERE {available} <= @now AND ({locked} IS NULL OR {locked} < @now) "
            + $"ORDER BY {available} LIMIT @limit FOR UPDATE SKIP LOCKED) "
            + $"RETURNING {id}, {Column(nameof(HaldusQueuedJob.JobType))}, {Column(nameof(HaldusQueuedJob.JobId))}, {deliveries}";
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task ProcessAsync(HaldusQueuedJob entry, CancellationToken stoppingToken)
    {
        if (entry.Deliveries > options.Value.MaxDeliveries)
        {
            logger.LogError(
                "Job {JobId} of type '{JobType}' was delivered {Deliveries} times without finishing and was dropped from the queue.",
                entry.JobId, entry.JobType, entry.Deliveries);
            await RemoveAsync(entry.Id);
            return;
        }

        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = KeepLeaseAsync(entry.Id, heartbeatStop.Token);
        var finished = false;

        try
        {
            finished = await runner.RunAsync(new HaldusJobTicket(entry.JobType, entry.JobId), stoppingToken);
        }
#pragma warning disable CA1031 // Altyapı hatası: kayıt kuyrukta kalır ve süresi dolunca yeniden teslim edilir.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            logger.LogError(exception, "Job {JobId} of type '{JobType}' could not be run; it stays queued.", entry.JobId, entry.JobType);
        }
        finally
        {
            await heartbeatStop.CancelAsync();

            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                // İş bittiği için durduruldu.
            }
        }

        if (finished)
        {
            await RemoveAsync(entry.Id);
        }
    }

    /// <summary>İş çalıştıkça kaydı elde tutar; böylece uzun süren bir iş başka bir sunucuya yeniden teslim edilmez.</summary>
    private async Task KeepLeaseAsync(Guid entryId, CancellationToken cancellationToken)
    {
        var lease = options.Value.Lease;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromTicks(lease.Ticks / 3), timeProvider, cancellationToken);

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var until = (DateTimeOffset?)(HaldusTime.UtcNow(timeProvider) + lease);

                await scope.ServiceProvider.GetRequiredService<TContext>().Set<HaldusQueuedJob>()
                    .Where(job => job.Id == entryId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LockedUntil, until), cancellationToken);
            }
            catch (DbException exception)
            {
                logger.LogWarning(exception, "The lease of queue entry {EntryId} could not be extended.", entryId);
            }
        }
    }

    private async Task RemoveAsync(Guid entryId)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();

            await scope.ServiceProvider.GetRequiredService<TContext>().Set<HaldusQueuedJob>()
                .Where(job => job.Id == entryId)
                .ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (DbException exception)
        {
            // Kayıt kalırsa süresi dolunca yeniden teslim edilir; iş sonuçlandığı için yürütücü onu yok sayar.
            logger.LogWarning(exception, "Queue entry {EntryId} could not be removed.", entryId);
        }
    }
}
