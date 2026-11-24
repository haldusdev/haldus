using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Engagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus.IntegrationTests;

/// <summary>Olay yayını (<c>PublishesEvents</c>): kayıtla birlikte yazılır, kalıcı olduktan sonra sırayla teslim edilir.</summary>
[Collection(ShopCollection.Name)]
public sealed class EventEndpointTests(ShopFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Events_FollowEveryChangeOfTheRecordInOrder()
    {
        var recorder = new Recorder();
        await using var factory = fixture.CreateFactory(services => services.AddSingleton<IHaldusEventPublisher>(recorder));
        var client = factory.CreateClient();

        var ticket = await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Post, "/tickets", new { subject = "Printer" }));
        var url = $"/tickets/{ticket.Id}";

        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Patch, url, new { subject = "Printer on fire" }));
        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Post, $"{url}/bill", new { note = "1 hour" }));
        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Post, $"{url}/close"));

        // Reddedilen istekler olay üretmez: geçersiz gövde, uymayan durum.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Patch, url, new { subject = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, $"{url}/close")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Delete, url)).StatusCode);

        var events = await recorder.WaitForAsync(ticket.Id.ToString(), count: 7);

        // Geçiş, kaydın güncellenmesi olayının yanında kendi adıyla da yayınlanır.
        Assert.Equal(
            ["ticket.created", "ticket.updated", "ticket.updated", "ticket.bill", "ticket.updated", "ticket.close", "ticket.deleted"],
            events.Select(item => item.Type));
        Assert.All(events, item => Assert.Equal(("ticket", "ada"), (item.Resource, item.UserId)));
        Assert.Equal(events.Select(item => item.Id).Order(), events.Select(item => item.Id));

        // Olay, kaydın o andaki halini yanıt biçiminde taşır.
        Assert.Equal("Printer", events[0].Data.GetProperty("subject").GetString());
        Assert.Equal("Printer on fire", events[1].Data.GetProperty("subject").GetString());
        Assert.Equal(("Billed", "1 hour"), (events[3].Data.GetProperty("ticketBilling").GetString(), events[3].Data.GetProperty("billingNote").GetString()));
        Assert.Equal("Closed", events[5].Data.GetProperty("status").GetString());
        Assert.Equal("Closed", events[6].Data.GetProperty("status").GetString());

        // Teslim edilen olay bekleme tablosundan silinir.
        Assert.Equal(0, await PendingAsync(ticket.Id.ToString()));
    }

    [Fact]
    public async Task Events_OfASoftDeletedRecord_SayDeleted()
    {
        var recorder = new Recorder();
        await using var factory = fixture.CreateFactory(services => services.AddSingleton<IHaldusEventPublisher>(recorder));
        var client = factory.CreateClient();

        var discussion = await ReadAsync<DiscussionResponse>(await SendAsync(client, HttpMethod.Post, "/discussions", new { title = "Events" }));
        var reply = await ReadAsync<ReplyResponse>(await SendAsync(client, HttpMethod.Post, $"/discussions/{discussion.Id}/replies", new { text = "Hi", score = 1 }));
        var url = $"/discussions/{discussion.Id}/replies/{reply.Id}";

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Delete, url)).StatusCode);
        await ReadAsync<ReplyResponse>(await SendAsync(client, HttpMethod.Post, $"{url}/restore"));

        // Yumuşak silme veritabanında bir güncellemedir; olay yine de silinmedir. Geri yükleme bir güncellemedir.
        var events = await recorder.WaitForAsync(reply.Id.ToString(), count: 3);
        Assert.Equal(["reply.created", "reply.deleted", "reply.updated"], events.Select(item => item.Type));
    }

    [Fact]
    public async Task Events_AreRetriedInOrderWhenAPublisherFails()
    {
        var recorder = new Recorder();
        await using var factory = fixture.CreateFactory(services => services.AddSingleton<IHaldusEventPublisher>(recorder));
        var client = factory.CreateClient();

        var ticket = await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Post, "/tickets", new { subject = "Flaky" }));
        await recorder.WaitForAsync(ticket.Id.ToString(), count: 1);

        // Yayıncı bir olayı ilk denemede reddeder.
        recorder.FailOnce = item => item.RecordId == ticket.Id.ToString() && item.Type == "ticket.updated";
        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Patch, $"/tickets/{ticket.Id}", new { subject = "Flaky, again" }));
        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Post, $"/tickets/{ticket.Id}/close"));

        // Olay kaybolmaz: sonra yeniden denenir ve arkasındakiler onu bekler; sıra bozulmaz.
        var events = await recorder.WaitForAsync(ticket.Id.ToString(), count: 4);
        Assert.Equal(["ticket.created", "ticket.updated", "ticket.updated", "ticket.close"], events.Select(item => item.Type));
        Assert.Equal("Flaky, again", events[1].Data.GetProperty("subject").GetString());
        Assert.Equal(1, recorder.Failures);
    }

    [Fact]
    public async Task Events_WaitInTheOutboxUntilAPublisherIsRegistered()
    {
        // Yayıncısı olmayan uygulama olayı yazar ama teslim etmez (ve silmez).
        var client = fixture.CreateClient();
        var ticket = await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Post, "/tickets", new { subject = "Waiting" }));

        Assert.Equal(0, await HaldusEventDispatcher.RunAsync(fixture.Services, CancellationToken));
        Assert.Equal(1, await PendingAsync(ticket.Id.ToString()));

        // Yayıncı kaydedilince bekleyen olay teslim edilir.
        var recorder = new Recorder();
        await using var factory = fixture.CreateFactory(services => services.AddSingleton<IHaldusEventPublisher>(recorder));
        using var started = factory.CreateClient();

        var events = await recorder.WaitForAsync(ticket.Id.ToString(), count: 1);
        Assert.Equal("ticket.created", events[0].Type);
    }

    [Fact]
    public async Task Events_ThatKeepFailing_AreGivenUpWithoutBlockingTheRest()
    {
        var recorder = new Recorder();
        var attempts = HaldusEvents.MaxAttempts;
        HaldusEvents.MaxAttempts = 1;

        try
        {
            await using var factory = fixture.CreateFactory(services => services.AddSingleton<IHaldusEventPublisher>(recorder));
            var client = factory.CreateClient();

            // Kural olay yazılmadan önce kurulur: dağıtıcı olayı yazılır yazılmaz teslim etmeye çalışır.
            var subject = $"Poison {Guid.NewGuid():N}";
            recorder.FailAlways = item => item.Data.TryGetProperty("subject", out var value) && value.GetString() == subject;
            var poison = await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Post, "/tickets", new { subject }));

            var healthy = await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Post, "/tickets", new { subject = "Healthy" }));

            // Vazgeçilen olay arkasındakileri bekletmez; kendisi tabloda, nedeniyle birlikte kalır.
            await recorder.WaitForAsync(healthy.Id.ToString(), count: 1);

            HaldusOutboxEvent? dead = null;
            await fixture.WithDbContextAsync(async context => dead = await context.Set<HaldusOutboxEvent>()
                .SingleOrDefaultAsync(item => item.RecordId == poison.Id.ToString(), CancellationToken));

            Assert.NotNull(dead);
            Assert.NotNull(dead.DeadAt);
            Assert.Equal((1, "Simulated outage"), (dead.Attempts, dead.LastError));
            Assert.Empty(recorder.Of(poison.Id.ToString()));
        }
        finally
        {
            HaldusEvents.MaxAttempts = attempts;
        }
    }

    private async Task<int> PendingAsync(string recordId)
    {
        var count = -1;
        await fixture.WithDbContextAsync(async context =>
            count = await context.Set<HaldusOutboxEvent>().CountAsync(item => item.RecordId == recordId, CancellationToken));
        return count;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.UserHeader, "ada");

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await client.SendAsync(request, CancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken)}");

        return (await response.Content.ReadFromJsonAsync<T>(CancellationToken))!;
    }

    /// <summary>Teslim edilen olayları biriktiren, istenirse bazılarını reddeden yayıncı.</summary>
    private sealed class Recorder : IHaldusEventPublisher
    {
        private readonly ConcurrentQueue<HaldusEvent> _events = new();
        private int _failures;

        public Func<HaldusEvent, bool>? FailOnce { get; set; }

        public Func<HaldusEvent, bool>? FailAlways { get; set; }

        public int Failures => _failures;

        public Task PublishAsync(HaldusEvent published, CancellationToken cancellationToken)
        {
            if (FailAlways?.Invoke(published) == true)
            {
                Interlocked.Increment(ref _failures);
                throw new InvalidOperationException("Simulated outage");
            }

            if (FailOnce?.Invoke(published) == true)
            {
                FailOnce = null;
                Interlocked.Increment(ref _failures);
                throw new InvalidOperationException("Simulated outage");
            }

            _events.Enqueue(published);
            return Task.CompletedTask;
        }

        public List<HaldusEvent> Of(string recordId) => [.. _events.Where(item => item.RecordId == recordId)];

        public async Task<List<HaldusEvent>> WaitForAsync(string recordId, int count)
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;

            while (Of(recordId).Count < count)
            {
                Assert.True(
                    DateTimeOffset.UtcNow < deadline,
                    $"Expected {count} events for {recordId}, received: {string.Join(", ", Of(recordId).Select(item => item.Type))}");
                await Task.Delay(50, CancellationToken);
            }

            return Of(recordId);
        }
    }
}
