using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Engagement;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

/// <summary>Okundu takibi, saklama süresi ve başka kaynaklardan toplanan alanlar.</summary>
[Collection(ShopCollection.Name)]
public sealed class EngagementEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly string _user = "user-" + Guid.NewGuid().ToString("N");
    private readonly string _otherUser = "user-" + Guid.NewGuid().ToString("N");

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ── Okundu takibi ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadTracking_MarksRecordsOfTheCurrentUser()
    {
        var mine = await NotifyAsync(_user, "Order shipped", "Invoice ready", "Welcome");
        var theirs = await NotifyAsync(_otherUser, "Password changed");

        var list = await ReadAsync<PagedResult<NotificationResponse>>(await SendAsync(HttpMethod.Get, "/me/notifications"));
        Assert.Equal(3, list.TotalCount);
        Assert.All(list.Items, notification => Assert.Null(notification.ReadAt));
        Assert.Equal(3, (await ReadAsync<UnreadCount>(await SendAsync(HttpMethod.Get, "/me/notifications/unread-count"))).Count);

        var read = await ReadAsync<NotificationResponse>(await SendAsync(HttpMethod.Post, $"/me/notifications/{mine[0]}/read"));
        Assert.NotNull(read.ReadAt);

        // Yeniden okundu saymak ilk okunma zamanını değiştirmez.
        var again = await ReadAsync<NotificationResponse>(await SendAsync(HttpMethod.Post, $"/me/notifications/{mine[0]}/read"));
        Assert.Equal(read.ReadAt, again.ReadAt);

        var unread = await ReadAsync<PagedResult<NotificationResponse>>(await SendAsync(HttpMethod.Get, "/me/notifications?unread=true"));
        var seen = await ReadAsync<PagedResult<NotificationResponse>>(await SendAsync(HttpMethod.Get, "/me/notifications?unread=false"));
        Assert.Equal(2, unread.TotalCount);
        Assert.Equal(mine[0], Assert.Single(seen.Items).Id);
        Assert.Equal(2, (await ReadAsync<UnreadCount>(await SendAsync(HttpMethod.Get, "/me/notifications/unread-count"))).Count);

        var undone = await ReadAsync<NotificationResponse>(await SendAsync(HttpMethod.Post, $"/me/notifications/{mine[0]}/unread"));
        Assert.Null(undone.ReadAt);

        // Başkasının bildirimi görünmez ve okundu sayılamaz.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/me/notifications/{theirs[0]}/read")).StatusCode);

        // Kullanıcısı bilinmeyen istek reddedilir.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/me/notifications/unread-count", user: "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/me/notifications/read-all", user: "")).StatusCode);

        // Bildirim salt-okunurdur: istemci oluşturamaz, değiştiremez, silemez.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(HttpMethod.Post, "/me/notifications", new { title = "Spam" })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(HttpMethod.Delete, $"/me/notifications/{mine[0]}")).StatusCode);
    }

    [Fact]
    public async Task ReadAll_MarksOnlyTheUnreadRecordsOfTheCurrentUser()
    {
        var mine = await NotifyAsync(_user, "One", "Two", "Three");
        await NotifyAsync(_otherUser, "Theirs");

        await ReadAsync<NotificationResponse>(await SendAsync(HttpMethod.Post, $"/me/notifications/{mine[1]}/read"));

        var result = await ReadAsync<ReadAllResult>(await SendAsync(HttpMethod.Post, "/me/notifications/read-all"));
        Assert.Equal(2, result.Updated);
        Assert.Equal(0, (await ReadAsync<UnreadCount>(await SendAsync(HttpMethod.Get, "/me/notifications/unread-count"))).Count);

        // Okunacak bir şey kalmadıysa hiçbir kayıt değişmez.
        Assert.Equal(0, (await ReadAsync<ReadAllResult>(await SendAsync(HttpMethod.Post, "/me/notifications/read-all"))).Updated);

        // Öteki kullanıcının bildirimi okunmamış kalır.
        Assert.Equal(1, (await ReadAsync<UnreadCount>(await SendAsync(HttpMethod.Get, "/me/notifications/unread-count", user: _otherUser))).Count);
    }

    // ── Saklama süresi ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retention_PurgesRecordsOlderThanThePeriod()
    {
        var ids = await NotifyAsync(_user, "Old", "Borderline", "Fresh");

        await fixture.WithDbContextAsync(async context =>
        {
            var now = DateTimeOffset.UtcNow;

            await Age(ids[0], now.AddDays(-91));
            await Age(ids[1], now.AddDays(-89));

            // Oluşturulma zamanını Haldus yazar; eski bir kayıt ancak doğrudan veritabanında yazılabilir.
            Task Age(Guid id, DateTimeOffset createdAt) => context.Set<Notification>()
                .IgnoreQueryFilters()
                .Where(notification => notification.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.CreatedAt, createdAt), CancellationToken);
        });

        await HaldusMaintenance.RunAsync(fixture.Services, CancellationToken);

        var remaining = await ReadAsync<PagedResult<NotificationResponse>>(await SendAsync(HttpMethod.Get, "/me/notifications"));
        Assert.Equal(["Borderline", "Fresh"], remaining.Items.Select(notification => notification.Title).Order());
    }

    // ── Toplanan alanlar ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rollups_FollowEveryWriteOfTheChildren()
    {
        var discussion = await ReadAsync<DiscussionResponse>(await SendAsync(HttpMethod.Post, "/discussions", new { title = "Shipping times" }));
        Assert.Equal((0, 0), (discussion.ReplyCount, discussion.Score));

        var url = $"/discussions/{discussion.Id}";
        var first = await ReadAsync<ReplyResponse>(await SendAsync(HttpMethod.Post, $"{url}/replies", new { text = "Two days", score = 3 }));
        var second = await ReadAsync<ReplyResponse>(await SendAsync(HttpMethod.Post, $"{url}/replies", new { text = "Never", score = -1 }));
        await AssertRollupAsync(replies: 2, score: 2);

        // Toplanan alan değişince toplam da değişir; toplamı etkilemeyen bir değişiklik hiçbir şeyi değiştirmez.
        await ReadAsync<ReplyResponse>(await SendAsync(HttpMethod.Patch, $"{url}/replies/{second.Id}", new { score = -4 }));
        await AssertRollupAsync(replies: 2, score: -1);
        await ReadAsync<ReplyResponse>(await SendAsync(HttpMethod.Patch, $"{url}/replies/{second.Id}", new { text = "Never arrived" }));
        await AssertRollupAsync(replies: 2, score: -1);

        // Silinen yanıt sayılmaz; geri yüklenince yeniden sayılır.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"{url}/replies/{second.Id}")).StatusCode);
        await AssertRollupAsync(replies: 1, score: 3);
        await ReadAsync<ReplyResponse>(await SendAsync(HttpMethod.Post, $"{url}/replies/{second.Id}/restore"));
        await AssertRollupAsync(replies: 2, score: -1);

        // Kalıcı silme de sayıyı düşürür.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"{url}/replies/{second.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, $"{url}/replies/{second.Id}/purge")).StatusCode);
        await AssertRollupAsync(replies: 1, score: 3);

        // Alanlar istekle yazılamaz.
        var forged = await ReadAsync<DiscussionResponse>(await SendAsync(HttpMethod.Put, url, new { title = "Shipping", replyCount = 99, score = 99 }));
        Assert.Equal((1, 3), (forged.ReplyCount, forged.Score));

        // Uygulamanın kendi kodu da aynı kuraldan geçer: yanıtı uç değil, doğrudan bağlam yazar.
        await fixture.WithDbContextAsync(async context =>
        {
            context.Add(new Reply { DiscussionId = discussion.Id, Text = "From a job", Score = 5 });
            context.Add(new Reply { DiscussionId = discussion.Id, Text = "From a job too", Score = 1 });
            await context.SaveChangesAsync(CancellationToken);
        });
        await AssertRollupAsync(replies: 3, score: 9);

        // Toplanan alana göre sıralanabilir.
        var quiet = await ReadAsync<DiscussionResponse>(await SendAsync(HttpMethod.Post, "/discussions", new { title = "Quiet" }));
        var sorted = await ReadAsync<PagedResult<DiscussionResponse>>(await SendAsync(HttpMethod.Get, "/discussions?sort=-replyCount&pageSize=100"));
        var order = sorted.Items.Select(item => item.Id).ToList();
        Assert.True(order.IndexOf(discussion.Id) < order.IndexOf(quiet.Id));

        Assert.NotEqual(Guid.Empty, first.Id);

        async Task AssertRollupAsync(int replies, int score)
        {
            var current = await ReadAsync<DiscussionResponse>(await SendAsync(HttpMethod.Get, url));
            Assert.Equal((replies, score), (current.ReplyCount, current.Score));
        }
    }

    [Fact]
    public async Task Rollups_CountEveryConcurrentWrite()
    {
        var discussion = await ReadAsync<DiscussionResponse>(await SendAsync(HttpMethod.Post, "/discussions", new { title = "Busy" }));

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            SendAsync(HttpMethod.Post, $"/discussions/{discussion.Id}/replies", new { text = $"Reply {index}", score = 1 })));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));

        // Kilit olmadan birlikte yazan istekler birbirinin kaydını göremez ve sayı eksik kalırdı.
        var current = await ReadAsync<DiscussionResponse>(await SendAsync(HttpMethod.Get, $"/discussions/{discussion.Id}"));
        Assert.Equal((12, 12), (current.ReplyCount, current.Score));
    }

    [Fact]
    public async Task Rollups_FeedTheComputedFieldsOfTheParent()
    {
        var bill = await ReadAsync<BillResponse>(await SendAsync(HttpMethod.Post, "/bills", new { payee = "Acme", amount = 100m }));
        Assert.Equal((0m, BillState.Unpaid), (bill.PaidAmount, bill.State));

        await ReadAsync<BillPaymentResponse>(await SendAsync(HttpMethod.Post, "/bill-payments", new { billId = bill.Id, amount = 40m }));
        var partial = await ReadAsync<BillResponse>(await SendAsync(HttpMethod.Get, $"/bills/{bill.Id}"));
        Assert.Equal((40m, BillState.PartiallyPaid), (partial.PaidAmount, partial.State));

        await ReadAsync<BillPaymentResponse>(await SendAsync(HttpMethod.Post, "/bill-payments", new { billId = bill.Id, amount = 60m }));
        var paid = await ReadAsync<BillResponse>(await SendAsync(HttpMethod.Get, $"/bills/{bill.Id}"));
        Assert.Equal((100m, BillState.Paid), (paid.PaidAmount, paid.State));

        // Tutarı değiştiren güncelleme durumu, toplanmış tutar üzerinden yeniden hesaplar.
        var raised = await ReadAsync<BillResponse>(await SendAsync(HttpMethod.Patch, $"/bills/{bill.Id}", new { amount = 150m }));
        Assert.Equal((100m, BillState.PartiallyPaid), (raised.PaidAmount, raised.State));

        // Başka bir faturanın ödemesi bu faturayı etkilemez.
        var other = await ReadAsync<BillResponse>(await SendAsync(HttpMethod.Post, "/bills", new { payee = "Globex", amount = 10m }));
        await ReadAsync<BillPaymentResponse>(await SendAsync(HttpMethod.Post, "/bill-payments", new { billId = other.Id, amount = 10m }));
        Assert.Equal(100m, (await ReadAsync<BillResponse>(await SendAsync(HttpMethod.Get, $"/bills/{bill.Id}"))).PaidAmount);
        Assert.Equal(BillState.Paid, (await ReadAsync<BillResponse>(await SendAsync(HttpMethod.Get, $"/bills/{other.Id}"))).State);
    }

    /// <summary>Bildirimi sistem yazar: bir isteğin dışında, bildirimin sahibi olarak çalışan kod.</summary>
    private async Task<List<Guid>> NotifyAsync(string user, params string[] titles)
    {
        var ids = new List<Guid>();

        await fixture.WithDbContextAsync(async context =>
        {
            using var identity = HaldusAmbient.Use(tenantId: null, user);

            foreach (var title in titles)
            {
                var notification = new Notification { Title = title };
                context.Add(notification);
                await context.SaveChangesAsync(CancellationToken);
                ids.Add(notification.Id);
            }
        });

        return ids;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? user = null)
    {
        using var request = new HttpRequestMessage(method, url);

        if ((user ?? _user) is { Length: > 0 } identity)
        {
            request.Headers.Add(DemoIdentity.UserHeader, identity);
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
