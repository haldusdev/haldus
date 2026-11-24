using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haldus;

/// <summary>Çalıştırılacak bir işin adresi: tipinin adı ve kimliği. Kuyruklarda taşınan tek şey budur.</summary>
public readonly record struct HaldusJobTicket(string JobType, Guid JobId);

/// <summary>
/// İşleri yürütücüye ulaştıran altyapı (bellek içi kuyruk, PostgreSQL kuyruğu, Hangfire...). Haldus'un
/// çekirdeği yalnızca bu arayüzü bilir; altyapılar ayrı paketlerdir ve <c>AddHaldus...Jobs()</c> ile seçilir.
/// </summary>
public interface IHaldusJobDispatcher
{
    /// <summary>
    /// İş kaydı kaydedilmeden hemen önce çağrılır. Kuyruğunu aynı veritabanında tutan altyapı, satırını
    /// burada aynı bağlama ekler; böylece iş ve kuyruk kaydı birlikte kalıcı olur.
    /// </summary>
    void Prepare(DbContext context, HaldusJobTicket ticket);

    /// <summary>İş kaydı kaydedildikten sonra çağrılır.</summary>
    ValueTask DispatchAsync(HaldusJobTicket ticket, CancellationToken cancellationToken);
}

public sealed class HaldusJobOptions
{
    /// <summary>
    /// Yarıda kalan (süreci çöken veya kapanan) bir işin en fazla kaç kez çalıştırılmaya başlanacağı.
    /// Mantığı hata fırlatan iş yeniden denenmez, başarısız sayılır.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Çalışan bir iş için, başka bir sunucudan gelen iptal isteğinin ne sıklıkla kontrol edileceği.</summary>
    public TimeSpan CancellationPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// İş kaydı henüz görünmüyorsa (onu oluşturan transaction bitmemiş olabilir) kaç kez ve hangi aralıkla
    /// yeniden bakılacağı.
    /// </summary>
    public int LookupAttempts { get; set; } = 20;

    public TimeSpan LookupDelay { get; set; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>Üretilen kodun iş tiplerini, <c>MapJob</c> çağrısının da varsa lambda'yı kaydettiği yer.</summary>
public static class HaldusJobRegistry
{
    private static readonly ConcurrentDictionary<string, Func<HaldusJobRunner, Guid, CancellationToken, Task<bool>>> Runners = new();
    private static readonly ConcurrentDictionary<Type, Delegate> Handlers = new();

    public static void Register<TJob>()
        where TJob : class, IHaldusJob =>
        Runners[Name<TJob>()] = static (runner, jobId, cancellationToken) => runner.RunAsync<TJob>(jobId, cancellationToken);

    public static string Name<TJob>() => typeof(TJob).FullName!;

    internal static bool IsRegistered<TJob>() => Runners.ContainsKey(Name<TJob>());

    internal static void SetHandler<TJob>(Func<TJob, JobContext, CancellationToken, Task> handler) => Handlers[typeof(TJob)] = handler;

    internal static Func<TJob, JobContext, CancellationToken, Task>? Handler<TJob>() =>
        Handlers.TryGetValue(typeof(TJob), out var handler) ? (Func<TJob, JobContext, CancellationToken, Task>)handler : null;

    internal static Func<HaldusJobRunner, Guid, CancellationToken, Task<bool>>? Runner(string jobType) =>
        Runners.TryGetValue(jobType, out var runner) ? runner : null;
}

/// <summary>
/// Bir işi çalıştırır: kaydı yükler, sahiplenir, mantığını onu başlatan kiracı ve kullanıcının kimliğiyle
/// çağırır ve sonucunu yazar. Hangi altyapı teslim ederse etsin işin yaşam döngüsü burada yönetilir.
/// </summary>
public sealed class HaldusJobRunner(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    IOptions<HaldusJobOptions> options,
    ILogger<HaldusJobRunner> logger)
{
    private const int MaxErrorLength = 2000;

    /// <summary>Bu süreçte çalışan işlerin iptal kaynakları; aynı süreçten gelen iptal beklemeden ulaşır.</summary>
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> Running = new();

    /// <returns>
    /// İş sonuçlandıysa (veya çalıştırılacak bir şey yoksa) <c>true</c>; süreç kapanırken yarıda kaldıysa
    /// <c>false</c>. Altyapı ikinci durumda işi yeniden teslim etmelidir.
    /// </returns>
    public Task<bool> RunAsync(HaldusJobTicket ticket, CancellationToken cancellationToken)
    {
        if (HaldusJobRegistry.Runner(ticket.JobType) is { } runner)
        {
            return runner(this, ticket.JobId, cancellationToken);
        }

        logger.LogError("No Haldus job type named '{JobType}' is registered; job {JobId} was dropped.", ticket.JobType, ticket.JobId);
        return Task.FromResult(true);
    }

    /// <summary>Aynı süreçte çalışan bir işin iptal belirtecini hemen tetikler.</summary>
    internal static void Signal(Guid jobId)
    {
        if (Running.TryGetValue(jobId, out var cancellation))
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // İş tam o anda bitti.
            }
        }
    }

