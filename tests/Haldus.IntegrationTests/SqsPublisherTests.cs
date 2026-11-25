using Amazon.SQS;
using Amazon.SQS.Model;
using System.Net;
using System.Net.Http.Headers;
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
using Microsoft.Extensions.Hosting;

namespace Haldus.IntegrationTests;

/// <summary>SQS yayıncısının ayarlarının doğrulanması; bir sunucu gerekmez.</summary>
public sealed class SqsPublisherOptionsTests
{
    private const string AccessKey = "AKIDEXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";

    [Fact]
    public void Options_AreValidatedWhenThePublisherIsFirstCreated()
    {
        var invalid = new Action<HaldusSqsOptions>[]
        {
            options => options.QueueUrl = null,
            options => options.QueueUrl = new Uri("/123456789012/orders", UriKind.Relative),
            options => options.QueueUrl = new Uri("ftp://sqs.example/123456789012/orders"),
            options => options.QueueUrl = new Uri("https://sqs.example/123456789012/orders?token=1"),
            options => options.QueueUrl = new Uri("https://user:password@sqs.example/123456789012/orders"),
            options => options.QueueUrl = new Uri("https://sqs.example/123456789012/"),
            options => options.QueueUrl = new Uri("https://sqs.example/123456789012/.fifo"),
            options => options.QueueUrl = new Uri("https://sqs.example/123456789012/has%20space"),
            options => options.QueueUrl = new Uri("https://sqs.example/123456789012/" + new string('q', 81)),
            options => options.SecretKey = "",
            options => options.AccessKey = "",
            options => options.Region = "",
            options => options.Region = "eu west/1",
            options => options.Timeout = TimeSpan.Zero,
            options => options.Timeout = TimeSpan.FromDays(2),
            options => options.EventTypes.Add(" "),
            options => options.ServiceUrl = new Uri("http://localhost:9324/base"),
            options => options.ServiceUrl = new Uri("ftp://localhost"),
            options => options.ServiceUrl = new Uri("relative", UriKind.Relative),
        };

        foreach (var adjust in invalid)
        {
            var services = new ServiceCollection();

            // Kaydetmek hata vermez (uygulama, şema üretimi sırasında kimlik bilgisi olmadan da açılır); kurmak verir.
            services.AddHaldusSqsPublisher(options =>
            {
                Configure(options);
                adjust(options);
            });

            using var provider = services.BuildServiceProvider();
            var failure = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IHaldusEventPublisher>);

            Assert.StartsWith("The SQS publisher is not configured correctly", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretKey, failure.ToString(), StringComparison.Ordinal);
        }

        using var valid = new ServiceCollection().AddHaldusSqsPublisher(Configure).BuildServiceProvider();
        Assert.IsType<HaldusSqsPublisher>(Assert.Single(valid.GetServices<IHaldusEventPublisher>()));
    }

    private static void Configure(HaldusSqsOptions options)
    {
        options.QueueUrl ??= new Uri("https://sqs.us-east-1.amazonaws.com/123456789012/orders");
        options.AccessKey = AccessKey;
        options.SecretKey = SecretKey;
    }
}

/// <summary>SDK'nın isteklerini testin verdiği işleyiciden geçirir.</summary>
internal sealed class HandlerClientFactory(Func<HttpMessageHandler> handler) : Amazon.Runtime.HttpClientFactory
{
    public override HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig clientConfig) => new(handler(), disposeHandler: false);
}

