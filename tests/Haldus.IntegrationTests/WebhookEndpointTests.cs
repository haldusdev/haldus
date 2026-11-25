using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Engagement;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haldus.IntegrationTests;

/// <summary>Olayların imzalı HTTP isteğiyle teslim edilmesi (<c>AddHaldusWebhookPublisher</c>).</summary>
[Collection(ShopCollection.Name)]
public sealed class WebhookEndpointTests(ShopFixture fixture)
{
    private const string Secret = "integration-test-webhook-secret";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Events_AreDeliveredAsSignedRequests()
    {
        await using var receiver = await Receiver.StartAsync();
        await using var factory = fixture.CreateFactory(services => services.AddHaldusWebhookPublisher(options =>
        {
            options.AllowInsecureHttp = true;
            options.Endpoints.Add(new HaldusWebhookEndpoint(receiver.Url, Secret, "ticket.*"));
        }));
        var client = factory.CreateClient();

        var ticket = await CreateTicketAsync(client, "Printer");
        var delivery = (await receiver.WaitForAsync(ticket.Id.ToString(), count: 1))[0];

        // Gövde olayın kendisidir; kaydın o andaki halini yanıt biçiminde taşır.
        using var body = JsonDocument.Parse(delivery.Body);
        Assert.Equal("ticket.created", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("ticket", body.RootElement.GetProperty("resource").GetString());
        Assert.Equal("Printer", body.RootElement.GetProperty("data").GetProperty("subject").GetString());
        Assert.Equal(body.RootElement.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), delivery.Id);
        Assert.Equal("application/json", delivery.ContentType);

        // Alıcı imzayı paylaşılan anahtarla doğrular; anahtar, gövde veya zaman uymuyorsa istek reddedilir.
        var now = DateTimeOffset.UtcNow;
        var tolerance = TimeSpan.FromMinutes(5);
        Assert.True(HaldusWebhookSignature.Verify(Secret, delivery.Id, delivery.Timestamp, delivery.Body, delivery.Signature, now, tolerance));
        Assert.False(HaldusWebhookSignature.Verify("another-secret-of-enough-length", delivery.Id, delivery.Timestamp, delivery.Body, delivery.Signature, now, tolerance));
        Assert.False(HaldusWebhookSignature.Verify(Secret, delivery.Id, delivery.Timestamp, [.. delivery.Body, (byte)' '], delivery.Signature, now, tolerance));
        Assert.False(HaldusWebhookSignature.Verify(Secret, delivery.Id + "0", delivery.Timestamp, delivery.Body, delivery.Signature, now, tolerance));
        Assert.False(HaldusWebhookSignature.Verify(Secret, delivery.Id, delivery.Timestamp, delivery.Body, delivery.Signature, now.AddHours(1), tolerance));
        Assert.False(HaldusWebhookSignature.Verify(Secret, delivery.Id, "not-a-number", delivery.Body, delivery.Signature, now, tolerance));

        // Süzülen türlerin dışındaki olaylar gönderilmez.
        var discussion = await ReadAsync<JsonElement>(await SendAsync(client, HttpMethod.Post, "/discussions", new { title = "Quiet" }));
        await SendAsync(client, HttpMethod.Post, $"/discussions/{discussion.GetProperty("id")}/replies", new { text = "Hi", score = 1 });
        var second = await CreateTicketAsync(client, "Scanner");
        await receiver.WaitForAsync(second.Id.ToString(), count: 1);
        Assert.DoesNotContain(receiver.All, item => item.Type.StartsWith("reply.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delivery_IsRetriedUntilTheEndpointAccepts()
    {
        await using var receiver = await Receiver.StartAsync();
        await using var factory = fixture.CreateFactory(services => services.AddHaldusWebhookPublisher(options =>
        {
            options.AllowInsecureHttp = true;
            options.Endpoints.Add(new HaldusWebhookEndpoint(receiver.Url, Secret, "ticket.created"));
        }));
        var client = factory.CreateClient();

        // Dağıtıcı, bekleyen başka olayları da getirebilir; yalnızca bu kaydın ilk teslimatı reddedilir.
        var subject = $"Flaky {Guid.NewGuid():N}";
        receiver.RejectOnce = subject;
        var ticket = await CreateTicketAsync(client, subject);

        var deliveries = await receiver.WaitForAsync(ticket.Id.ToString(), count: 1);
        Assert.Equal(1, receiver.Rejections);

        // Yeniden denenen teslimat aynı olay kimliğini taşır; alıcı yinelenen olayı ondan tanır.
        Assert.Single(deliveries.Select(item => item.Id).Distinct());

        var pending = -1;
        await fixture.WithDbContextAsync(async context =>
            pending = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
                context.Set<HaldusOutboxEvent>(), item => item.RecordId == ticket.Id.ToString(), CancellationToken));
        Assert.Equal(0, pending);
    }

    [Fact]
    public async Task Endpoint_ThatDoesNotAnswer_IsRetriedAndDoesNotStopTheApplication()
    {
        await using var receiver = await Receiver.StartAsync();
        await using var factory = fixture.CreateFactory(services => services.AddHaldusWebhookPublisher(options =>
        {
            options.AllowInsecureHttp = true;
            options.Timeout = TimeSpan.FromSeconds(1);
            options.Endpoints.Add(new HaldusWebhookEndpoint(receiver.Url, Secret, "ticket.created"));
        }));
        var client = factory.CreateClient();

        // İlk teslimat yanıtsız kalır ve zaman aşımına uğrar. Zaman aşımı bir iptal istisnasıdır; dağıtıcı onu sürecin
        // kapanması sanıp dursaydı uygulama da onunla birlikte dururdu.
        var subject = $"Stalled {Guid.NewGuid():N}";
        receiver.StallOnce = subject;
        var ticket = await CreateTicketAsync(client, subject);

        await receiver.WaitForAsync(ticket.Id.ToString(), count: 1);
        Assert.Equal(1, receiver.Stalls);

        // Uygulama ayaktadır ve sonraki olaylar da teslim edilir.
        var next = await CreateTicketAsync(client, "After the stall");
        await receiver.WaitForAsync(next.Id.ToString(), count: 1);
    }

    [Fact]
    public void Signature_OfAStandardWebhooksSecret_MatchesThatSpecification()
    {
        // Standard Webhooks belirtiminin kendi örneği: anahtar base64 çözülerek kullanılır.
        const string StandardSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
        var body = Encoding.UTF8.GetBytes("{\"test\": 2432232314}");

        Assert.Equal(
            "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=",
            HaldusWebhookSignature.Compute(StandardSecret, "msg_p5jXN8AQM9LWM0D4loKWxJek", 1_614_265_330, body));
    }

    [Fact]
    public void Endpoints_MustUseHttpsAndALongSecret()
    {
        Assert.Throws<OptionsValidationException>(() => Options(options =>
            options.Endpoints.Add(new HaldusWebhookEndpoint(new Uri("http://example.com/hook"), Secret))));
        Assert.Throws<OptionsValidationException>(() => Options(options =>
            options.Endpoints.Add(new HaldusWebhookEndpoint(new Uri("https://example.com/hook"), "short"))));
        Assert.Throws<OptionsValidationException>(() => Options(options =>
            options.Endpoints.Add(new HaldusWebhookEndpoint(new Uri("/hook", UriKind.Relative), Secret))));

        Assert.Single(Options(options => options.Endpoints.Add(new HaldusWebhookEndpoint(new Uri("https://example.com/hook"), Secret))).Endpoints);

        static HaldusWebhookOptions Options(Action<HaldusWebhookOptions> configure)
        {
            using var provider = new ServiceCollection()
                .AddSingleton(TimeProvider.System)
                .AddHaldusWebhookPublisher(configure)
                .BuildServiceProvider();

            return provider.GetRequiredService<IOptions<HaldusWebhookOptions>>().Value;
        }
    }

    [Fact]
    public void Signature_AcceptsAnyOfTheListedSignatures()
    {
        var body = Encoding.UTF8.GetBytes("{\"id\":7}");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var current = HaldusWebhookSignature.Compute(Secret, "7", now.ToUnixTimeSeconds(), body);
        var previous = HaldusWebhookSignature.Compute("the-previous-secret-value", "7", now.ToUnixTimeSeconds(), body);

        Assert.StartsWith("v1,", current);
        Assert.NotEqual(current, previous);

        // Anahtar değiştirilirken başlık iki imzayı birlikte taşıyabilir.
        var header = $"{previous} {current}";
        var timestamp = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(HaldusWebhookSignature.Verify(Secret, "7", timestamp, body, header, now, TimeSpan.FromMinutes(5)));
        Assert.True(HaldusWebhookSignature.Verify("the-previous-secret-value", "7", timestamp, body, header, now, TimeSpan.FromMinutes(5)));
        Assert.False(HaldusWebhookSignature.Verify(Secret, "7", timestamp, body, previous, now, TimeSpan.FromMinutes(5)));
        Assert.False(HaldusWebhookSignature.Verify(Secret, "7", timestamp, body, "", now, TimeSpan.FromMinutes(5)));
    }

    private static async Task<TicketResponse> CreateTicketAsync(HttpClient client, string subject) =>
        (await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Post, "/tickets", new { subject })))!;

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