    internal async Task<bool> RunAsync<TJob>(Guid jobId, CancellationToken cancellationToken)
        where TJob : class, IHaldusJob
    {
        var settings = options.Value;

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<IHaldusDbContextProvider>().For<TJob>();

        TJob? job = null;

        for (var attempt = 0; attempt < settings.LookupAttempts; attempt++)
        {
            job = await Jobs<TJob>(context).FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);

            if (job is not null)
            {
                break;
            }

            await Task.Delay(settings.LookupDelay, timeProvider, cancellationToken);
        }

        if (job is null)
        {
            logger.LogWarning("Job {JobId} of type '{JobType}' was not found; nothing to run.", jobId, typeof(TJob).Name);
            return true;
        }

        if (job.Status is not (JobStatus.Queued or JobStatus.Running))
        {
            // Başlamadan iptal edilmiş veya zaten sonuçlanmış.
            return true;
        }

        if (job.CancelRequested)
        {
            await FinishAsync<TJob>(jobId, JobStatus.Cancelled, error: null);
            return true;
        }

        if (job.Attempts >= settings.MaxAttempts)
        {
            logger.LogError("Job {JobId} of type '{JobType}' was abandoned after {Attempts} attempts.", jobId, typeof(TJob).Name, job.Attempts);
            await FinishAsync<TJob>(jobId, JobStatus.Failed, error: null);
            return true;
        }

        // İş, onu oluşturan kiracı ve kullanıcının kimliğiyle çalışır.
        using var identity = HaldusAmbient.Use(
            (job as IMultiTenant)?.TenantId,
            (job as IOwned)?.OwnerId ?? (job as IUserAuditable)?.CreatedBy);

        job.Status = JobStatus.Running;
        job.StartedAt ??= HaldusTime.UtcNow(timeProvider);
        job.Attempts++;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Durum bu arada değişti: iş iptal edildi veya başka bir yürütücü sahiplendi.
            return true;
        }

        // İş, onu oluşturan isteğin diliyle çalışır. Kültür bu asenkron akışla birlikte taşınır ve metot
        // bitince çağıranın kültürü geri gelir.
        if (TryGetCulture(job.Culture) is { } culture)
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
            System.Globalization.CultureInfo.CurrentUICulture = culture;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Running[jobId] = cancellation;
        var watcher = WatchForCancellationAsync<TJob>(jobId, cancellation);

        try
        {
            var jobContext = new RunningJobContext<TJob>(this, services, job);

            if (HaldusJobRegistry.Handler<TJob>() is { } lambda)
            {
                await lambda(job, jobContext, cancellation.Token);
            }
            else
            {
                await services.GetRequiredService<IJobHandler<TJob>>().HandleAsync(job, jobContext, cancellation.Token);
            }

            job.Status = JobStatus.Succeeded;
            job.Progress = 100;
            job.CompletedAt = HaldusTime.UtcNow(timeProvider);

            // Biten işin sonucu, süreç tam o anda kapanıyor olsa da kaydedilir.
            await context.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Süreç kapanıyor. İş "çalışıyor" olarak kalır; altyapı onu yeniden teslim eder.
            return false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await FinishAsync<TJob>(jobId, JobStatus.Cancelled, error: null);
            return true;
        }
        catch (JobFailedException failure)
        {
            await FinishAsync<TJob>(jobId, JobStatus.Failed, failure.Message);
            return true;
        }
