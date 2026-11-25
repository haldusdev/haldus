using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Haldus;

/// <summary><c>AddHaldusSqsPublisher</c> ayarları.</summary>
public sealed class HaldusSqsOptions
{
    /// <summary>
    /// Servisin adresi (<c>http://localhost:9324</c>); yol içeremez. Boşsa AWS'nin <see cref="Region"/> bölgesindeki
    /// adresi (<c>https://sqs.{Region}.amazonaws.com</c>) kullanılır.
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>İmzaya giren bölge; kuyruğun bölgesi olmalıdır.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Olayların gönderildiği kuyruğun adresi (<c>https://sqs.eu-central-1.amazonaws.com/123456789012/orders.fifo</c>).
    /// Kuyruk var olmalıdır; Haldus kuyruk oluşturmaz. Adı <c>.fifo</c> ile biten kuyruk FIFO sayılır.
    /// </summary>
    public Uri? QueueUrl { get; set; }

    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    /// <summary>
    /// Gönderilecek olay türleri; boşsa hepsi. Tür tam adıyla (<c>order.created</c>) veya kaynağın bütün olayları
    /// için <c>order.*</c> biçiminde yazılır. Süzülen olay teslim edilmiş sayılır.
    /// </summary>
    public IList<string> EventTypes { get; } = [];

    /// <summary>Bir gönderimin, yanıtı okunana kadar en çok ne kadar süreceği.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Servisin reddettiği bir gönderim. Metin servisin hata kodunu taşır; yanıtın gövdesini ve kimlik bilgisini taşımaz.</summary>
public sealed class HaldusSqsException : Exception
{
    internal HaldusSqsException(string queue, HttpStatusCode statusCode, string? errorCode, string? detail = null)
        : base($"SQS SendMessage failed for queue '{queue}': {(int)statusCode} {errorCode ?? statusCode.ToString()}{(detail is null ? string.Empty : $" ({detail})")}")
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>Servisin hata kodu (<c>QueueDoesNotExist</c>, <c>InvalidClientTokenId</c>...); yanıt bir SQS hatası değilse boştur.</summary>
    public string? ErrorCode { get; }
}

/// <summary>
/// Olayları bir Amazon SQS kuyruğuna (veya SQS uyumlu bir servise) gönderen yayıncı. İstekler <see cref="HttpClient"/>
/// ile gönderilir ve AWS Signature Version 4 ile imzalanır. Servis isteği kabul etmezse veya ulaşılamazsa hata
/// fırlatır; olay kutuda kalır ve sonra yeniden denenir. Teslimat bu yüzden en az bir kezdir: FIFO kuyruk yinelenen
/// olayı kendisi ayıklar (beş dakika içinde), standart kuyrukta tüketici <c>EventId</c> özniteliğinden tanımalıdır.
/// Standart kuyruk sırayı da korumaz.
/// </summary>
public sealed class HaldusSqsPublisher : IHaldusEventPublisher
{
    public const string HttpClientName = "Haldus.Events.Sqs";

    private const string Service = "sqs";
    private const string FifoSuffix = ".fifo";
    private const int MaxFifoIdLength = 128;
    private const int MaxResponseBytes = 8 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _clients;
    private readonly Uri _service;
    private readonly string _queueUrl;
    private readonly string _queue;
    private readonly bool _fifo;
    private readonly string _region;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string[] _eventTypes;
    private readonly TimeSpan _timeout;

    // Ayarlar kaydedilirken değil yayıncı ilk kurulduğunda doğrulanır: build sırasındaki şema üretimi uygulamayı
    // kimlik bilgileri olmadan başlatır ve orada durmamalıdır. Olay servisi yayıncıları o sırada hiç kurmaz; gerçek
    // bir açılışta ise kurar ve yanlış ayar uygulamayı ilk olaydan önce durdurur.
    public HaldusSqsPublisher(IHttpClientFactory clients, IOptions<HaldusSqsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        Require(
            settings.Region.Length > 0 && settings.Region.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'),
            "Region is required and is made of letters, digits and '-'.");
        Require(!string.IsNullOrEmpty(settings.AccessKey) && !string.IsNullOrEmpty(settings.SecretKey), "AccessKey and SecretKey are required.");
        Require(settings.Timeout > TimeSpan.Zero && settings.Timeout <= TimeSpan.FromHours(1), "Timeout must be positive and at most one hour.");
        Require(settings.EventTypes.All(type => !string.IsNullOrWhiteSpace(type)), "EventTypes cannot contain an empty entry.");

        var service = settings.ServiceUrl ?? new Uri($"https://sqs.{settings.Region}.amazonaws.com");
        Require(
            service.IsAbsoluteUri && service.Scheme is "http" or "https" && service.AbsolutePath == "/" && service.Query.Length == 0 && service.UserInfo.Length == 0,
            "ServiceUrl must be an absolute http or https address without a path, query or user info.");

        var queue = settings.QueueUrl;
        Require(
            queue is not null && queue.IsAbsoluteUri && queue.Scheme is "http" or "https" && queue.Query.Length == 0 && queue.Fragment.Length == 0 && queue.UserInfo.Length == 0,
            "QueueUrl must be an absolute http or https address without a query, fragment or user info.");

        // Kuyruğun adı hata metinlerine yazılır; adresin geri kalanı (hesap numarası) yazılmaz.
        var name = queue!.AbsolutePath.Split('/')[^1];
        var stem = name.EndsWith(FifoSuffix, StringComparison.Ordinal) ? name[..^FifoSuffix.Length] : name;
        Require(
            name.Length <= 80 && stem.Length > 0 && stem.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'),
            "QueueUrl must end with a queue name of up to 80 letters, digits, '-' and '_' (and '.fifo' for a FIFO queue).");

        _clients = clients;
        _service = service;
        _queueUrl = queue.AbsoluteUri;
        _queue = name;
        _fifo = name.Length != stem.Length;
        _region = settings.Region;
        _accessKey = settings.AccessKey;
        _secretKey = settings.SecretKey;
        _eventTypes = [.. settings.EventTypes];
        _timeout = settings.Timeout;
    }

    public async Task PublishAsync(HaldusEvent published, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);

        if (!Accepts(published.Type))
        {
            return;
        }

        var payload = Message(published);
        using var request = new HttpRequestMessage(HttpMethod.Post, _service) { Content = new ByteArrayContent(payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", "AmazonSQS.SendMessage");

        // İmzanın saati gerçek saat olmalıdır (servis birkaç dakikadan fazla sapmayı reddeder); bu yüzden
        // uygulamanın, testlerde değiştirilebilen TimeProvider servisi kullanılmaz.
        S3Signer.Sign(request, S3Signer.HashOf(payload), _accessKey, _secretKey, _region, DateTimeOffset.UtcNow, Service);

        // Süre yanıtın gövdesini okumayı da kapsar; HttpClient'ın kendi süresi başlıklar gelince biter.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            using var response = await _clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new HaldusSqsException(_queue, response.StatusCode, ErrorCode(await ReadAsync(response, "__type", timeout.Token)));
            }

            // Başarı yalnızca beklenen yanıtla sayılır: her isteğe 200 dönen bir adres (yanlış ServiceUrl, araya giren
            // bir vekil sunucu) olayı almış sayılırsa olay kutudan silinir ve kaybolur.
            if (string.IsNullOrEmpty(await ReadAsync(response, "MessageId", timeout.Token)))
            {
                throw new HaldusSqsException(_queue, response.StatusCode, errorCode: null, "the response is not a SendMessage result");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Zaman aşımı bir iptal istisnasıdır; dağıtıcı onu sürecin kapanması sanmamalıdır.
            throw new TimeoutException($"The SQS queue '{_queue}' did not answer in time.");
        }
    }

    private bool Accepts(string type)
    {
        if (_eventTypes.Length == 0)
        {
            return true;
        }

        foreach (var pattern in _eventTypes)
        {
            if (string.Equals(pattern, type, StringComparison.Ordinal)
                || (pattern.EndsWith(".*", StringComparison.Ordinal) && type.StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private byte[] Message(HaldusEvent published)
    {
        var id = published.Id.ToString(CultureInfo.InvariantCulture);

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("QueueUrl", _queueUrl);

            // Gövde webhook yayıncısının gönderdiğiyle aynı JSON'dur.
            writer.WriteString("MessageBody", JsonSerializer.SerializeToUtf8Bytes(published, Json));

            writer.WriteStartObject("MessageAttributes");
            Attribute(writer, "EventId", "Number", id);
            Attribute(writer, "EventType", "String", published.Type);
            Attribute(writer, "Resource", "String", published.Resource);
            writer.WriteEndObject();

            if (_fifo)
            {
                // Grup kayıttır: bir kaydın olayları sırayla tüketilir, başka kayıtların olayları onu beklemez.
                var record = $"{published.Resource}:{published.RecordId}";
                writer.WriteString("MessageGroupId", FifoId(record));

                // Olayın kimliği yalnızca kendi veritabanında benzersizdir; ayrı bağlamı olan iki modül aynı kuyruğa
                // yazarsa aynı numaralı iki ayrı olaydan biri yinelenmiş sayılıp düşerdi. Kayıt bu yüzden kimliğe katılır.
                writer.WriteString("MessageDeduplicationId", FifoId($"{record}:{id}"));
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void Attribute(Utf8JsonWriter writer, string name, string type, string value)
    {
        writer.WriteStartObject(name);
        writer.WriteString("DataType", type);
        writer.WriteString("StringValue", value);
        writer.WriteEndObject();
    }

    // SQS bu kimliklerde en çok 128 karakter ve yalnızca ASCII harf, rakam ve noktalama kabul eder; sığmayan
    // (ör. boşluk taşıyan bir doğal anahtar) özetiyle yazılır.
    private static string FifoId(string value) =>
        value.Length <= MaxFifoIdLength && value.All(character => character is > ' ' and <= '~')
            ? value
            : S3Signer.HashOf(Encoding.UTF8.GetBytes(value));

    // Yanıt beklenen JSON olmayabilir (araya giren bir vekil sunucunun sayfası); o zaman yalnızca durum kodu kalır.
    private static async Task<string?> ReadAsync(HttpResponseMessage response, string property, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxResponseBytes];

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, read));

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Kod "com.amazonaws.sqs#QueueDoesNotExist" biçiminde gelir. Hata metni kutuya ve günlüğe yazıldığı için
    // yanıttan yalnızca kod biçimindeki kısa bir metin alınır; servisin açıklaması alınmaz.
    private static string? ErrorCode(string? type)
    {
        if (type is null)
        {
            return null;
        }

        var code = type[(type.LastIndexOf('#') + 1)..];

        return code.Length is > 0 and <= 100 && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? code
            : null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("The SQS publisher is not configured correctly: " + message);
        }
    }
}

public static class HaldusSqsServiceCollectionExtensions
{
    /// <summary>
    /// Olayları (<c>.PublishesEvents()</c>) bir SQS kuyruğuna gönderir. Kuyruk önceden oluşturulmuş olmalıdır.
    /// İstekler <see cref="HaldusSqsPublisher.HttpClientName"/> adlı istemciyle gönderilir; uygulama onu
    /// <c>AddHttpClient</c> ile ayrıca yapılandırabilir (vekil sunucu). Bir uygulamada tek bir kuyruk kaydedilir.
    /// </summary>
    public static IServiceCollection AddHaldusSqsPublisher(this IServiceCollection services, Action<HaldusSqsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<HaldusSqsOptions>().Configure(configure);

        // Yönlendirme izlenmez: imza ilk adrese aittir ve olayın verisi adresin gösterdiği başka bir yere gitmemelidir.
        // Süreyi yayıncı kendisi ölçer (yanıtın gövdesi dahil); istemcinin kendi süresi onu kısaltmamalıdır.
        services.AddHttpClient(HaldusSqsPublisher.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        services.AddSingleton<IHaldusEventPublisher, HaldusSqsPublisher>();
        return services;
    }
}