/// <summary>Olayların bir SQS kuyruğuna gönderilmesi (<c>AddHaldusSqsPublisher</c>), SQS uyumlu bir sunucuya (ElasticMQ) karşı.</summary>
[Collection(ShopCollection.Name)]
public sealed class SqsPublisherTests(ShopFixture fixture, ElasticMqFixture sqs) : IClassFixture<ElasticMqFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Events_ReachTheQueueWithTheirAttributes()
    {
        var queue = await sqs.CreateQueueAsync();
        await using var factory = fixture.CreateFactory(services => services.AddHaldusSqsPublisher(queue.Configure));
        var client = factory.CreateClient();

        var ticket = await CreateTicketAsync(client, "Printer");
        var message = Assert.Single(await queue.WaitForAsync(ticket.Id.ToString(), count: 1));

        // Gövde olayın kendisidir; kaydın o andaki halini yanıt biçiminde taşır.
        Assert.Equal(
            ["id", "type", "resource", "recordId", "tenantId", "userId", "data", "occurredAt"],
            message.Event.EnumerateObject().Select(property => property.Name));
        Assert.Equal("ticket.created", message.Event.GetProperty("type").GetString());
        Assert.Equal("ticket", message.Event.GetProperty("resource").GetString());
        Assert.Equal("ada", message.Event.GetProperty("userId").GetString());
        Assert.Equal("Printer", message.Event.GetProperty("data").GetProperty("subject").GetString());

        // Tüketici, gövdeyi çözmeden yönlendirebilsin ve yinelenen olayı tanıyabilsin diye öznitelikler de taşınır.
        var id = message.Event.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            [("EventId", "Number", id), ("EventType", "String", "ticket.created"), ("Resource", "String", "ticket")],
            message.Attributes.OrderBy(attribute => attribute.Key, StringComparer.Ordinal).Select(attribute => (attribute.Key, attribute.Value.Type, attribute.Value.Value)));

        // Standart kuyrukta grup ve yineleme kimliği yoktur.
        Assert.False(message.System.ContainsKey("MessageGroupId"));
        Assert.False(message.System.ContainsKey("MessageDeduplicationId"));

        // Gönderilen olay bekleme tablosundan silinir.
        await WaitForPendingAsync(ticket.Id.ToString(), expected: 0);
    }

    [Fact]
    public async Task EventTypes_LimitWhatIsSent()
    {
        var queue = await sqs.CreateQueueAsync();
        await using var factory = fixture.CreateFactory(services => services.AddHaldusSqsPublisher(options =>
        {
            queue.Configure(options);
            options.EventTypes.Add("ticket.close");
            options.EventTypes.Add("reply.*");
        }));
        var client = factory.CreateClient();

        var ticket = await CreateTicketAsync(client, "Scanner");
        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Patch, $"/tickets/{ticket.Id}", new { subject = "Scanner, again" }));
        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Post, $"/tickets/{ticket.Id}/close"));

        var discussion = await ReadAsync<DiscussionResponse>(await SendAsync(client, HttpMethod.Post, "/discussions", new { title = "Queues" }));
        var replies = $"/discussions/{discussion.Id}/replies";
        var reply = await ReadAsync<ReplyResponse>(await SendAsync(client, HttpMethod.Post, replies, new { text = "Hi", score = 1 }));
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Delete, $"{replies}/{reply.Id}")).StatusCode);

        // Olaylar sırayla işlenir: yanıtın olayları geldiyse biletinkiler çoktan işlenmiştir.
        var ofReply = await queue.WaitForAsync(reply.Id.ToString(), count: 2);
        Assert.Equal(["reply.created", "reply.deleted"], ofReply.Select(message => message.Type));

        // Tam adıyla sayılan tür tek başına, "kaynak.*" kaynağın bütün olaylarıyla gelir; gerisi gönderilmez.
        Assert.Equal(["ticket.close"], (await queue.ReceiveAsync()).Where(message => message.RecordId == ticket.Id.ToString()).Select(message => message.Type));
        Assert.All(queue.All, message => Assert.True(message.Type is "ticket.close" || message.Type.StartsWith("reply.", StringComparison.Ordinal), message.Type));

        // Süzülen olay da teslim edilmiş sayılır ve bekleme tablosundan silinir.
        await WaitForPendingAsync(ticket.Id.ToString(), expected: 0);
    }

    [Fact]
    public async Task FifoQueue_GroupsByRecordAndDropsARedeliveredEvent()
    {
        var queue = await sqs.CreateQueueAsync(fifo: true);
        var subject = $"Flaky {Guid.NewGuid():N}";
        var sends = new SendCounter(subject);

        // SQS'ten sonra gelen yayıncı olayı ilk denemede reddeder; olay yeniden denenirken SQS'e ikinci kez gönderilir.
        var flaky = new FailingOnce(published => published.Type == "ticket.created" && published.Data.GetProperty("subject").GetString() == subject);

        await using var factory = fixture.CreateFactory(services =>
        {
            services.AddHaldusSqsPublisher(options =>
            {
                queue.Configure(options);
                options.ConfigureClient = config => config.HttpClientFactory = new HandlerClientFactory(() => sends);
            });
            services.AddSingleton<IHaldusEventPublisher>(flaky);
        });
        var client = factory.CreateClient();

        var ticket = await CreateTicketAsync(client, subject);
        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Patch, $"/tickets/{ticket.Id}", new { subject = "Renamed" }));

        // Kuyruk sıralıdır: güncelleme geldiyse yinelenen oluşturma olayı (ayıklanmasaydı) ondan önce gelmiş olurdu.
        var messages = await queue.WaitForAsync(ticket.Id.ToString(), count: 2);
        Assert.Equal(["ticket.created", "ticket.updated"], messages.Select(message => message.Type));
        Assert.Equal((1, 2), (flaky.Failures, sends.Count));

        // Grup kayıttır; yineleme kimliği olayı ayırt eder.
        Assert.All(messages, message =>
        {
            Assert.Equal($"ticket:{ticket.Id}", message.System["MessageGroupId"]);
            Assert.Equal($"ticket:{ticket.Id}:{message.Event.GetProperty("id").GetInt64()}", message.System["MessageDeduplicationId"]);
        });
        Assert.NotEqual(messages[0].System["MessageDeduplicationId"], messages[1].System["MessageDeduplicationId"]);

        await WaitForPendingAsync(ticket.Id.ToString(), expected: 0);
        Assert.Equal(2, (await queue.ReceiveAsync()).Count(message => message.RecordId == ticket.Id.ToString()));
    }

    [Fact]
    public async Task RedeliveredEvent_IsDroppedByAFifoQueueButNotByAStandardQueue()
    {
        var fifo = await sqs.CreateQueueAsync(fifo: true);
        var standard = await sqs.CreateQueueAsync();
        var toFifo = sqs.CreatePublisher(fifo.Configure);
        var toStandard = sqs.CreatePublisher(standard.Configure);

        var first = Event(7, "record-1");
        var sameNumberOfAnotherRecord = Event(7, "record-2");
        var next = Event(8, "record-1");

        foreach (var published in new[] { first, first, sameNumberOfAnotherRecord, next, first })
        {
            await toFifo.PublishAsync(published, CancellationToken);
            await toStandard.PublishAsync(published, CancellationToken);
        }

        // Olayın numarası yalnızca kendi veritabanında benzersizdir; başka bir kaydın aynı numaralı olayı düşmez.
        var kept = await fifo.WaitForAsync("record-1", count: 2);
        Assert.Equal([7L, 8L], kept.Select(message => message.Event.GetProperty("id").GetInt64()));
        Assert.Single(await fifo.WaitForAsync("record-2", count: 1));
        Assert.Equal(3, (await fifo.ReceiveAsync()).Count);

        // Standart kuyruk ayıklamaz; tüketici yinelenen olayı EventId özniteliğinden tanımalıdır.
        await standard.WaitForAsync("record-1", count: 4);
        Assert.Equal(5, (await standard.ReceiveAsync()).Count);
        Assert.Equal(3, standard.All.Count(message => message.Attributes["EventId"].Value == "7" && message.RecordId == "record-1"));
    }

    [Fact]
    public async Task MissingQueue_FailsLoudlyKeepsTheEventAndDoesNotStopTheApplication()
    {
        // Sıradaki başka testlerden kalan olaylar önce çalışan bir kuyruğa boşaltılır: teslim edilemeyen olay
        // arkasındakileri beklettiği için, yoksa bu testin olayı hiç denenmeyebilirdi.
        var drain = await sqs.CreateQueueAsync();

        await using (var draining = fixture.CreateFactory(services => services.AddHaldusSqsPublisher(drain.Configure)))
        {
            var last = await CreateTicketAsync(draining.CreateClient(), "Last of the backlog");
            await drain.WaitForAsync(last.Id.ToString(), count: 1);
            await WaitForPendingAsync(last.Id.ToString(), expected: 0);
        }

        var queue = sqs.Queue($"later-{Guid.NewGuid():N}");

        // Yayıncının kendisi servisin hata koduyla, kuyruğun adını söyleyerek reddeder.
        var direct = await Assert.ThrowsAsync<HaldusSqsException>(() => sqs.CreatePublisher(queue.Configure).PublishAsync(Event(1, "record-1"), CancellationToken));
        Assert.Equal((HttpStatusCode.BadRequest, "QueueDoesNotExist"), (direct.StatusCode, direct.ErrorCode));
        Assert.Equal($"SQS SendMessage failed for queue '{queue.Name}': 400 QueueDoesNotExist", direct.Message);
        Assert.DoesNotContain(sqs.SecretKey, direct.ToString(), StringComparison.Ordinal);

        await using var factory = fixture.CreateFactory(services => services.AddHaldusSqsPublisher(queue.Configure));
        var client = factory.CreateClient();
        var ticket = await CreateTicketAsync(client, "Nowhere to go");

        // Olay kaybolmaz: nedeniyle birlikte bekleme tablosunda kalır ve sonra yeniden denenir.
        var waiting = await WaitForAttemptAsync(ticket.Id.ToString());
        Assert.Equal(direct.Message, waiting.LastError);
        Assert.Null(waiting.DeadAt);
        Assert.DoesNotContain(sqs.SecretKey, waiting.LastError, StringComparison.Ordinal);

        // Uygulama ayaktadır.
        await ReadAsync<TicketResponse>(await SendAsync(client, HttpMethod.Get, $"/tickets/{ticket.Id}"));
        var second = await CreateTicketAsync(client, "Still serving");
        Assert.Equal(1, await PendingAsync(second.Id.ToString()));

        // Kuyruk oluşturulunca bekleyen olaylar, yazıldıkları sırayla gönderilir.
        await queue.CreateAsync();
        await queue.WaitForAsync(second.Id.ToString(), count: 1);
        Assert.Equal(
            [ticket.Id.ToString(), second.Id.ToString()],
            queue.All.Select(message => message.RecordId).Where(id => id == ticket.Id.ToString() || id == second.Id.ToString()));
        await WaitForPendingAsync(ticket.Id.ToString(), expected: 0);
        await WaitForPendingAsync(second.Id.ToString(), expected: 0);
    }

    [Fact]
    public async Task InvalidOptions_StopTheApplicationWhenItStarts()
    {
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var factory = fixture.CreateFactory(services =>
        {
            services.AddHaldusSqsPublisher(options =>
            {
                sqs.Queue("orders").Configure(options);
                options.SecretKey = "";
            });

            // Duran uygulamanın servisleri de atılır; durduğu bu yüzden, açılmadan önce kurulan bir dinleyiciyle izlenir.
            services.AddHostedService(provider => new StopProbe(provider.GetRequiredService<IHostApplicationLifetime>(), stopping));
        });

        try
        {
            using var client = factory.CreateClient();
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // Uygulama, istemci kurulamadan durmuş (ve servisleri atılmış) olabilir.
        }

        // Yayıncı olay servisi başlarken kurulur; yanlış ayar ilk olayı beklemeden uygulamayı durdurur (olaylar
        // denenip vazgeçilmek yerine kutuda bekler).
        await stopping.Task.WaitAsync(Timeout, CancellationToken);
    }

    private static HaldusEvent Event(long id, string recordId) => new(
        id,
        "ticket.created",
        "ticket",
        recordId,
        TenantId: null,
        UserId: "ada",
        JsonSerializer.SerializeToElement(new { subject = "Direct" }),
        DateTimeOffset.UtcNow);

    private async Task<int> PendingAsync(string recordId)
    {
        var count = -1;
        await fixture.WithDbContextAsync(async context =>
            count = await context.Set<HaldusOutboxEvent>().CountAsync(item => item.RecordId == recordId, CancellationToken));
        return count;
    }

    // Mesaj kuyruğa, olay tablodan silinmeden önce ulaşır; silinmesi beklenir.
    private async Task WaitForPendingAsync(string recordId, int expected)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;

        while (await PendingAsync(recordId) is var pending && pending != expected)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"Expected {expected} pending events for {recordId}, found {pending}.");
            await Task.Delay(50, CancellationToken);
        }
    }

    private async Task<HaldusOutboxEvent> WaitForAttemptAsync(string recordId)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;

        while (true)
        {
            HaldusOutboxEvent? waiting = null;
            await fixture.WithDbContextAsync(async context => waiting = await context.Set<HaldusOutboxEvent>()
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.RecordId == recordId, CancellationToken));

            if (waiting is { Attempts: > 0 })
            {
                return waiting;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"The event of {recordId} was never attempted.");
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

    /// <summary>Bir olayı ilk denemede reddeden yayıncı; olayın bütün yayıncılara yeniden teslim edilmesine yol açar.</summary>
    private sealed class FailingOnce(Func<HaldusEvent, bool> matches) : IHaldusEventPublisher
    {
        private int _failures;

        public int Failures => _failures;

        public Task PublishAsync(HaldusEvent published, CancellationToken cancellationToken)
        {
            if (matches(published) && Interlocked.CompareExchange(ref _failures, 1, 0) == 0)
            {
                throw new InvalidOperationException("Simulated outage");
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Uygulamanın durmaya başladığını bildirir.</summary>
    private sealed class StopProbe : IHostedService
    {
        public StopProbe(IHostApplicationLifetime lifetime, TaskCompletionSource stopping) =>
            lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Gövdesinde verilen metin geçen gönderimleri sayar.</summary>
    private sealed class SendCounter(string marker) : DelegatingHandler(new SocketsHttpHandler())
    {
        private int _count;

        public int Count => _count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null && (await request.Content.ReadAsStringAsync(cancellationToken)).Contains(marker, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _count);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// SQS uyumlu bir sunucu (ElasticMQ). Kuyruklar testlerin içinden, AWS SDK istemcisiyle oluşturulur ve okunur.
/// ElasticMQ imzayı doğrulamaz: bu testler kuyruğun davranışını sınar, kimlik bilgisinin AWS'de kabul edileceğini değil.
/// </summary>
public sealed class ElasticMqFixture : IAsyncLifetime
{
    private const string Region = "eu-test-1";
    private const string Account = "000000000000";

    // softwaremill/elasticmq-native:1.6.11
    private static readonly string Image = Environment.GetEnvironmentVariable("HALDUS_TEST_ELASTICMQ_IMAGE")
        ?? "softwaremill/elasticmq-native@sha256:fe3af4a8dd59f310b20e07ec3499cfdeb16a14f518c95ad7e70f200845dce3f5";

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(9324, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(9324).ForPath("/health")))
        .Build();

    private AmazonSQSClient? _admin;
    private readonly List<ServiceProvider> _providers = [];

    public string AccessKey { get; } = "haldus" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    public string SecretKey { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private Uri Endpoint => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(9324)}");

    /// <summary>Adı verilen kuyruk; var olması gerekmez.</summary>
    public TestQueue Queue(string name) => new(this, name, new Uri(Endpoint, $"/{Account}/{name}"));

    public async Task<TestQueue> CreateQueueAsync(bool fifo = false)
    {
        var queue = Queue($"events-{Guid.NewGuid():N}{(fifo ? ".fifo" : string.Empty)}");
        await queue.CreateAsync();
        return queue;
    }

    /// <summary>Uygulamanın kuracağı gibi, servislerden kurulan bir yayıncı.</summary>
    public IHaldusEventPublisher CreatePublisher(Action<HaldusSqsOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddHaldusSqsPublisher(configure);

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IHaldusEventPublisher>();
    }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _admin = new AmazonSQSClient(
            new Amazon.Runtime.BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonSQSConfig { ServiceURL = Endpoint.GetLeftPart(UriPartial.Authority), AuthenticationRegion = Region });
    }

    // Kuyruğu hazırlamak ve içine bakmak için, yayıncıdan bağımsız bir istemci.
    private AmazonSQSClient Admin => _admin ?? throw new InvalidOperationException("The server has not been started.");

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        _admin?.Dispose();
        await _container.DisposeAsync();
    }

    public sealed record QueueMessage(
        string Body,
        JsonElement Event,
        IReadOnlyDictionary<string, (string Type, string Value)> Attributes,
        IReadOnlyDictionary<string, string> System)
    {
        public string Type => Event.GetProperty("type").GetString()!;

        public string RecordId => Event.GetProperty("recordId").GetString()!;
    }

    public sealed class TestQueue
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly ElasticMqFixture _owner;
        private readonly List<QueueMessage> _messages = [];

        internal TestQueue(ElasticMqFixture owner, string name, Uri url)
        {
            _owner = owner;
            Name = name;
            Url = url;
        }

        public string Name { get; }

        public Uri Url { get; }

        /// <summary>Kuyruktan şimdiye kadar okunan bütün mesajlar, kuyruğun verdiği sırayla.</summary>
        public IReadOnlyList<QueueMessage> All => _messages;

        public void Configure(HaldusSqsOptions options)
        {
            options.ServiceUrl = _owner.Endpoint;
            options.Region = Region;
            options.QueueUrl = Url;
            options.AccessKey = _owner.AccessKey;
            options.SecretKey = _owner.SecretKey;
        }

        public Task CreateAsync() => _owner.Admin.CreateQueueAsync(
            new CreateQueueRequest
            {
                QueueName = Name,
                Attributes = Name.EndsWith(".fifo", StringComparison.Ordinal) ? new Dictionary<string, string> { ["FifoQueue"] = "true" } : [],
            },
            CancellationToken);

        /// <summary>Kuyrukta bekleyen mesajları okur (ve kuyruktan siler); şimdiye kadar okunanların hepsini döndürür.</summary>
        public async Task<IReadOnlyList<QueueMessage>> ReceiveAsync()
        {
            while (true)
            {
                var received = await _owner.Admin.ReceiveMessageAsync(
                    new ReceiveMessageRequest
                    {
                        QueueUrl = Url.AbsoluteUri,
                        MaxNumberOfMessages = 10,
                        MessageAttributeNames = ["All"],
                        MessageSystemAttributeNames = ["All"],
                    },
                    CancellationToken);

                if (received.Messages is not { Count: > 0 } messages)
                {
                    return [.. _messages];
                }

                foreach (var message in messages)
                {
                    using var parsed = JsonDocument.Parse(message.Body);

                    _messages.Add(new QueueMessage(
                        message.Body,
                        parsed.RootElement.Clone(),
                        (message.MessageAttributes ?? []).ToDictionary(attribute => attribute.Key, attribute => (attribute.Value.DataType, attribute.Value.StringValue)),
                        new Dictionary<string, string>(message.Attributes ?? [])));

                    await _owner.Admin.DeleteMessageAsync(Url.AbsoluteUri, message.ReceiptHandle, CancellationToken);
                }
            }
        }
        public async Task<List<QueueMessage>> WaitForAsync(string recordId, int count)
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;

            while (true)
            {
                var matching = (await ReceiveAsync()).Where(message => message.RecordId == recordId).ToList();

                if (matching.Count >= count)
                {
                    return matching;
                }

                Assert.True(
                    DateTimeOffset.UtcNow < deadline,
                    $"Expected {count} messages for {recordId} in {Name}, got: {string.Join(", ", matching.Select(message => message.Type))}");
                await Task.Delay(100, CancellationToken);
            }
        }
    }
}