#pragma warning disable CA1031 // İşin mantığından gelen her hata işi başarısız yapar; yürütücüyü düşürmemelidir.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // Ayrıntı istemciye gösterilmez; iç bilgileri sızdırabilir.
            logger.LogError(exception, "Job {JobId} of type '{JobType}' failed.", jobId, typeof(TJob).Name);
            await FinishAsync<TJob>(jobId, JobStatus.Failed, error: null);
            return true;
        }
        finally
        {
            Running.TryRemove(jobId, out _);
            await cancellation.CancelAsync();

            try
            {
                await watcher;
            }
            catch (OperationCanceledException)
            {
                // İzleyici, iş bittiği için durduruldu.
            }
        }
    }

    private static System.Globalization.CultureInfo? TryGetCulture(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        try
        {
            return System.Globalization.CultureInfo.GetCultureInfo(name);
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// İşin sonucunu ayrı bir kapsamda yazar. Mantığın kullandığı bağlam atılır; böylece başarısız veya
    /// iptal edilen bir işin yarım kalan değişiklikleri kaydedilmez.
    /// </summary>
    private async Task FinishAsync<TJob>(Guid jobId, JobStatus status, string? error)
        where TJob : class, IHaldusJob
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IHaldusDbContextProvider>().For<TJob>();
        var completedAt = (DateTimeOffset?)HaldusTime.UtcNow(timeProvider);
        var message = error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error;

        await Jobs<TJob>(context)
            .Where(candidate => candidate.Id == jobId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => EF.Property<JobStatus>(candidate, nameof(IHaldusJob.Status)), status)
                    .SetProperty(candidate => EF.Property<string?>(candidate, nameof(IHaldusJob.Error)), message)
                    .SetProperty(candidate => EF.Property<DateTimeOffset?>(candidate, nameof(IHaldusJob.CompletedAt)), completedAt),
                CancellationToken.None);
    }

    internal async Task ReportProgressAsync<TJob>(Guid jobId, int percent, CancellationToken cancellationToken)
        where TJob : class, IHaldusJob
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IHaldusDbContextProvider>().For<TJob>();

        await Jobs<TJob>(context)
            .Where(candidate => candidate.Id == jobId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => EF.Property<int>(candidate, nameof(IHaldusJob.Progress)), percent),
                cancellationToken);
    }

    /// <summary>Başka bir sunucudan gelen iptal isteğini görmek için iş kaydına aralıklarla bakar.</summary>
    private async Task WatchForCancellationAsync<TJob>(Guid jobId, CancellationTokenSource cancellation)
        where TJob : class, IHaldusJob
    {
        var token = cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            await Task.Delay(options.Value.CancellationPollInterval, timeProvider, token);

            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<IHaldusDbContextProvider>().For<TJob>();

            var requested = await Jobs<TJob>(context)
                .Where(candidate => candidate.Id == jobId)
                .Select(candidate => EF.Property<bool>(candidate, nameof(IHaldusJob.CancelRequested)))
                .FirstOrDefaultAsync(token);

            if (requested)
            {
                await cancellation.CancelAsync();
            }
        }
    }

    /// <summary>
    /// İş kimliğiyle, kuyruktan gelen bir adresle aranır; kiracı henüz bilinmediği için kiracı filtresi
    /// uygulanmaz. Diğer filtreler geçerli kalır.
    /// </summary>
    private static IQueryable<TJob> Jobs<TJob>(DbContext context)
        where TJob : class, IHaldusJob
    {
        // Kiracı ve sahip, işin kendisinden okunur; o ana kadar bilinmedikleri için bu iki filtre uygulanmaz.
        string[] unknown =
        [
            .. typeof(IMultiTenant).IsAssignableFrom(typeof(TJob)) ? new[] { HaldusFilters.Tenant } : [],
            .. typeof(IOwned).IsAssignableFrom(typeof(TJob)) ? new[] { HaldusFilters.Owner } : [],
        ];

        var jobs = context.Set<TJob>().AsQueryable();
        return unknown.Length == 0 ? jobs : jobs.IgnoreQueryFilters(unknown);
    }

    private sealed class RunningJobContext<TJob>(HaldusJobRunner runner, IServiceProvider services, TJob job) : JobContext
        where TJob : class, IHaldusJob
    {
        public override IServiceProvider Services => services;

        public override async Task ReportProgressAsync(int percent, CancellationToken cancellationToken = default)
        {
            var clamped = Math.Clamp(percent, 0, 100);
            await runner.ReportProgressAsync<TJob>(job.Id, clamped, cancellationToken);
            job.Progress = clamped;
        }
    }
}

