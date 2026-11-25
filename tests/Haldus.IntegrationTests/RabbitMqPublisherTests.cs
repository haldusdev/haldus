using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Engagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Haldus.IntegrationTests;

/// <summary>Olayların RabbitMQ'ya gönderilmesi (<c>AddHaldusRabbitMqPublisher</c>), gerçek bir aracıya karşı.</summary>
[Collection(ShopCollection.Name)]
public sealed class RabbitMqPublisherTests(ShopFixture fixture, RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Events_ReachTheQueueThroughTheOutbox()
    {
        var queue = await rabbit.DeclareQueueAsync();

        await using var factory = fixture.CreateFactory(services => services.AddHaldusRabbitMqPublisher(options =>
        {
            rabbit.Configure(options);

            // Varsayılan exchange: yönlendirme anahtarı kuyruğun adıdır.
            options.RoutingKey = queue;
        }));
        var client = factory.CreateClient();

        var ticket = await CreateTicketAsync(client, "Printer");
        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Patch, $"/tickets/{ticket.Id}", new { subject = "Printer on fire" }));

        var messages = await rabbit.WaitForAsync(queue, ticket.Id.ToString(), count: 2);

        // Bir kaydın olayları yazıldıkları sırayla gelir.
        Assert.Equal(["ticket.created", "ticket.updated"], messages.Select(message => message.Properties.Type));

        var created = messages[0];
        using var body = JsonDocument.Parse(created.Body);

        // Gövde olayın kendisidir; kaydın o andaki halini yanıt biçiminde taşır.
        Assert.Equal(
            ["id", "type", "resource", "recordId", "tenantId", "userId", "data", "occurredAt"],
            body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Printer", body.RootElement.GetProperty("data").GetProperty("subject").GetString());
        Assert.Equal("ada", body.RootElement.GetProperty("userId").GetString());

        // Tüketici, gövdeyi çözmeden yönlendirebilsin ve yinelenen olayı tanıyabilsin diye özellikler de taşınır.
        Assert.Equal("application/json", created.Properties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, created.Properties.DeliveryMode);
        Assert.Equal(body.RootElement.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), created.Properties.MessageId);
        Assert.Equal("ticket", Text(created.Properties.Headers!["resource"]));
        Assert.Equal(ticket.Id.ToString(), Text(created.Properties.Headers!["record-id"]));

        // Gönderilen olay bekleme tablosundan silinir.
        await WaitForPendingAsync(ticket.Id.ToString(), expected: 0);
    }

    [Fact]
    public async Task TopicExchange_RoutesByEventTypeAndAnUnroutableEventIsNotDelivered()
    {
        var exchange = $"events-{Guid.NewGuid():N}";
        var queue = await rabbit.DeclareQueueAsync(exchange, binding: "ticket.created");

        await using var publisher = rabbit.CreatePublisher(options =>
        {
            rabbit.Configure(options);
            options.Exchange = exchange;
        });

        // Yönlendirme anahtarı olayın türüdür; kuyruk yalnızca bağlandığı türü alır.
        await publisher.PublishAsync(Event(1, "record-1", "ticket.created"), CancellationToken);
        var delivered = Assert.Single(await rabbit.WaitForAsync(queue, "record-1", count: 1));
        Assert.Equal("ticket.created", delivered.Properties.Type);

        // Hiçbir kuyruğa yönlendirilemeyen olay teslim edilmiş sayılmaz: aracı onu geri çevirir ve yayıncı hata fırlatır
        // (olay kutuda kalır). Sessizce yutulsaydı, bağı unutulmuş bir kuyruk olayları kaybettirirdi.
        var returned = await Assert.ThrowsAnyAsync<PublishException>(() => publisher.PublishAsync(Event(2, "record-1", "ticket.updated"), CancellationToken));
        Assert.True(returned.IsReturn);

        // Hatadan sonra yayıncı yeni bir bağlantıyla çalışmaya devam eder.
        await publisher.PublishAsync(Event(3, "record-2", "ticket.created"), CancellationToken);
        Assert.Single(await rabbit.WaitForAsync(queue, "record-2", count: 1));
    }

    [Fact]
    public async Task EventTypes_LimitWhatIsSent()
    {
        var queue = await rabbit.DeclareQueueAsync();

        await using var publisher = rabbit.CreatePublisher(options =>
        {
            rabbit.Configure(options);
            options.RoutingKey = queue;
            options.EventTypes.Add("ticket.*");
        });

        // Süzülen olay teslim edilmiş sayılır (hata fırlatmaz) ama gönderilmez.
        await publisher.PublishAsync(Event(1, "record-1", "order.created"), CancellationToken);
        await publisher.PublishAsync(Event(2, "record-1", "ticket.created"), CancellationToken);

        Assert.Equal(["ticket.created"], (await rabbit.WaitForAsync(queue, "record-1", count: 1)).Select(message => message.Properties.Type));
    }

    [Fact]
    public async Task ClosedConnection_IsReopenedForTheNextEvent()
    {
        var queue = await rabbit.DeclareQueueAsync();

        await using var publisher = rabbit.CreatePublisher(options =>
        {
            rabbit.Configure(options);
            options.RoutingKey = queue;
        });

        await publisher.PublishAsync(Event(1, "record-1"), CancellationToken);
        await rabbit.CloseAllConnectionsAsync();

        // Kopan bağlantı fark edilmeden gönderilen ilk olay hata alabilir (kutuda kalır ve yeniden denenir); sonraki
        // deneme yeni bir bağlantıyla geçer.
        var deadline = DateTimeOffset.UtcNow + Timeout;

        while (true)
        {
            try
            {
                await publisher.PublishAsync(Event(2, "record-2"), CancellationToken);
                break;
            }
            catch (Exception exception) when (exception is AlreadyClosedException or OperationInterruptedException or IOException or TimeoutException)
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, $"The publisher never recovered: {exception.Message}");
            }
        }

        Assert.NotEmpty(await rabbit.WaitForAsync(queue, "record-2", count: 1));
    }

    [Fact]
    public async Task WrongCredentialsAndMissingExchange_FailLoudly()
    {
        await using var denied = rabbit.CreatePublisher(options =>
        {
            rabbit.Configure(options);
            options.RoutingKey = "anywhere";
            options.Password = "not-the-password";
        });

        var refused = await Assert.ThrowsAsync<BrokerUnreachableException>(() => denied.PublishAsync(Event(1, "record-1"), CancellationToken));
        Assert.DoesNotContain("not-the-password", refused.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rabbit.Password, refused.ToString(), StringComparison.Ordinal);

        await using var nowhere = rabbit.CreatePublisher(options =>
        {
            rabbit.Configure(options);
            options.Exchange = $"missing-{Guid.NewGuid():N}";
        });

        // Olmayan exchange'e gönderim aracının kanalı kapatmasıyla sonuçlanır; olay teslim edilmiş sayılmaz.
        await Assert.ThrowsAnyAsync<Exception>(() => nowhere.PublishAsync(Event(2, "record-1"), CancellationToken));

        // Ulaşılamayan aracı, sürecin kapanması sanılacak bir iptal değil, bir hatadır.
        await using var unreachable = rabbit.CreatePublisher(options =>
        {
            rabbit.Configure(options);
            options.RoutingKey = "anywhere";
            options.Port = 1;
            options.Timeout = TimeSpan.FromSeconds(3);
        });

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => unreachable.PublishAsync(Event(3, "record-1"), CancellationToken));
        Assert.IsNotAssignableFrom<OperationCanceledException>(failure);
    }

    [Fact]
    public void Options_AreValidatedWhenThePublisherIsFirstCreated()
    {
        var invalid = new Action<HaldusRabbitMqOptions>[]
        {
            options => options.HostName = " ",
            options => options.Port = 0,
            options => options.Port = 70000,
            options => options.VirtualHost = "",
            options => options.Timeout = TimeSpan.Zero,
            options => options.Timeout = TimeSpan.FromDays(2),
            options => options.EventTypes.Add(" "),

            // Varsayılan exchange'de hedef kuyruğun adı verilmelidir.
            options => options.RoutingKey = null,
        };

        foreach (var adjust in invalid)
        {
            var services = new ServiceCollection();
            services.AddHaldusRabbitMqPublisher(options =>
            {
                options.RoutingKey = "events";
                adjust(options);
            });

            using var provider = services.BuildServiceProvider();
            var failure = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IHaldusEventPublisher>);
            Assert.StartsWith("The RabbitMQ publisher is not configured correctly", failure.Message, StringComparison.Ordinal);
        }

        // Kurmak bağlantı açmaz; bağlantı ilk olayda kurulur.
        using var valid = new ServiceCollection().AddHaldusRabbitMqPublisher(options => options.Exchange = "events").BuildServiceProvider();
        Assert.IsType<HaldusRabbitMqPublisher>(Assert.Single(valid.GetServices<IHaldusEventPublisher>()));
    }

    private static string Text(object? header) => Encoding.UTF8.GetString((byte[])header!);

    private static HaldusEvent Event(long id, string recordId, string type = "ticket.created") => new(
        id,
        type,
        "ticket",
        recordId,
        TenantId: null,
        UserId: "ada",
        JsonSerializer.SerializeToElement(new { subject = "Direct" }),
        DateTimeOffset.UtcNow);

    // Mesaj kuyruğa, olay tablodan silinmeden önce ulaşır; silinmesi beklenir.
    private async Task WaitForPendingAsync(string recordId, int expected)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;

        while (true)
        {
            var pending = -1;
            await fixture.WithDbContextAsync(async context =>
                pending = await context.Set<HaldusOutboxEvent>().CountAsync(item => item.RecordId == recordId, CancellationToken));

            if (pending == expected)
            {
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Expected {expected} pending events for {recordId}, found {pending}.");
            await Task.Delay(50, CancellationToken);
        }
    }

    private static async Task<TicketResponse> CreateTicketAsync(HttpClient client, string subject) =>
        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Post, "/tickets", new { subject }));

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
}

