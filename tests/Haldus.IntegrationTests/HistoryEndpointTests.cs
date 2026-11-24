using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Microsoft.AspNetCore.Mvc;
using Haldus.Sample.Shop.Features.Content;
using Haldus.Sample.Shop.Features.Supply;

namespace Haldus.IntegrationTests;

/// <summary>Saklanan sürümler (<c>Revisioned</c>), değişiklik akışı (<c>ChangeFeed</c>) ve dönemli kayıtlar (<c>EffectiveDated</c>).</summary>
[Collection(ShopCollection.Name)]
public sealed class HistoryEndpointTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _editor = $"editor-{Guid.NewGuid():N}";

    public HistoryEndpointTests(ShopFixture fixture)
    {
        _client = fixture.CreateClient();

        // Akış varsayılan olarak en yeni değişikliklerin birkaç saniye gerisinde kalır; testler beklemez.
        HaldusChanges.SafetyLag = TimeSpan.Zero;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => HaldusChanges.SafetyLag = TimeSpan.FromSeconds(2);

    // ── Sürümler ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Revisions_KeepTheRecordAsItWasAfterEveryWrite()
    {
        var created = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, "/articles", new
        {
            title = "First title",
            slug = Slug(),
            body = "Draft",
            articleSections = new[] { new { heading = "Intro" }, new { heading = "Outro" } },
        }));
        var url = $"/articles/{created.Id}";

        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Patch, url, new { title = "Second title" }));

        // Hiçbir şeyi değiştirmeyen yazma yeni bir sürüm değildir.
        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Patch, url, new { title = "Second title" }));

        var revisions = await ReadAsync<PagedResult<RevisionInfo>>(await SendAsync(HttpMethod.Get, $"{url}/revisions"));
        Assert.Equal(2, revisions.TotalCount);
        Assert.True(revisions.Items[0].Id > revisions.Items[1].Id);
        Assert.All(revisions.Items, revision => Assert.Equal(_editor, revision.CreatedBy));

        // Sürüm, kaydın o andaki halidir: alanları, hesaplanan alanları ve satırlarıyla, kaydın kendi yanıt biçiminde.
        var first = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"{url}/revisions/{revisions.Items[1].Id}"));
        Assert.Equal((created.Id, "First title", "Draft", 2), (first.Id, first.Title, first.Body, first.SectionCount));
        Assert.Equal(["Intro", "Outro"], first.ArticleSections.Select(section => section.Heading));
        Assert.Equal(created.CreatedAt, first.CreatedAt);

        var second = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"{url}/revisions/{revisions.Items[0].Id}"));
        Assert.Equal("Second title", second.Title);
        Assert.NotNull(second.UpdatedAt);

        // Sürüm yalnızca kendi kaydında bulunur.
        var other = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, "/articles", new { title = "Other", slug = Slug() }));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/articles/{other.Id}/revisions/{revisions.Items[0].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{url}/revisions/{long.MaxValue}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/articles/{Guid.NewGuid()}/revisions")).StatusCode);
    }

    [Fact]
    public async Task Revisions_RollbackRestoresFieldsAndRows()
    {
        var created = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, "/articles", new
        {
            title = "Guide",
            slug = Slug(),
            body = "v1",
            articleSections = new[] { new { heading = "One", text = "1" }, new { heading = "Two", text = "2" } },
        }));
        var url = $"/articles/{created.Id}";

        // Satırlardan biri değişir, biri silinir, biri eklenir.
        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Put, url, new
        {
            title = "Guide (edited)",
            slug = created.Slug,
            body = "v2",
            articleSections = new object[]
            {
                new { id = created.ArticleSections[1].Id, heading = "Two!", text = "2" },
                new { heading = "Three", text = "3" },
            },
        }));

        // Satırları bir aksiyon değiştirse de makale değişmiş sayılır ve sürümü saklanır.
        var cleared = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, $"{url}/clear-sections"));
        Assert.Equal((0, 0), (cleared.ArticleSections.Count, cleared.SectionCount));
        Assert.NotNull(cleared.UpdatedAt);

        var revisions = await ReadAsync<PagedResult<RevisionInfo>>(await SendAsync(HttpMethod.Get, $"{url}/revisions"));
        Assert.Equal(3, revisions.TotalCount);

        var afterAction = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"{url}/revisions/{revisions.Items[0].Id}"));
        Assert.Empty(afterAction.ArticleSections);

        // İlk sürüme dönülür: alanlar ve satırlar (kimlikleriyle, sırasıyla) geri gelir, hesaplanan alan yenilenir.
        var restored = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, $"{url}/revisions/{revisions.Items[2].Id}/rollback"));
        Assert.Equal(("Guide", "v1", 2), (restored.Title, restored.Body, restored.SectionCount));
        Assert.Equal(created.ArticleSections.Select(section => section.Id), restored.ArticleSections.Select(section => section.Id));
        Assert.Equal(["One", "Two"], restored.ArticleSections.Select(section => section.Heading));

        var read = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal(["One", "Two"], read.ArticleSections.Select(section => section.Heading));

        // Geri yükleme de bir yazmadır: yeni bir sürüm olur, eskiler durur.
        var after = await ReadAsync<PagedResult<RevisionInfo>>(await SendAsync(HttpMethod.Get, $"{url}/revisions"));
        Assert.Equal(4, after.TotalCount);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"{url}/revisions/{long.MaxValue}/rollback")).StatusCode);
    }

    // ── Değişiklik akışı ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeFeed_ReportsWhatChangedSinceACursorIncludingDeletions()
    {
        var start = await CurrentCursorAsync();

        var kept = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, "/articles", new { title = "Kept", slug = Slug() }));
        var removed = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, "/articles", new { title = "Removed", slug = Slug() }));
        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Patch, $"/articles/{kept.Id}", new { title = "Kept and edited" }));
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/articles/{removed.Id}")).StatusCode);

        var page = await ReadAsync<ChangeFeedPage<ArticleResponse>>(await SendAsync(HttpMethod.Get, $"/articles/changes?since={start}"));

        // Bir kayıt kaç kez değişirse değişsin bir kez yer alır ve şimdiki haliyle döner; silinen kayıt yalnızca kimliğiyle.
        var mine = page.Items.Where(item => item.Id == kept.Id.ToString() || item.Id == removed.Id.ToString()).ToList();
        Assert.Equal(2, mine.Count);

        var upserted = Assert.Single(mine, item => !item.Deleted);
        Assert.Equal((kept.Id.ToString(), "Kept and edited"), (upserted.Id, upserted.Record!.Title));

        var deleted = Assert.Single(mine, item => item.Deleted);
        Assert.Equal((removed.Id.ToString(), null), (deleted.Id, deleted.Record));

        Assert.True(page.Next > start);

        // Akış kaldığı yerden sürer: yeni bir değişiklik yoksa sayfa boştur ve imleç ilerlemez.
        var end = await CurrentCursorAsync(page.Next);
        var idle = await ReadAsync<ChangeFeedPage<ArticleResponse>>(await SendAsync(HttpMethod.Get, $"/articles/changes?since={end}"));
        Assert.Empty(idle.Items);
        Assert.Equal((end, false), (idle.Next, idle.HasMore));
    }

    [Fact]
    public async Task ChangeFeed_PagesAndHoldsBackChangesThatAreTooFresh()
    {
        var start = await CurrentCursorAsync();

        foreach (var title in new[] { "A", "B", "C" })
        {
            await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, "/articles", new { title, slug = Slug() }));
        }

        var first = await ReadAsync<ChangeFeedPage<ArticleResponse>>(await SendAsync(HttpMethod.Get, $"/articles/changes?since={start}&pageSize=2"));
        Assert.Equal((2, true), (first.Items.Count, first.HasMore));

        var second = await ReadAsync<ChangeFeedPage<ArticleResponse>>(await SendAsync(HttpMethod.Get, $"/articles/changes?since={first.Next}&pageSize=2"));
        Assert.NotEmpty(second.Items);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));

        // Henüz yeterince eskimemiş değişiklikler verilmez; imleç de onları atlamaz.
        HaldusChanges.SafetyLag = TimeSpan.FromHours(1);
        var held = await ReadAsync<ChangeFeedPage<ArticleResponse>>(await SendAsync(HttpMethod.Get, $"/articles/changes?since={start}"));
        HaldusChanges.SafetyLag = TimeSpan.Zero;

        Assert.Empty(held.Items);
        Assert.Equal((start, false), (held.Next, held.HasMore));

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/articles/changes?since=abc")).StatusCode);
    }

    // ── Dönemli kayıtlar ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EffectiveDated_KeepsOnePeriodPerDayAndClosesThePreviousOne()
    {
        var supplier = await ReadAsync<SupplierResponse>(await SendAsync(HttpMethod.Post, "/suppliers", new { name = "Dated" }));
        var other = await ReadAsync<SupplierResponse>(await SendAsync(HttpMethod.Post, "/suppliers", new { name = "Other" }));
        var url = $"/suppliers/{supplier.Id}/supplier-rates";

        var first = await ReadAsync<SupplierRateResponse>(await SendAsync(HttpMethod.Post, url, new { currency = "TRY", unitPrice = 10, effectiveFrom = "2026-01-01" }));
        Assert.Equal((new DateOnly(2026, 1, 1), null), (first.EffectiveFrom, first.EffectiveTo));

        // Açık uçlu dönemin içinde başlayan yeni kayıt, öncekini kendi başladığı gün kapatır.
        var second = await ReadAsync<SupplierRateResponse>(await SendAsync(HttpMethod.Post, url, new { currency = "TRY", unitPrice = 12, effectiveFrom = "2026-06-01" }));
        var closed = await ReadAsync<SupplierRateResponse>(await SendAsync(HttpMethod.Get, $"{url}/{first.Id}"));
        Assert.Equal(new DateOnly(2026, 6, 1), closed.EffectiveTo);
        Assert.Null(second.EffectiveTo);

        // Dönemler sayılan alan (para birimi) ve ebeveyn başına ayrı tutulur.
        await ReadAsync<SupplierRateResponse>(await SendAsync(HttpMethod.Post, url, new { currency = "EUR", unitPrice = 1, effectiveFrom = "2026-03-01" }));
        await ReadAsync<SupplierRateResponse>(await SendAsync(
            HttpMethod.Post, $"/suppliers/{other.Id}/supplier-rates", new { currency = "TRY", unitPrice = 99, effectiveFrom = "2026-03-01" }));

        // Bir gün için en çok bir kayıt geçerlidir: dönem yarı açıktır (başlangıç dahil, bitiş hariç).
        Assert.Equal([10m], await PricesAsync(url, "2026-03-01", "TRY"));
        Assert.Equal([10m], await PricesAsync(url, "2026-05-31", "TRY"));
        Assert.Equal([12m], await PricesAsync(url, "2026-06-01", "TRY"));
        Assert.Empty(await PricesAsync(url, "2025-12-31", "TRY"));
        Assert.Equal([1m], await PricesAsync(url, "2026-06-01", "EUR"));

        // Başka bir dönemle çakışan kayıt yazılamaz; ne oluştururken ne güncellerken.
        var overlapping = await SendAsync(HttpMethod.Post, url, new { currency = "TRY", unitPrice = 11, effectiveFrom = "2026-02-01", effectiveTo = "2026-03-01" });
        var stretched = await SendAsync(HttpMethod.Patch, $"{url}/{first.Id}", new { effectiveTo = "2026-07-01" });
        var turkish = await SendAsync(HttpMethod.Patch, $"{url}/{first.Id}", new { effectiveTo = "2026-07-01" }, language: "tr");

        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stretched.StatusCode);
        Assert.Equal("Dönemler çakışıyor", (await turkish.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Title);

        // Bitiş başlangıçtan sonra olmalıdır.
        var inverted = await SendAsync(HttpMethod.Post, url, new { currency = "USD", unitPrice = 1, effectiveFrom = "2026-02-01", effectiveTo = "2026-02-01" });
        Assert.Equal(HttpStatusCode.BadRequest, inverted.StatusCode);
        Assert.Equal(["effectiveTo"], (await inverted.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!.Errors.Keys);

        // Çakışmayan bir değişiklik serbesttir; araya boşluk bırakmak da.
        var shortened = await ReadAsync<SupplierRateResponse>(await SendAsync(HttpMethod.Patch, $"{url}/{first.Id}", new { effectiveTo = "2026-04-01" }));
        Assert.Equal(new DateOnly(2026, 4, 1), shortened.EffectiveTo);
        Assert.Empty(await PricesAsync(url, "2026-05-01", "TRY"));
    }

    [Fact]
    public async Task EffectiveDated_WrittenConcurrently_NeverOverlaps()
    {
        var supplier = await ReadAsync<SupplierResponse>(await SendAsync(HttpMethod.Post, "/suppliers", new { name = "Racing" }));
        var url = $"/suppliers/{supplier.Id}/supplier-rates";

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            SendAsync(HttpMethod.Post, url, new { currency = "TRY", unitPrice = index, effectiveFrom = "2026-01-01" })));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(5, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        Assert.Single(await PricesAsync(url, "2026-02-01", "TRY"));
    }

    private static string Slug() => $"article-{Guid.NewGuid():N}";

    private async Task<decimal[]> PricesAsync(string url, string asOf, string currency)
    {
        var page = await ReadAsync<PagedResult<SupplierRateResponse>>(await SendAsync(HttpMethod.Get, $"{url}?asOf={asOf}&pageSize=100"));
        return [.. page.Items.Where(rate => rate.Currency == currency).Select(rate => rate.UnitPrice)];
    }

    // ── Satırları değişen sahip ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApprovedRecord_LosesItsApprovalWhenAnActionChangesItsRows()
    {
        var first = $"first-{Guid.NewGuid():N}";
        var second = $"second-{Guid.NewGuid():N}";

        var request = await ReadAsync<PurchaseRequestResponse>(await SendAsync(HttpMethod.Post, "/purchase-requests", new
        {
            title = "Desks",
            amount = 900,
            purchaseRequestLines = new[] { new { item = "Desk", quantity = 3 } },
        }));
        var url = $"/purchase-requests/{request.Id}";

        await ReadAsync<PurchaseRequestResponse>(await SendAsync(HttpMethod.Post, $"{url}/approve", user: first));
        var approved = await ReadAsync<PurchaseRequestResponse>(await SendAsync(HttpMethod.Post, $"{url}/approve", user: second));
        Assert.Equal(ApprovalStatus.Approved, approved.ApprovalStatus);

        // Aksiyon yalnızca kalemleri değiştirir; talebin kendi alanlarına dokunmaz. Onay yine de düşer.
        var cleared = await ReadAsync<PurchaseRequestResponse>(await SendAsync(HttpMethod.Post, $"{url}/clear-lines", user: second));
        Assert.Empty(cleared.PurchaseRequestLines);
        Assert.Equal((ApprovalStatus.Pending, 0), (cleared.ApprovalStatus, cleared.ApprovalLevel));
        Assert.Contains(second, cleared.Contributors);

        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/order", new { reference = "PO-9" })).StatusCode);
    }

    /// <summary>Akışın şu anki sonu: bundan sonraki değişiklikler bu imleçten okunur.</summary>
    private async Task<long> CurrentCursorAsync(long since = 0)
    {
        while (true)
        {
            var page = await ReadAsync<ChangeFeedPage<ArticleResponse>>(await SendAsync(HttpMethod.Get, $"/articles/changes?since={since}&pageSize=500"));
            since = page.Next;

            if (!page.HasMore)
            {
                return since;
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? user = null, string? language = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.UserHeader, user ?? _editor);

        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
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