    private sealed record Delivery(string Id, string Timestamp, string Signature, string? ContentType, byte[] Body, string Type, string RecordId);

    /// <summary>Teslimatları biriktiren, gerçek bir soket dinleyen küçük alıcı.</summary>
    private sealed class Receiver : IAsyncDisposable
    {
        private readonly ConcurrentQueue<Delivery> _deliveries = new();
        private readonly WebApplication _app;
        private int _rejections;
        private int _stalls;

        private Receiver(WebApplication app) => _app = app;

        public Uri Url { get; private set; } = null!;

        /// <summary>Konusu bu olan kaydın ilk teslimatı 503 ile reddedilir.</summary>
        public string? RejectOnce { get; set; }

        /// <summary>Konusu bu olan kaydın ilk teslimatı yanıtsız bırakılır.</summary>
        public string? StallOnce { get; set; }

        public int Rejections => _rejections;

        public int Stalls => _stalls;

        public IReadOnlyCollection<Delivery> All => [.. _deliveries];

        public static async Task<Receiver> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var app = builder.Build();
            var receiver = new Receiver(app);
            app.MapPost("/hook", receiver.ReceiveAsync);

            await app.StartAsync(CancellationToken);
            receiver.Url = new Uri(new Uri(app.Urls.Single()), "/hook");
            return receiver;
        }

