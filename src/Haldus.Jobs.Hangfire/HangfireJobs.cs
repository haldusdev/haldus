using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haldus;

public static class HaldusHangfireJobsExtensions
{
    /// <summary>
    /// İşleri Hangfire'a teslim eder. Hangfire'ın kendisini (depolama, sunucu, panel) uygulama kurar:
    /// <c>AddHangfire(...)</c> ve <c>AddHangfireServer()</c>. Haldus yalnızca işi kuyruğa koyar ve Hangfire
    /// onu çalıştırmak istediğinde yürütücüsünü çağırır; işin durumu, ilerlemesi ve iptali yine iş kaydında
    /// tutulur.
    /// </summary>
    /// <remarks>
    /// Kuyruğa koyma, iş kaydının kaydedilmesiyle aynı transaction'da değildir: süreç ikisinin arasında
    /// çökerse iş <c>Queued</c> olarak kalır ve çalıştırılmaz. Bunun kabul edilemediği yerde kuyruğunu iş
    /// kaydıyla birlikte yazan PostgreSQL altyapısı kullanılmalıdır.
    /// </remarks>
    public static IServiceCollection AddHaldusHangfireJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddTransient<HaldusHangfireJob>();
        services.Replace(ServiceDescriptor.Singleton<IHaldusJobDispatcher, HangfireJobDispatcher>());

        return services;
    }
}

internal sealed class HangfireJobDispatcher(IBackgroundJobClient client) : IHaldusJobDispatcher
{
    public void Prepare(DbContext context, HaldusJobTicket ticket)
    {
        // Hangfire kendi deposunu kullanır; iş kaydıyla birlikte yazılacak bir şey yok.
    }

    public ValueTask DispatchAsync(HaldusJobTicket ticket, CancellationToken cancellationToken)
    {
        // Hangfire, CancellationToken parametresini çalıştırma anında kendi belirteciyle değiştirir.
        client.Enqueue<HaldusHangfireJob>(job => job.RunAsync(ticket.JobType, ticket.JobId, CancellationToken.None));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Hangfire'ın çalıştırdığı iş: Haldus'un yürütücüsünü çağırır.</summary>
public sealed class HaldusHangfireJob(HaldusJobRunner runner)
{
    public async Task RunAsync(string jobType, Guid jobId, CancellationToken cancellationToken)
    {
        if (!await runner.RunAsync(new HaldusJobTicket(jobType, jobId), cancellationToken))
        {
            // Sunucu kapanırken iş yarıda kaldı; Hangfire'ın onu yeniden kuyruğa alması için bunu bildiririz.
            throw new OperationCanceledException(cancellationToken);
        }
    }
}