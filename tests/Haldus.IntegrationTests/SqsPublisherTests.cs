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

/// <summary>İmzalayıcının S3 dışındaki bir servis için de AWS'nin örnekleriyle aynı sonucu verdiği; bir sunucu gerekmez.</summary>
public sealed class SqsSignerTests
{
    // AWS'nin Signature Version 4 sınama takımındaki (aws-sig-v4-test-suite) örnek kimlik; gerçek bir hesaba ait değildir.
    private const string AccessKey = "AKIDEXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly DateTimeOffset Now = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

    [Fact]
    public void Signer_MatchesTheAwsTestSuiteForAnotherService()
    {
        // get-vanilla
        using var get = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");
        Assert.Equal("host;x-amz-date/5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", Sign(get));
        Assert.Equal("20150830T123600Z", Assert.Single(get.Headers.GetValues("x-amz-date")));
        Assert.Equal("example.amazonaws.com", get.Headers.Host);

        // Gövdenin özetini başlıkta yalnızca S3 ister.
        Assert.False(get.Headers.Contains("x-amz-content-sha256"));

        // post-vanilla
        using var post = new HttpRequestMessage(HttpMethod.Post, "https://example.amazonaws.com/");
        Assert.Equal("host;x-amz-date/5da7c1a2acd57cee7505fc6676e4e544621c30862966e37dddb68e92efbe5d6b", Sign(post));

        // get-vanilla-query-order-key-case: sorgu parametreleri sıralanır.
        using var query = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/?Param2=value2&Param1=value1");
        Assert.Equal("host;x-amz-date/b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500", Sign(query));

        // post-header-key-sort: istekteki öteki başlıklar da imzalanır.
        using var header = new HttpRequestMessage(HttpMethod.Post, "https://example.amazonaws.com/");
        header.Headers.TryAddWithoutValidation("My-Header1", "value1");
        Assert.Equal("host;my-header1;x-amz-date/c5410059b04c1ee005303aed430f6e6645f61f4dc9e1461ec8f8916fdf18852c", Sign(header));

        // Takımın özel karakterli yol örnekleri (get-utf8, get-space) yolu bir kez kodlar; S3 dışındaki servislerin
        // beklediği iki kez kodlamayı sınamaz. Öyle bir örnek bulunamadığı için yolun kodlanması burada sınanmıyor;
        // SQS yayıncısı yalnızca köke ("/") istek gönderir.
    }

    [Fact]
    public void Signer_SignsForS3UnlessAnotherServiceIsNamed()
    {
        static AuthenticationHeaderValue Signed(HttpRequestMessage request, string? service)
        {
            if (service is null)
            {
                S3Signer.Sign(request, EmptyHash, AccessKey, SecretKey, "eu-central-1", Now);
            }
            else
            {
                S3Signer.Sign(request, EmptyHash, AccessKey, SecretKey, "eu-central-1", Now, service);
            }

            return request.Headers.Authorization!;
        }

        const string address = "https://example.amazonaws.com/a%20b/c$d?x=1";
        using var implicitS3 = new HttpRequestMessage(HttpMethod.Put, address);
        using var explicitS3 = new HttpRequestMessage(HttpMethod.Put, address);
        using var sqs = new HttpRequestMessage(HttpMethod.Put, address);

        Assert.Equal(Signed(implicitS3, null), Signed(explicitS3, "s3"));
        Assert.Contains("/eu-central-1/s3/aws4_request,SignedHeaders=host;x-amz-content-sha256;x-amz-date,", implicitS3.Headers.Authorization!.Parameter, StringComparison.Ordinal);

        Assert.Contains("/eu-central-1/sqs/aws4_request,SignedHeaders=host;x-amz-date,", Signed(sqs, "sqs").Parameter, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretKey, sqs.ToString(), StringComparison.Ordinal);
    }

