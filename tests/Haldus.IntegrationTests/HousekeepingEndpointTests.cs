using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Engagement;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

/// <summary>Hassas alanlar (<c>Sensitive</c>), ölçüte göre silme (<c>Purgeable</c>) ve işin yeniden çalıştırılması (<c>Rerunnable</c>).</summary>
[Collection(ShopCollection.Name)]
public sealed class HousekeepingEndpointTests(ShopFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly string _source = "import-" + Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ── Hassas alan ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sensitive_FieldsAreWrittenButNeverReturned()
    {
        var created = await SendAsync(HttpMethod.Post, "/contacts", new { name = "Ada", nationalId = "12345678901", source = _source });
        var contact = await ReadAsync<ContactResponse>(created);
        var url = $"/contacts/{contact.Id}";

        // Değer saklanır ama hiçbir yanıtta görünmez: oluşturma, tekil okuma, liste, güncelleme.
        Assert.Equal("12345678901", await StoredNationalIdAsync(contact.Id));

        var renamed = await SendAsync(HttpMethod.Patch, url, new { name = "Ada L." });
        var responses = new[]
        {
            created,
            await SendAsync(HttpMethod.Get, url),
            await SendAsync(HttpMethod.Get, $"/contacts?source={_source}"),
            renamed,
        };

        foreach (var response in responses)
        {
            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            Assert.True(response.IsSuccessStatusCode, body);
            Assert.DoesNotContain("nationalId", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("12345678901", body);
        }

        // Kısmi güncelleme göndermediği alana dokunmaz; gönderirse yazar.
        Assert.Equal("12345678901", await StoredNationalIdAsync(contact.Id));
        await ReadAsync<ContactResponse>(await SendAsync(HttpMethod.Patch, url, new { nationalId = "99999999999" }));
        Assert.Equal("99999999999", await StoredNationalIdAsync(contact.Id));

        // Kuralları her alan gibi geçerlidir.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Patch, url, new { nationalId = new string('9', 21) })).StatusCode);

        // Saklanan sürümlerde de yer almaz; geri yükleme şimdiki değere dokunmaz.
        var snapshots = new List<string>();
        await fixture.WithDbContextAsync(async context => snapshots = await context.Set<HaldusRevision>()
            .Where(revision => revision.RecordId == contact.Id.ToString())
            .Select(revision => revision.Snapshot)
            .ToListAsync(CancellationToken));

        Assert.Equal(3, snapshots.Count);
        Assert.All(snapshots, snapshot => Assert.DoesNotContain("9999", snapshot));
        Assert.All(snapshots, snapshot => Assert.DoesNotContain("12345678901", snapshot));
        Assert.All(snapshots, snapshot => Assert.DoesNotContain("NationalId", snapshot));

        var revisions = await ReadAsync<PagedResult<RevisionInfo>>(await SendAsync(HttpMethod.Get, $"{url}/revisions"));
        var restored = await ReadAsync<ContactResponse>(await SendAsync(HttpMethod.Post, $"{url}/revisions/{revisions.Items[^1].Id}/rollback"));
        Assert.Equal("Ada", restored.Name);
        Assert.Equal("99999999999", await StoredNationalIdAsync(contact.Id));
    }

    // ── Ölçüte göre silme ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Purge_CountsFirstAndDeletesOnlyWhenForced()
    {
        var keep = "keep-" + Guid.NewGuid().ToString("N")[..8];
        var doomed = new List<Guid>();

        foreach (var name in new[] { "Alan", "Alice", "Bob" })
        {
            doomed.Add((await ReadAsync<ContactResponse>(await SendAsync(HttpMethod.Post, "/contacts", new { name, source = _source }))).Id);
        }

        var kept = await ReadAsync<ContactResponse>(await SendAsync(HttpMethod.Post, "/contacts", new { name = "Carol", source = keep }));

        // Ölçütsüz bir istek koleksiyonun tamamını silerdi; reddedilir.
        var unbounded = await SendAsync(HttpMethod.Post, "/contacts/purge?force=true");
        Assert.Equal(HttpStatusCode.BadRequest, unbounded.StatusCode);
        Assert.Equal(["filter"], (await unbounded.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!.Errors.Keys);

        // force verilmedikçe yalnızca sayar ve bir örnek gösterir.
        var dryRun = await ReadAsync<PurgeResult<Guid>>(await SendAsync(HttpMethod.Post, $"/contacts/purge?source={_source}"));
        Assert.Equal((3L, 0), (dryRun.Matched, dryRun.Purged));
        Assert.Equal(doomed.Order(), dryRun.Sample.Order());
        Assert.Equal(3, (await ReadAsync<PagedResult<ContactResponse>>(await SendAsync(HttpMethod.Get, $"/contacts?source={_source}"))).TotalCount);

        // Ölçütler birlikte daraltır.
        var narrowed = await ReadAsync<PurgeResult<Guid>>(await SendAsync(HttpMethod.Post, $"/contacts/purge?source={_source}&nameStartsWith=Al&force=true"));
        Assert.Equal((2L, 2), (narrowed.Matched, narrowed.Purged));

        var rest = await ReadAsync<PurgeResult<Guid>>(await SendAsync(HttpMethod.Post, $"/contacts/purge?source={_source}&force=true"));
        Assert.Equal((1L, 1), (rest.Matched, rest.Purged));
        Assert.Equal(doomed[2], Assert.Single(rest.Sample));

        var again = await ReadAsync<PurgeResult<Guid>>(await SendAsync(HttpMethod.Post, $"/contacts/purge?source={_source}&force=true"));
        Assert.Equal((0L, 0), (again.Matched, again.Purged));

        // Ölçüte uymayan kayıt yerinde durur.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/contacts/{kept.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/contacts/{doomed[0]}")).StatusCode);
    }

    // ── İşin yeniden çalıştırılması ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_StartsANewJobWithTheSameInputs()
    {
        // Kiracının henüz siparişi yok; iş başarısız olur.
        var first = await ReadAsync<ReportExportResponse>(await SendAsync(HttpMethod.Post, "/report-exports", new { year = 2031 }));
        Assert.Null(first.RerunOfId);
        await WaitForAsync($"/report-exports/{first.Id}", job => job.Status == JobStatus.Failed);

        // Neden giderilir ve iş aynı girdilerle yeniden çalıştırılır.
        await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada" }));
        var accepted = await SendAsync(HttpMethod.Post, $"/report-exports/{first.Id}/rerun");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var second = await ReadAsync<ReportExportResponse>(accepted);

        // Yeni bir iş kaydıdır: girdileri aynıdır, asıl işe işaret eder ve kendi yaşam döngüsünden geçer.
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal((2031, first.Id), (second.Year, second.RerunOfId));
        Assert.EndsWith($"/report-exports/{second.Id}", accepted.Headers.Location!.ToString());

        var finished = await WaitForAsync($"/report-exports/{second.Id}", job => job.Status == JobStatus.Succeeded);
        Assert.Equal((first.Id, 1), (finished.RerunOfId, finished.OrderCount));

        // Asıl iş olduğu gibi kalır.
        var original = await ReadAsync<ReportExportResponse>(await SendAsync(HttpMethod.Get, $"/report-exports/{first.Id}"));
        Assert.Equal((JobStatus.Failed, null), (original.Status, original.RerunOfId));

        // Bitmemiş iş yeniden çalıştırılamaz.
        var slow = await ReadAsync<ReportExportResponse>(await SendAsync(HttpMethod.Post, "/report-exports", new { year = 2031, simulatedSeconds = 20 }));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"/report-exports/{slow.Id}/rerun")).StatusCode);
        await SendAsync(HttpMethod.Post, $"/report-exports/{slow.Id}/cancel");

        // Başka bir kiracının işi görünmez.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/report-exports/{first.Id}/rerun", tenant: Guid.NewGuid())).StatusCode);
    }

    private async Task<string?> StoredNationalIdAsync(Guid id)
    {
        string? value = null;
        await fixture.WithDbContextAsync(async context =>
            value = (await context.Set<Contact>().AsNoTracking().SingleAsync(contact => contact.Id == id, CancellationToken)).NationalId);
        return value;
    }

    private async Task<ReportExportResponse> WaitForAsync(string url, Func<ReportExportResponse, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        ReportExportResponse last;

        do
        {
            last = await ReadAsync<ReportExportResponse>(await SendAsync(HttpMethod.Get, url));

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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, Guid? tenant = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, (tenant ?? _tenant).ToString());
        request.Headers.Add(DemoIdentity.UserHeader, "ada");

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
