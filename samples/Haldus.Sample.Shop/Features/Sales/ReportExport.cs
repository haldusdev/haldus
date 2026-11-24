using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Haldus.Sample.Shop.Features.Sales;

/// <summary>
/// Sipariş raporu üreten iş. İstek onu oluşturur ve hemen döner; iş arka planda çalışır ve istemci durumunu
/// <c>GET /report-exports/{id}</c> ile izler. Yazılabilir alanlar işin girdisi, diğerleri çıktısıdır.
/// </summary>
public partial class ReportExport
{
    [Range(2000, 2100)]
    public int Year { get; set; }

    /// <summary>Yalnızca örnek içindir: işin ne kadar süreceğini taklit eder.</summary>
    [Range(0, 60)]
    public int SimulatedSeconds { get; set; }

    public int OrderCount { get; private set; }

    public string? DownloadUrl { get; private set; }

    public void Complete(int orderCount, string downloadUrl)
    {
        OrderCount = orderCount;
        DownloadUrl = downloadUrl;
    }
}

public sealed class ReportExportHandler(AppDbContext db, IStringLocalizer<ShopMessages> messages) : IJobHandler<ReportExport>
{
    public async Task HandleAsync(ReportExport job, JobContext context, CancellationToken cancellationToken)
    {
        await context.ReportProgressAsync(10, cancellationToken);

        // İş, onu başlatan kiracının kimliğiyle çalışır: yalnızca o kiracının siparişleri sayılır.
        var orders = await db.Orders.CountAsync(cancellationToken);

        if (orders == 0)
        {
            // İş başarısız sayılır ve neden, işin Error alanında istemciye gösterilir.
            throw new JobFailedException(messages["NothingToExport"]);
        }

        await context.ReportProgressAsync(50, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(job.SimulatedSeconds), cancellationToken);

        job.Complete(orders, $"/downloads/reports/{job.Id}.csv");
    }
}