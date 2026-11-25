using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Engagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus.IntegrationTests;

/// <summary>Olayların Kafka'ya yazılması (<c>AddHaldusKafkaPublisher</c>), gerçek bir kümeye (tek düğüm) karşı.</summary>
[Collection(ShopCollection.Name)]
public sealed class KafkaPublisherTests(ShopFixture fixture, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Events_ReachTheTopicThroughTheOutboxKeyedByRecord()
    {
        var topic = await kafka.CreateTopicAsync(partitions: 3);

        await using var factory = fixture.CreateFactory(services => services.AddHaldusKafkaPublisher(options => kafka.Configure(options, topic)));
        var client = factory.CreateClient();

        var ticket = await CreateTicketAsync(client, "Printer");
        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Patch, $"/tickets/{ticket.Id}", new { subject = "Printer on fire" }));

        var messages = await kafka.WaitForAsync(topic, $"ticket:{ticket.Id}", count: 2);

        // Anahtar kayıttır: bir kaydın olayları aynı bölüme düşer ve yazıldıkları sırayla okunur.
        Assert.Single(messages.Select(message => message.Partition).Distinct());
        Assert.Equal(["ticket.created", "ticket.updated"], messages.Select(message => Header(message, "event-type")));
        Assert.True(messages[0].Offset < messages[1].Offset);

        var created = messages[0];
        using var body = JsonDocument.Parse(created.Message.Value);

        // Gövde olayın kendisidir; kaydın o andaki halini yanıt biçiminde taşır.
        Assert.Equal(
            ["id", "type", "resource", "recordId", "tenantId", "userId", "data", "occurredAt"],
            body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Printer", body.RootElement.GetProperty("data").GetProperty("subject").GetString());

        // Tüketici, gövdeyi çözmeden süzebilsin ve yinelenen olayı tanıyabilsin diye başlıklar da taşınır.
        Assert.Equal(body.RootElement.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), Header(created, "event-id"));
        Assert.Equal("ticket", Header(created, "resource"));

        // Gönderilen olay bekleme tablosundan silinir.
        await WaitForPendingAsync(ticket.Id.ToString(), expected: 0);
    }

    [Fact]
    public async Task SameRecord_AlwaysLandsInTheSamePartition()
    {
        var topic = await kafka.CreateTopicAsync(partitions: 6);
        using var publisher = kafka.CreatePublisher(options => kafka.Configure(options, topic));

        foreach (var index in Enumerable.Range(1, 12))
        {
            await publisher.PublishAsync(Event(index, index % 2 == 0 ? "even" : "odd"), CancellationToken);
        }

        var even = await kafka.WaitForAsync(topic, "ticket:even", count: 6);
        var odd = await kafka.WaitForAsync(topic, "ticket:odd", count: 6);

        Assert.Single(even.Select(message => message.Partition).Distinct());
        Assert.Single(odd.Select(message => message.Partition).Distinct());
        Assert.Equal(["2", "4", "6", "8", "10", "12"], even.Select(message => Header(message, "event-id")));
    }

    [Fact]
    public async Task EventTypes_LimitWhatIsSent()
    {
        var topic = await kafka.CreateTopicAsync();
        using var publisher = kafka.CreatePublisher(options =>
        {
            kafka.Configure(options, topic);
            options.EventTypes.Add("ticket.*");
        });

        // Süzülen olay teslim edilmiş sayılır (hata fırlatmaz) ama gönderilmez.
        await publisher.PublishAsync(Event(1, "record-1", "order.created"), CancellationToken);
        await publisher.PublishAsync(Event(2, "record-1", "ticket.created"), CancellationToken);

        Assert.Equal(["ticket.created"], (await kafka.WaitForAsync(topic, "ticket:record-1", count: 1)).Select(message => Header(message, "event-type")));
    }

    [Fact]
    public async Task MissingTopicAndUnreachableCluster_FailLoudly()
    {
        using var nowhere = kafka.CreatePublisher(options =>
        {
            kafka.Configure(options, $"missing-{Guid.NewGuid():N}");
            options.Timeout = TimeSpan.FromSeconds(5);
        });

        // Olmayan konuya yazılan olay teslim edilmiş sayılmaz (küme konuyu kendiliğinden oluşturmayacak biçimde çalışır).
        var missing = await Assert.ThrowsAnyAsync<Exception>(() => nowhere.PublishAsync(Event(1, "record-1"), CancellationToken));
        Assert.IsNotAssignableFrom<OperationCanceledException>(missing);

        using var unreachable = kafka.CreatePublisher(options =>
        {
            options.BootstrapServers = "127.0.0.1:1";
            options.Topic = "events";
            options.Timeout = TimeSpan.FromSeconds(3);
        });

        // Ulaşılamayan küme, sürecin kapanması sanılacak bir iptal değil, bir hatadır.
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => unreachable.PublishAsync(Event(2, "record-1"), CancellationToken));
        Assert.IsNotAssignableFrom<OperationCanceledException>(failure);
    }

    [Fact]
    public void Options_AreValidatedWhenThePublisherIsFirstCreated()
    {
        var invalid = new Action<HaldusKafkaOptions>[]
        {
            options => options.BootstrapServers = " ",
            options => options.Topic = "",
            options => options.Topic = "has space",
            options => options.Topic = new string('t', 250),
            options => options.Timeout = TimeSpan.Zero,
            options => options.Timeout = TimeSpan.FromDays(2),
            options => options.EventTypes.Add(" "),
        };

        foreach (var adjust in invalid)
        {
            var services = new ServiceCollection();
            services.AddHaldusKafkaPublisher(options =>
            {
                options.BootstrapServers = "localhost:9092";
                options.Topic = "events";
                adjust(options);
            });

            using var provider = services.BuildServiceProvider();
            var failure = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IHaldusEventPublisher>);
            Assert.StartsWith("The Kafka publisher is not configured correctly", failure.Message, StringComparison.Ordinal);
        }

        // Kurmak bağlantı açmaz; istemci ilk olayda kurulur.
        using var valid = new ServiceCollection()
            .AddHaldusKafkaPublisher(options =>
            {
                options.BootstrapServers = "localhost:9092";
                options.Topic = "events";
            })
            .BuildServiceProvider();
        Assert.IsType<HaldusKafkaPublisher>(Assert.Single(valid.GetServices<IHaldusEventPublisher>()));
    }

    private static string Header(ConsumeResult<string, byte[]> message, string name) =>
        Encoding.UTF8.GetString(message.Message.Headers.GetLastBytes(name));

    private static HaldusEvent Event(long id, string recordId, string type = "ticket.created") => new(
        id,
        type,
        "ticket",
        recordId,
        TenantId: null,
        UserId: "ada",
        JsonSerializer.SerializeToElement(new { subject = "Direct" }),
        DateTimeOffset.UtcNow);

    // Mesaj konuya, olay tablodan silinmeden önce ulaşır; silinmesi beklenir.
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

