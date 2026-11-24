using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Platform;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed class JobEndpointTests(ShopFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_AcceptsTheJobAndRunsItInTheBackground()
    {
        await CreateOrdersAsync(2, _tenant);
        await CreateOrdersAsync(1, Guid.NewGuid());

        var response = await SendAsync(HttpMethod.Post, "/report-exports", new { year = 2026 }, user: "ada");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await ReadAsync<ReportExportResponse>(response);
        Assert.Equal($"/report-exports/{accepted.Id}", response.Headers.Location!.OriginalString);
        Assert.Contains(accepted.Status, new[] { JobStatus.Queued, JobStatus.Running, JobStatus.Succeeded });
        Assert.Null(accepted.DownloadUrl);

        var finished = await WaitForAsync<ReportExportResponse>($"/report-exports/{accepted.Id}", job => job.Status == JobStatus.Succeeded);

        // İş, onu başlatan kiracının kimliğiyle çalıştı: yalnızca o kiracının iki siparişini gördü.
        Assert.Equal(2, finished.OrderCount);
        Assert.Equal($"/downloads/reports/{accepted.Id}.csv", finished.DownloadUrl);
        Assert.Equal(100, finished.Progress);
        Assert.Null(finished.Error);
        Assert.NotNull(finished.StartedAt);
        Assert.True(finished.CompletedAt >= finished.StartedAt);
        Assert.Equal("ada", finished.CreatedBy);

        // Kuyruk kaydı iş sonuçlanınca silinir.
        await WaitUntilAsync(async () => await CountQueueEntriesAsync(accepted.Id) == 0);
    }

    [Fact]
    public async Task Status_IsWrittenByName()
    {
        await CreateOrdersAsync(1, _tenant);

        var response = await SendAsync(HttpMethod.Post, "/report-exports", new { year = 2026, status = "Succeeded", progress = 100 });
        var accepted = await ReadAsync<ReportExportResponse>(response);
        await WaitForAsync<ReportExportResponse>($"/report-exports/{accepted.Id}", job => job.Status == JobStatus.Succeeded);

        var raw = await (await SendAsync(HttpMethod.Get, $"/report-exports/{accepted.Id}")).Content.ReadAsStringAsync(CancellationToken);
        var filtered = await ReadAsync<PagedResult<ReportExportResponse>>(await SendAsync(HttpMethod.Get, "/report-exports?status=Succeeded"));
        var none = await ReadAsync<PagedResult<ReportExportResponse>>(await SendAsync(HttpMethod.Get, "/report-exports?status=Failed"));

        Assert.Contains("\"status\":\"Succeeded\"", raw);
        Assert.Equal([accepted.Id], filtered.Items.Select(job => job.Id));
        Assert.Empty(none.Items);
    }

    [Fact]
    public async Task Create_ValidatesTheInput()
    {
        var response = await SendAsync(HttpMethod.Post, "/report-exports", new { year = 1900 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.Equal(["year"], problem!.Errors.Keys);
    }

    [Fact]
    public async Task AJobThatReportsFailure_ShowsTheReasonAndSavesNothingElse()
    {
        // Bu kiracının siparişi yok; işin mantığı bunu bir başarısızlık olarak bildirir.
        var accepted = await ReadAsync<ReportExportResponse>(await SendAsync(HttpMethod.Post, "/report-exports", new { year = 2026 }));

        var failed = await WaitForAsync<ReportExportResponse>($"/report-exports/{accepted.Id}", job => job.Status == JobStatus.Failed);

        Assert.Equal("There are no orders to export.", failed.Error);
        Assert.Null(failed.DownloadUrl);
        Assert.NotNull(failed.CompletedAt);
    }

    [Fact]
    public async Task AJob_RunsInTheLanguageOfTheRequestThatCreatedIt()
    {
        var accepted = await ReadAsync<ReportExportResponse>(
            await SendAsync(HttpMethod.Post, "/report-exports", new { year = 2026 }, language: "tr"));

        var failed = await WaitForAsync<ReportExportResponse>($"/report-exports/{accepted.Id}", job => job.Status == JobStatus.Failed);

        // Arka planda istek yoktur; iş, oluşturulurken kaydedilen dille çalışır.
        Assert.Equal("Dışa aktarılacak sipariş yok.", failed.Error);
    }

    [Fact]
    public async Task Cancel_StopsARunningJob()
    {
        await CreateOrdersAsync(1, _tenant);
        var accepted = await ReadAsync<ReportExportResponse>(
            await SendAsync(HttpMethod.Post, "/report-exports", new { year = 2026, simulatedSeconds = 60 }));
        var url = $"/report-exports/{accepted.Id}";

        // İş bekleme aşamasına geldiğinde ilerlemesi 50 olur.
        await WaitForAsync<ReportExportResponse>(url, job => job.Progress == 50);

        var cancel = await SendAsync(HttpMethod.Post, $"{url}/cancel");
        var cancelled = await WaitForAsync<ReportExportResponse>(url, job => job.Status == JobStatus.Cancelled);
        var again = await SendAsync(HttpMethod.Post, $"{url}/cancel");

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.NotNull(cancelled.CompletedAt);

        // İptal edilen işin yarım kalan çıktısı kaydedilmez.
        Assert.Null(cancelled.DownloadUrl);

        // Sonuçlanmış iş yeniden iptal edilemez.
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(
            "'cancel' is not allowed while the record is in the 'Cancelled' state.",
            (await again.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Detail);
    }

    [Fact]
    public async Task Cancel_OfAJobThatHasNotStarted_TakesEffectImmediately()
    {
        // Kuyruk kaydı olmayan bir iş hiç alınmaz; sırada bekleyen bir işi temsil eder.
        var id = Guid.CreateVersion7();
        await fixture.WithDbContextAsync(async db =>
        {
            db.ReportExports.Add(new ReportExport { Id = id, TenantId = _tenant, Year = 2026, CreatedAt = DateTimeOffset.UtcNow });
            using (HaldusAmbient.Use(_tenant, userId: null))
            {
                await db.SaveChangesAsync(CancellationToken);
            }
        });

        var cancelled = await ReadAsync<ReportExportResponse>(await SendAsync(HttpMethod.Post, $"/report-exports/{id}/cancel"));

        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.CompletedAt);
        Assert.Null(cancelled.StartedAt);
    }

    [Fact]
    public async Task Jobs_AreNotReachableByAnotherTenant()
    {
        await CreateOrdersAsync(1, _tenant);
        var accepted = await ReadAsync<ReportExportResponse>(
            await SendAsync(HttpMethod.Post, "/report-exports", new { year = 2026, simulatedSeconds = 60 }));
        var url = $"/report-exports/{accepted.Id}";
        var intruder = Guid.NewGuid();

        var read = await SendAsync(HttpMethod.Get, url, tenant: intruder);
        var cancel = await SendAsync(HttpMethod.Post, $"{url}/cancel", tenant: intruder);
        var list = await ReadAsync<PagedResult<ReportExportResponse>>(await SendAsync(HttpMethod.Get, "/report-exports", tenant: intruder));

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Empty(list.Items);

        await SendAsync(HttpMethod.Post, $"{url}/cancel");
        await WaitForAsync<ReportExportResponse>(url, job => job.Status == JobStatus.Cancelled);
    }

    [Fact]
    public async Task Idempotent_AcceptsTheSameJobOnlyOnce()
    {
        await CreateOrdersAsync(1, _tenant);
        var key = Guid.NewGuid().ToString();

        var first = await SendAsync(HttpMethod.Post, "/report-exports", new { year = 2026 }, idempotencyKey: key);
        var second = await SendAsync(HttpMethod.Post, "/report-exports", new { year = 2026 }, idempotencyKey: key);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal((await ReadAsync<ReportExportResponse>(first)).Id, (await ReadAsync<ReportExportResponse>(second)).Id);

        var jobs = await ReadAsync<PagedResult<ReportExportResponse>>(await SendAsync(HttpMethod.Get, "/report-exports"));
        var only = Assert.Single(jobs.Items);
        await WaitForAsync<ReportExportResponse>($"/report-exports/{only.Id}", job => job.Status == JobStatus.Succeeded);
    }

    [Fact]
    public async Task AJobWhoseLogicIsALambda_Runs()
    {
        var accepted = await ReadAsync<CatalogRecountResponse>(await SendAsync(HttpMethod.Post, "/catalog-recounts", new { }));

        var finished = await WaitForAsync<CatalogRecountResponse>(
            $"/catalog-recounts/{accepted.Id}", job => job.Status == JobStatus.Succeeded);

        var products = -1;
        await fixture.WithDbContextAsync(async db => products = await db.Products.CountAsync(CancellationToken));
        Assert.InRange(finished.ProductCount, 0, products);
    }

    [Fact]
    public async Task InMemoryInfrastructure_RunsJobsWithoutAQueueTable()
    {
        // Aynı uygulama, bu kez bellek içi kuyrukla: PostgreSQL kuyruğunun yürütücüsü çıkarılır.
        using var client = fixture.CreateClient(services =>
        {
            foreach (var worker in services
                .Where(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType?.Assembly == typeof(PostgreSqlJobOptions).Assembly)
                .ToList())
            {
                services.Remove(worker);
            }

            services.AddHaldusInMemoryJobs();
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/catalog-recounts") { Content = JsonContent.Create(new { }) };
        var accepted = await ReadAsync<CatalogRecountResponse>(await client.SendAsync(request, CancellationToken));

        var finished = await WaitForAsync<CatalogRecountResponse>(
            $"/catalog-recounts/{accepted.Id}", job => job.Status == JobStatus.Succeeded);

        Assert.Equal(100, finished.Progress);
        Assert.Equal(0, await CountQueueEntriesAsync(accepted.Id));
    }

    private async Task CreateOrdersAsync(int count, Guid tenant)
    {
        for (var index = 0; index < count; index++)
        {
            var response = await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada" }, tenant: tenant);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    private async Task<int> CountQueueEntriesAsync(Guid jobId)
    {
        var count = -1;
        await fixture.WithDbContextAsync(async db =>
            count = await db.Set<HaldusQueuedJob>().CountAsync(entry => entry.JobId == jobId, CancellationToken));

        return count;
    }

    /// <summary>İş arka planda ilerler; beklenen duruma gelene kadar yoklanır.</summary>
    private async Task<T> WaitForAsync<T>(string url, Func<T, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        T last;

        do
        {
            last = await ReadAsync<T>(await SendAsync(HttpMethod.Get, url));

            if (condition(last))
            {
                return last;
            }

            await Task.Delay(100, CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail($"The job at {url} did not reach the expected state in time. Last seen: {last}");
        return last;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;

        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The expected condition was not reached in time.");
            await Task.Delay(100, CancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        object? body = null,
        Guid? tenant = null,
        string? user = null,
        string? idempotencyKey = null,
        string? language = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, (tenant ?? _tenant).ToString());

        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
        }

        if (user is not null)
        {
            request.Headers.Add(DemoIdentity.UserHeader, user);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken)}");

        return (await response.Content.ReadFromJsonAsync<T>(CancellationToken))!;
    }
}