/// <summary>Gerçek bir RabbitMQ aracısı. Kuyruklar ve exchange'ler testlerin içinden oluşturulur ve okunur.</summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    // rabbitmq:4 (yönetim eklentisi olmayan imaj)
    private static readonly string Image = Environment.GetEnvironmentVariable("HALDUS_TEST_RABBITMQ_IMAGE")
        ?? "rabbitmq@sha256:b18964ddaeef52f2dd60413573d843240c02681cb1a022365aaae236cee05677";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IContainer _container;
    private readonly Dictionary<string, List<Received>> _received = [];
    private IConnection? _connection;
    private IChannel? _channel;

    public RabbitMqFixture()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(5672, assignRandomHostPort: true)
            .WithEnvironment("RABBITMQ_DEFAULT_USER", UserName)
            .WithEnvironment("RABBITMQ_DEFAULT_PASS", Password)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
            .Build();
    }

    public string UserName { get; } = "haldus" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    public string Password { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private IChannel Channel => _channel ?? throw new InvalidOperationException("The broker has not been started.");

    public void Configure(HaldusRabbitMqOptions options)
    {
        options.HostName = _container.Hostname;
        options.Port = _container.GetMappedPublicPort(5672);
        options.UserName = UserName;
        options.Password = Password;
    }

    /// <summary>Uygulamanın kuracağı gibi, ayarlardan kurulan bir yayıncı.</summary>
    public HaldusRabbitMqPublisher CreatePublisher(Action<HaldusRabbitMqOptions> configure)
    {
        var options = new HaldusRabbitMqOptions();
        configure(options);
        return new HaldusRabbitMqPublisher(Microsoft.Extensions.Options.Options.Create(options));
    }

    /// <summary>Kalıcı bir kuyruk; exchange verilirse o da (topic olarak) oluşturulur ve kuyruk ona bağlanır.</summary>
    public async Task<string> DeclareQueueAsync(string? exchange = null, string? binding = null)
    {
        var queue = $"events-{Guid.NewGuid():N}";
        await Channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: CancellationToken);

        if (exchange is not null)
        {
            await Channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, cancellationToken: CancellationToken);
            await Channel.QueueBindAsync(queue, exchange, binding ?? "#", cancellationToken: CancellationToken);
        }

        return queue;
    }

    /// <summary>Kuyruktan, verilen kaydın en az <paramref name="count"/> mesajı okunana kadar bekler; geliş sırasıyla döner.</summary>
    public async Task<List<Received>> WaitForAsync(string queue, string recordId, int count)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;

        if (!_received.TryGetValue(queue, out var all))
        {
            _received[queue] = all = [];
        }

        while (true)
        {
            while (await Channel.BasicGetAsync(queue, autoAck: true, CancellationToken) is { } message)
            {
                var body = message.Body.ToArray();
                using var parsed = JsonDocument.Parse(body);
                all.Add(new Received(body, message.BasicProperties, parsed.RootElement.GetProperty("recordId").GetString()!));
            }

            var matching = all.Where(message => message.RecordId == recordId).ToList();

            if (matching.Count >= count)
            {
                return matching;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Expected {count} messages for {recordId} in {queue}, got {matching.Count}.");
            await Task.Delay(100, CancellationToken);
        }
    }

    /// <summary>Yayıncıların bağlantılarını aracı tarafında kapatır (testin kendi bağlantısı yeniden kurulur).</summary>
    public async Task CloseAllConnectionsAsync()
    {
        var result = await _container.ExecAsync(["rabbitmqctl", "close_all_connections", "closed by the test"], CancellationToken);
        Assert.True(result.ExitCode == 0, result.Stderr);

        await DisconnectAsync();
        await ConnectAsync();
    }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await ConnectAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await _container.DisposeAsync();
    }

    private async Task ConnectAsync()
    {
        var factory = new ConnectionFactory
        {
            HostName = _container.Hostname,
            Port = _container.GetMappedPublicPort(5672),
            UserName = UserName,
            Password = Password,
        };

        _connection = await factory.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();
    }

    private async Task DisconnectAsync()
    {
        try
        {
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }
        }
        catch (Exception exception) when (exception is AlreadyClosedException or OperationInterruptedException or IOException)
        {
            // Aracı bağlantıyı zaten kapatmış olabilir.
        }

        _channel = null;
        _connection = null;
    }

    public sealed record Received(byte[] Body, IReadOnlyBasicProperties Properties, string RecordId);
}