/// <summary>
/// Tek düğümlü gerçek bir Kafka kümesi (KRaft). Düğüm istemcilere kendi adresini bildirir; bu adres testin ulaşabildiği
/// bağlantı noktası olmalıdır, bu yüzden bağlantı noktası kapsayıcı başlamadan önce seçilir.
/// </summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    // apache/kafka:4
    private static readonly string Image = Environment.GetEnvironmentVariable("HALDUS_TEST_KAFKA_IMAGE")
        ?? "apache/kafka@sha256:77e3df9054047a88b520d0cc46e16696d3b22022e1d580aeccd2632df6532837";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly IContainer _container;
    private readonly int _port = FreePort();

    public KafkaFixture()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(_port, 9094)
            .WithEnvironment("KAFKA_NODE_ID", "1")
            .WithEnvironment("KAFKA_PROCESS_ROLES", "broker,controller")
            .WithEnvironment("KAFKA_LISTENERS", "PLAINTEXT://:9092,CONTROLLER://:9093,HOST://:9094")
            .WithEnvironment("KAFKA_ADVERTISED_LISTENERS", $"PLAINTEXT://localhost:9092,HOST://127.0.0.1:{_port}")
            .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,HOST:PLAINTEXT")
            .WithEnvironment("KAFKA_CONTROLLER_LISTENER_NAMES", "CONTROLLER")
            .WithEnvironment("KAFKA_INTER_BROKER_LISTENER_NAME", "PLAINTEXT")
            .WithEnvironment("KAFKA_CONTROLLER_QUORUM_VOTERS", "1@localhost:9093")
            .WithEnvironment("KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_MIN_ISR", "1")
            .WithEnvironment("KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS", "0")

            // Olmayan konuya yazmak konuyu oluşturmamalıdır; yoksa yanlış yazılmış bir konu adı fark edilmezdi.
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Kafka Server started"))
            .Build();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private string BootstrapServers => $"127.0.0.1:{_port}";

    public void Configure(HaldusKafkaOptions options, string topic)
    {
        options.BootstrapServers = BootstrapServers;
        options.Topic = topic;
    }

    /// <summary>Uygulamanın kuracağı gibi, ayarlardan kurulan bir yayıncı.</summary>
    public HaldusKafkaPublisher CreatePublisher(Action<HaldusKafkaOptions> configure)
    {
        var options = new HaldusKafkaOptions();
        configure(options);
        return new HaldusKafkaPublisher(Microsoft.Extensions.Options.Options.Create(options));
    }

    public async Task<string> CreateTopicAsync(int partitions = 1)
    {
        var topic = $"events-{Guid.NewGuid():N}";

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = partitions, ReplicationFactor = 1 }]);
        return topic;
    }

    /// <summary>Konuyu baştan okur; verilen anahtarın en az <paramref name="count"/> mesajı gelene kadar bekler.</summary>
    public async Task<List<ConsumeResult<string, byte[]>>> WaitForAsync(string topic, string key, int count)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = $"test-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();

        consumer.Subscribe(topic);

        var deadline = DateTimeOffset.UtcNow + Timeout;
        var matching = new List<ConsumeResult<string, byte[]>>();

        try
        {
            while (matching.Count < count)
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, $"Expected {count} messages for {key} in {topic}, got {matching.Count}.");

                // Tüketim eşzamanlı bir çağrıdır; test iş parçacığını uzun süre tutmamak için kısa aralıklarla yoklanır.
                var result = await Task.Run(() => consumer.Consume(TimeSpan.FromMilliseconds(250)), CancellationToken);

                if (result?.Message is not null && result.Message.Key == key)
                {
                    matching.Add(result);
                }
            }
        }
        finally
        {
            consumer.Close();
        }

        return [.. matching.OrderBy(message => message.Offset.Value)];
    }

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