    /// <summary>İmzalanan başlıklar ve imza; kimlik ve kapsam her örnekte aynıdır.</summary>
    private static string Sign(HttpRequestMessage request)
    {
        S3Signer.Sign(request, EmptyHash, AccessKey, SecretKey, "us-east-1", Now, "service");

        const string credential = $"Credential={AccessKey}/20150830/us-east-1/service/aws4_request,SignedHeaders=";
        var authorization = request.Headers.Authorization!;
        Assert.Equal(S3Signer.Algorithm, authorization.Scheme);
        Assert.StartsWith(credential, authorization.Parameter, StringComparison.Ordinal);

        return authorization.Parameter![credential.Length..].Replace(",Signature=", "/", StringComparison.Ordinal);
    }
}

/// <summary>SQS yayıncısının gönderdiği istek ve yanıtı yorumlaması; servis yerine sahte bir işleyici yanıt verir.</summary>
public sealed class SqsPublisherRequestTests : IAsyncDisposable
{
    private const string AccessKey = "AKIDEXAMPLE";
    private const string SecretKey = "a-secret-key-that-must-never-be-written-anywhere";

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Request_IsASignedSendMessageCallToTheRegionOfTheQueue()
    {
        var sent = new List<Sent>();
        var publisher = CreatePublisher(
            options =>
            {
                options.Region = "eu-west-3";
                options.QueueUrl = new Uri("https://sqs.eu-west-3.amazonaws.com/123456789012/orders.fifo");
                options.EventTypes.Add("ticket.*");
            },
            async (message, _) =>
            {
                sent.Add(await Sent.OfAsync(message));
                return Json(HttpStatusCode.OK, """{"MD5OfMessageBody":"x","MessageId":"1"}""");
            });

        var published = Event(41, "0198c0de");
        await publisher.PublishAsync(published, CancellationToken);

        // Süzülen tür için istek gönderilmez; olay teslim edilmiş sayılır.
        await publisher.PublishAsync(Event(42, "0198c0de", "reply.created"), CancellationToken);

        // Adres verilmediyse AWS'nin bölgedeki adresi kullanılır; istek JSON protokolüyle köke gider.
        var request = Assert.Single(sent);
        Assert.Equal((HttpMethod.Post, "https://sqs.eu-west-3.amazonaws.com/"), (request.Method, request.Address));
        Assert.Equal("AmazonSQS.SendMessage", request.Headers["X-Amz-Target"]);
        Assert.Equal("sqs.eu-west-3.amazonaws.com", request.Headers["Host"]);
        Assert.Equal("application/x-amz-json-1.0", request.ContentType);
        Assert.False(request.Headers.ContainsKey("x-amz-content-sha256"));

        var date = request.Headers["x-amz-date"];
        var authorization = request.Headers["Authorization"];
        Assert.StartsWith(
            $"AWS4-HMAC-SHA256 Credential={AccessKey}/{date[..8]}/eu-west-3/sqs/aws4_request,SignedHeaders=host;x-amz-date;x-amz-target,Signature=",
            authorization,
            StringComparison.Ordinal);
        Assert.DoesNotContain(SecretKey, request.Text, StringComparison.Ordinal);

        // İmza gönderilen gövdeye aittir: aynı istek aynı saatle yeniden imzalanınca aynı imza çıkar.
        using var again = new HttpRequestMessage(HttpMethod.Post, request.Address);
        again.Headers.Add("X-Amz-Target", "AmazonSQS.SendMessage");
        var now = DateTimeOffset.ParseExact(date, "yyyyMMdd'T'HHmmss'Z'", null, System.Globalization.DateTimeStyles.AssumeUniversal);
        S3Signer.Sign(again, S3Signer.HashOf(request.Body), AccessKey, SecretKey, "eu-west-3", now, "sqs");
        Assert.Equal(authorization, again.Headers.Authorization!.ToString());

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("https://sqs.eu-west-3.amazonaws.com/123456789012/orders.fifo", root.GetProperty("QueueUrl").GetString());
        Assert.Equal("ticket:0198c0de", root.GetProperty("MessageGroupId").GetString());
        Assert.Equal("ticket:0198c0de:41", root.GetProperty("MessageDeduplicationId").GetString());
        Assert.Equal(("Number", "41"), Attribute(root, "EventId"));
        Assert.Equal(("String", "ticket.created"), Attribute(root, "EventType"));
        Assert.Equal(("String", "ticket"), Attribute(root, "Resource"));

        // Gövde olayın kendisidir, webhook yayıncısının gönderdiği biçimde.
        Assert.Equal(
            JsonSerializer.Serialize(published, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            root.GetProperty("MessageBody").GetString());

        static (string?, string?) Attribute(JsonElement root, string name)
        {
            var attribute = root.GetProperty("MessageAttributes").GetProperty(name);
            return (attribute.GetProperty("DataType").GetString(), attribute.GetProperty("StringValue").GetString());
        }
    }

    [Fact]
    public async Task FifoIds_ThatSqsWouldReject_AreReplacedWithTheirHash()
    {
        var sent = new List<Sent>();
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> accept = async (request, _) =>
        {
            sent.Add(await Sent.OfAsync(request));
            return Json(HttpStatusCode.OK, """{"MessageId":"1"}""");
        };

        // Boşluk veya ASCII dışı karakter taşıyan ya da 128 karakteri aşan kimlik olduğu gibi gönderilemez.
        var fifo = CreatePublisher(options => options.QueueUrl = new Uri("http://localhost:9324/000000000000/orders.fifo"), accept);
        await fifo.PublishAsync(Event(7, "has space"), CancellationToken);
        await fifo.PublishAsync(Event(8, "çay"), CancellationToken);
        await fifo.PublishAsync(Event(9, new string('k', 130)), CancellationToken);

        Assert.All(sent, request =>
        {
            using var body = JsonDocument.Parse(request.Body);
            Assert.Matches("^[0-9a-f]{64}$", body.RootElement.GetProperty("MessageGroupId").GetString());
            Assert.Matches("^[0-9a-f]{64}$", body.RootElement.GetProperty("MessageDeduplicationId").GetString());
        });
        Assert.Equal(3, sent.Select(request => Encoding.UTF8.GetString(request.Body)).Distinct().Count());

        // Standart kuyruğa grup ve yineleme kimliği gönderilmez.
        sent.Clear();
        var standard = CreatePublisher(options => options.QueueUrl = new Uri("http://localhost:9324/000000000000/orders"), accept);
        await standard.PublishAsync(Event(7, "0198c0de"), CancellationToken);

        using var plain = JsonDocument.Parse(Assert.Single(sent).Body);
        Assert.False(plain.RootElement.TryGetProperty("MessageGroupId", out _));
        Assert.False(plain.RootElement.TryGetProperty("MessageDeduplicationId", out _));
    }

    [Fact]
    public async Task Failure_CarriesTheErrorCodeButNeitherTheResponseNorTheSecret()
    {
        var answer = Json(HttpStatusCode.OK, "{}");
        var publisher = CreatePublisher(Configure, (_, _) => Task.FromResult(answer));

        async Task<HaldusSqsException> FailAsync(HttpResponseMessage response)
        {
            answer = response;
            var failure = await Assert.ThrowsAsync<HaldusSqsException>(() => publisher.PublishAsync(Event(1, "a"), CancellationToken));

            Assert.StartsWith("SQS SendMessage failed for queue 'orders': ", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretKey, failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("123456789012", failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("leaky", failure.ToString(), StringComparison.Ordinal);
            return failure;
        }

        var missing = await FailAsync(Json(HttpStatusCode.BadRequest, """{"__type":"com.amazonaws.sqs#QueueDoesNotExist","message":"a leaky description"}"""));
        Assert.Equal((HttpStatusCode.BadRequest, "QueueDoesNotExist"), (missing.StatusCode, missing.ErrorCode));
        Assert.EndsWith(": 400 QueueDoesNotExist", missing.Message, StringComparison.Ordinal);

        var denied = await FailAsync(Json(HttpStatusCode.Forbidden, """{"__type":"InvalidClientTokenId","message":"a leaky description"}"""));
        Assert.Equal((HttpStatusCode.Forbidden, "InvalidClientTokenId"), (denied.StatusCode, denied.ErrorCode));

        // Kod biçiminde olmayan bir metin (hata metnine sızdırılmak istenen bir içerik) alınmaz.
        var hostile = await FailAsync(Json(HttpStatusCode.BadRequest, """{"__type":"x#a leaky\nwith a line break"}"""));
        Assert.Null(hostile.ErrorCode);
        Assert.EndsWith(": 400 BadRequest", hostile.Message, StringComparison.Ordinal);

        // Yanıt SQS'e ait değilse (araya giren bir vekil sunucunun sayfası) yalnızca durum kodu kalır.
        var gateway = await FailAsync(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>a leaky page</html>") });
        Assert.Equal(HttpStatusCode.BadGateway, gateway.StatusCode);
        Assert.Null(gateway.ErrorCode);

        var moved = await FailAsync(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.example/") } });
        Assert.Equal(HttpStatusCode.Found, moved.StatusCode);

        // Her isteğe 200 dönen bir adres olayı almış sayılmaz; yoksa olay kutudan silinir ve kaybolurdu.
        foreach (var body in new[] { "<html>a leaky page</html>", "", "{}", """{"MessageId":""}""", """["MessageId"]""" })
        {
            var accepted = await FailAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            Assert.EndsWith(": 200 OK (the response is not a SendMessage result)", accepted.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Service_ThatDoesNotAnswerInTime_IsATimeoutAndNotACancellation()
    {
        void Short(HaldusSqsOptions options)
        {
            Configure(options);
            options.Timeout = TimeSpan.FromMilliseconds(200);
        }

        // Yanıt hiç gelmez.
        var silent = CreatePublisher(Short, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Json(HttpStatusCode.OK, "{}");
        });

        // Başlıklar gelir ama gövde gelmez; süre gövdenin okunmasını da kapsar.
        var stalled = CreatePublisher(Short, (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }));

        foreach (var publisher in new[] { silent, stalled })
        {
            var failure = await Assert.ThrowsAsync<TimeoutException>(() => publisher.PublishAsync(Event(1, "a"), CancellationToken));
            Assert.Equal("The SQS queue 'orders' did not answer in time.", failure.Message);
        }

        // Çağıranın iptali ise iptal olarak kalır.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => silent.PublishAsync(Event(1, "a"), cancelled.Token));
    }

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

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }
    }

    private static void Configure(HaldusSqsOptions options)
    {
        options.QueueUrl ??= new Uri("https://sqs.us-east-1.amazonaws.com/123456789012/orders");
        options.AccessKey = AccessKey;
        options.SecretKey = SecretKey;
    }

    private IHaldusEventPublisher CreatePublisher(
        Action<HaldusSqsOptions> configure,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var services = new ServiceCollection();
        services.AddHaldusSqsPublisher(options =>
        {
            configure(options);
            Configure(options);
        });
        services.AddHttpClient(HaldusSqsPublisher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler(respond));

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IHaldusEventPublisher>();
    }

    private static HaldusEvent Event(long id, string recordId, string type = "ticket.created") => new(
        id,
        type,
        type[..type.IndexOf('.', StringComparison.Ordinal)],
        recordId,
        TenantId: null,
        UserId: "ada",
        JsonSerializer.SerializeToElement(new { subject = "Çay & <kahve>" }),
        new DateTimeOffset(2026, 11, 25, 9, 30, 0, TimeSpan.Zero));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/x-amz-json-1.0") };

    private sealed record Sent(HttpMethod Method, string Address, Dictionary<string, string> Headers, string? ContentType, byte[] Body, string Text)
    {
        public static async Task<Sent> OfAsync(HttpRequestMessage request) => new(
            request.Method,
            request.RequestUri!.AbsoluteUri,
            request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
            request.Content!.Headers.ContentType?.ToString(),
            await request.Content.ReadAsByteArrayAsync(CancellationToken),
            request.ToString());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    /// <summary>Hiç veri vermeyen, okuması ancak iptalle biten gövde.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
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
            services.AddHaldusSqsPublisher(queue.Configure);
            services.AddHttpClient(HaldusSqsPublisher.HttpClientName).AddHttpMessageHandler(() => sends);
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
    private sealed class SendCounter(string marker) : DelegatingHandler
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
/// SQS uyumlu bir sunucu (ElasticMQ). Kuyruklar testlerin içinden, imzalı isteklerle oluşturulur ve okunur. ElasticMQ
/// imzayı doğrulamaz: bu testler isteğin biçimini ve kuyruğun davranışını sınar, imzanın AWS'de kabul edileceğini değil.
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

    private readonly HttpClient _http = new();
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

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        _http.Dispose();
        await _container.DisposeAsync();
    }

    // Kuyruğu hazırlamak ve içine bakmak için, yayıncıdan bağımsız imzalı istekler.
    private async Task<JsonElement> CallAsync(string action, object input)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(input);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
        request.Headers.Add("X-Amz-Target", "AmazonSQS." + action);
        S3Signer.Sign(request, S3Signer.HashOf(body), AccessKey, SecretKey, Region, DateTimeOffset.UtcNow, "sqs");

        using var response = await _http.SendAsync(request, CancellationToken);
        var text = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode}: {text}");

        if (text.Length == 0)
        {
            return default;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
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

        public Task CreateAsync() => _owner.CallAsync(
            "CreateQueue",
            new
            {
                QueueName = Name,
                Attributes = Name.EndsWith(".fifo", StringComparison.Ordinal) ? new Dictionary<string, string> { ["FifoQueue"] = "true" } : [],
            });

        /// <summary>Kuyrukta bekleyen mesajları okur (ve kuyruktan siler); şimdiye kadar okunanların hepsini döndürür.</summary>
        public async Task<IReadOnlyList<QueueMessage>> ReceiveAsync()
        {
            while (true)
            {
                var received = await _owner.CallAsync(
                    "ReceiveMessage",
                    new
                    {
                        QueueUrl = Url.AbsoluteUri,
                        MaxNumberOfMessages = 10,
                        MessageAttributeNames = new[] { "All" },
                        MessageSystemAttributeNames = new[] { "All" },
                    });

                if (received.ValueKind != JsonValueKind.Object
                    || !received.TryGetProperty("Messages", out var messages)
                    || messages.GetArrayLength() == 0)
                {
                    return [.. _messages];
                }

                foreach (var message in messages.EnumerateArray())
                {
                    var body = message.GetProperty("Body").GetString()!;
                    using var parsed = JsonDocument.Parse(body);

                    _messages.Add(new QueueMessage(
                        body,
                        parsed.RootElement.Clone(),
                        message.TryGetProperty("MessageAttributes", out var attributes)
                            ? attributes.EnumerateObject().ToDictionary(
                                attribute => attribute.Name,
                                attribute => (attribute.Value.GetProperty("DataType").GetString()!, attribute.Value.GetProperty("StringValue").GetString()!))
                            : [],
                        message.GetProperty("Attributes").EnumerateObject().ToDictionary(attribute => attribute.Name, attribute => attribute.Value.GetString()!)));

                    await _owner.CallAsync("DeleteMessage", new { QueueUrl = Url.AbsoluteUri, ReceiptHandle = message.GetProperty("ReceiptHandle").GetString() });
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