/// <summary>Üretilen iş uçlarının kullandığı yardımcılar.</summary>
public static class HaldusJobs
{
    /// <summary>
    /// Uygulama, build sırasında OpenAPI belgesi üretmek için mi başlatıldı. O sırada başlangıç kodu ve arka
    /// plan servisleri çalışır; iş altyapıları veritabanına bağlanmamak için bunu kontrol eder.
    /// </summary>
    public static bool IsGeneratingOpenApiDocument { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

    /// <summary>
    /// İşi iptal eder: sırada bekliyorsa hemen iptal olur, çalışıyorsa iptali istenir ve mantığının iptal
    /// belirteci tetiklenir.
    /// </summary>
    /// <returns>İş zaten sonuçlandıysa <c>false</c>.</returns>
    public static async Task<bool> CancelAsync<TJob>(DbContext context, TJob job, TimeProvider timeProvider, CancellationToken cancellationToken)
        where TJob : class, IHaldusJob
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(timeProvider);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            switch (job.Status)
            {
                case JobStatus.Queued:
                    job.Status = JobStatus.Cancelled;
                    job.CompletedAt = HaldusTime.UtcNow(timeProvider);
                    break;
                case JobStatus.Running:
                    job.CancelRequested = true;
                    break;
                default:
                    return false;
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                HaldusJobRunner.Signal(job.Id);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Yürütücü işi tam o anda sahiplendi veya bitirdi; güncel durumla yeniden karar verilir.
                await context.Entry(job).ReloadAsync(cancellationToken);
            }
        }

        return false;
    }

    /// <summary>İş altyapısı seçilmediyse veya işin mantığı yoksa hata ilk istekte değil, uygulama açılırken verilir.</summary>
    internal static void Require<TJob>(IServiceProvider services, bool hasLambda)
        where TJob : class
    {
        if (services.GetService<IServiceProviderIsService>() is not { } registered)
        {
            return;
        }

        if (!registered.IsService(typeof(IHaldusJobDispatcher)))
        {
            throw new InvalidOperationException(
                $"MapJob<{typeof(TJob).Name}> needs a job infrastructure, but none is registered. " +
                "Add one of the Haldus.Jobs packages and call its AddHaldus...Jobs() method.");
        }

        if (!hasLambda && !registered.IsService(typeof(IJobHandler<TJob>)))
        {
            throw new InvalidOperationException(
                $"MapJob<{typeof(TJob).Name}> needs its logic, but no '{typeof(IJobHandler<TJob>)}' is registered. " +
                "Add a class that implements it (Haldus registers it automatically) or pass the logic as a lambda.");
        }
    }
}