        public async Task<IReadOnlyList<Delivery>> WaitForAsync(string recordId, int count)
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;

            while (true)
            {
                var matching = _deliveries.Where(item => item.RecordId == recordId).ToList();

                if (matching.Count >= count)
                {
                    return matching;
                }

                Assert.True(DateTimeOffset.UtcNow < deadline, $"Expected {count} deliveries for {recordId}, got {matching.Count}.");
                await Task.Delay(50, CancellationToken);
            }
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();

        private async Task<IResult> ReceiveAsync(HttpRequest request)
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, request.HttpContext.RequestAborted);
            var body = buffer.ToArray();

            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data");

            if (RejectOnce is { } subject
                && data.TryGetProperty("subject", out var value)
                && value.GetString() == subject)
            {
                RejectOnce = null;
                Interlocked.Increment(ref _rejections);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (StallOnce is { } stalled
                && data.TryGetProperty("subject", out var stalledSubject)
                && stalledSubject.GetString() == stalled)
            {
                StallOnce = null;
                Interlocked.Increment(ref _stalls);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), request.HttpContext.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    // Gönderen vazgeçti.
                }

                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            _deliveries.Enqueue(new Delivery(
                request.Headers[HaldusWebhookSignature.IdHeader].ToString(),
                request.Headers[HaldusWebhookSignature.TimestampHeader].ToString(),
                request.Headers[HaldusWebhookSignature.SignatureHeader].ToString(),
                request.ContentType,
                body,
                document.RootElement.GetProperty("type").GetString() ?? "",
                document.RootElement.GetProperty("recordId").GetString() ?? ""));

            return Results.Ok();
        }
    }
}
