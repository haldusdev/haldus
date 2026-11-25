using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Haldus;

/// <summary><c>AddHaldusSqsPublisher</c> ayarları.</summary>
public sealed class HaldusSqsOptions
{
    /// <summary>
    /// Servisin adresi (<c>http://localhost:9324</c>); SQS uyumlu servisler ve öykünücüler içindir, yol içeremez.
    /// Boşsa AWS'nin <see cref="Region"/> bölgesindeki adresi kullanılır.
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>Bölge; kuyruğun bölgesi olmalıdır.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Olayların gönderildiği kuyruğun adresi (<c>https://sqs.eu-central-1.amazonaws.com/123456789012/orders.fifo</c>).
    /// Kuyruk var olmalıdır; Haldus kuyruk oluşturmaz. Adı <c>.fifo</c> ile biten kuyruk FIFO sayılır.
    /// </summary>
    public Uri? QueueUrl { get; set; }

    /// <summary>
    /// Erişim anahtarı. <see cref="SecretKey"/> ile birlikte verilir; ikisi de boşsa kimlik bilgisi AWS SDK'nın
    /// varsayılan zincirinden okunur (ortam değişkenleri, profil, kapsayıcı veya sunucu rolü).
    /// </summary>
    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    /// <summary>Geçici kimlik bilgisinin oturum token'ı; yalnızca <see cref="AccessKey"/> ile birlikte anlamlıdır.</summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// Gönderilecek olay türleri; boşsa hepsi. Tür tam adıyla (<c>order.created</c>) veya kaynağın bütün olayları
    /// için <c>order.*</c> biçiminde yazılır. Süzülen olay teslim edilmiş sayılır.
    /// </summary>
    public IList<string> EventTypes { get; } = [];

    /// <summary>Bir gönderimin, SDK'nın yeniden denemeleri dahil, en çok ne kadar süreceği.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// AWS SDK istemcisinin ayarlarını, Haldus kendi ayarlarını yazdıktan sonra değiştirir (vekil sunucu, yeniden
    /// deneme sayısı...).
    /// </summary>
    public Action<AmazonSQSConfig>? ConfigureClient { get; set; }
}

/// <summary>Servisin reddettiği bir gönderim. Metin servisin hata kodunu taşır; yanıtın gövdesini ve kimlik bilgisini taşımaz.</summary>
public sealed class HaldusSqsException : Exception
{
    internal HaldusSqsException(string queue, HttpStatusCode statusCode, string? errorCode, Exception? inner = null)
        : base($"SQS SendMessage failed for queue '{queue}': {(int)statusCode} {errorCode ?? statusCode.ToString()}", inner)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>Servisin hata kodu (<c>AWS.SimpleQueueService.NonExistentQueue</c>, <c>InvalidClientTokenId</c>...).</summary>
    public string? ErrorCode { get; }
}

/// <summary>
/// Olayları bir Amazon SQS kuyruğuna (veya SQS uyumlu bir servise) gönderen yayıncı; servisle AWS SDK
/// (<c>AWSSDK.SQS</c>) konuşur. Servis isteği kabul etmezse veya ulaşılamazsa hata fırlatır; olay kutuda kalır ve
/// sonra yeniden denenir. Teslimat bu yüzden en az bir kezdir: FIFO kuyruk yinelenen olayı kendisi ayıklar (beş dakika
/// içinde), standart kuyrukta tüketici <c>EventId</c> özniteliğinden tanımalıdır. Standart kuyruk sırayı da korumaz.
/// </summary>
public sealed class HaldusSqsPublisher : IHaldusEventPublisher, IDisposable
{
    private const string FifoSuffix = ".fifo";
    private const int MaxFifoIdLength = 128;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AmazonSQSClient _client;
    private readonly string _queueUrl;
    private readonly string _queue;
    private readonly bool _fifo;
    private readonly string[] _eventTypes;
    private readonly TimeSpan _timeout;

    // Ayarlar kaydedilirken değil yayıncı ilk kurulduğunda doğrulanır: build sırasındaki şema üretimi uygulamayı
    // kimlik bilgileri olmadan başlatır ve orada durmamalıdır. Olay servisi yayıncıları o sırada hiç kurmaz; gerçek
    // bir açılışta ise kurar ve yanlış ayar uygulamayı ilk olaydan önce durdurur.
    public HaldusSqsPublisher(IOptions<HaldusSqsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        Require(
            settings.Region is { Length: > 0 } && settings.Region.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'),
            "Region is required and is made of letters, digits and '-'.");
        Require(
            string.IsNullOrEmpty(settings.AccessKey) == string.IsNullOrEmpty(settings.SecretKey),
            "AccessKey and SecretKey are given together, or both are left empty to use the default AWS credentials.");
        Require(settings.Timeout > TimeSpan.Zero && settings.Timeout <= TimeSpan.FromHours(1), "Timeout must be positive and at most one hour.");
        Require(settings.EventTypes.All(type => !string.IsNullOrWhiteSpace(type)), "EventTypes cannot contain an empty entry.");

        var service = settings.ServiceUrl;
        Require(
            service is null
                || (service.IsAbsoluteUri && service.Scheme is "http" or "https" && service.AbsolutePath == "/" && service.Query.Length == 0 && service.UserInfo.Length == 0),
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

        var config = new AmazonSQSConfig { Timeout = settings.Timeout };

        if (service is null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }
        else
        {
            config.ServiceURL = service.GetLeftPart(UriPartial.Authority);
            config.AuthenticationRegion = settings.Region;
        }

        settings.ConfigureClient?.Invoke(config);

        _client = string.IsNullOrEmpty(settings.AccessKey)
            ? new AmazonSQSClient(config)
            : new AmazonSQSClient(
                string.IsNullOrEmpty(settings.SessionToken)
                    ? new BasicAWSCredentials(settings.AccessKey, settings.SecretKey)
                    : new SessionAWSCredentials(settings.AccessKey, settings.SecretKey, settings.SessionToken),
                config);

        _queueUrl = queue.AbsoluteUri;
        _queue = name;
        _fifo = name.Length != stem.Length;
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

        var id = published.Id.ToString(CultureInfo.InvariantCulture);
        var request = new SendMessageRequest
        {
            QueueUrl = _queueUrl,

            // Gövde webhook yayıncısının gönderdiğiyle aynı JSON'dur.
            MessageBody = JsonSerializer.Serialize(published, Json),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EventId"] = new() { DataType = "Number", StringValue = id },
                ["EventType"] = new() { DataType = "String", StringValue = published.Type },
                ["Resource"] = new() { DataType = "String", StringValue = published.Resource },
            },
        };

        if (_fifo)
        {
            // Grup kayıttır: bir kaydın olayları sırayla tüketilir, başka kayıtların olayları onu beklemez.
            var record = $"{published.Resource}:{published.RecordId}";
            request.MessageGroupId = FifoId(record);

            // Olayın kimliği yalnızca kendi veritabanında benzersizdir; ayrı bağlamı olan iki modül aynı kuyruğa
            // yazarsa aynı numaralı iki ayrı olaydan biri yinelenmiş sayılıp düşerdi. Kayıt bu yüzden kimliğe katılır.
            request.MessageDeduplicationId = FifoId($"{record}:{id}");
        }

        // Süre SDK'nın yeniden denemelerini de kapsar; dağıtıcı bu sırada sırayı ve kilidini tutar.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            var response = await _client.SendMessageAsync(request, timeout.Token);

            // Başarı yalnızca beklenen yanıtla sayılır: her isteğe 200 dönen bir adres (yanlış ServiceUrl, araya giren
            // bir vekil sunucu) olayı almış sayılırsa olay kutudan silinir ve kaybolur.
            if (string.IsNullOrEmpty(response.MessageId))
            {
                throw new HaldusSqsException(_queue, response.HttpStatusCode, "NoMessageId");
            }
        }
        catch (AmazonSQSException exception)
        {
            // Servisin açıklaması kutuya ve günlüğe yazılan metne alınmaz; yalnızca kodu alınır.
            throw new HaldusSqsException(_queue, exception.StatusCode, exception.ErrorCode, exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Zaman aşımı bir iptal istisnasıdır; dağıtıcı onu sürecin kapanması sanmamalıdır.
            throw new TimeoutException($"The SQS queue '{_queue}' did not answer in time.");
        }
    }

    public void Dispose() => _client.Dispose();

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

    // SQS bu kimliklerde en çok 128 karakter ve yalnızca ASCII harf, rakam ve noktalama kabul eder; sığmayan
    // (ör. boşluk taşıyan bir doğal anahtar) özetiyle yazılır.
    private static string FifoId(string value) =>
        value.Length <= MaxFifoIdLength && value.All(character => character is > ' ' and <= '~')
            ? value
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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
    /// Servisle AWS SDK konuşur; istemcinin ayarları <see cref="HaldusSqsOptions.ConfigureClient"/> ile
    /// değiştirilebilir. Bir uygulamada tek bir kuyruk kaydedilir.
    /// </summary>
    public static IServiceCollection AddHaldusSqsPublisher(this IServiceCollection services, Action<HaldusSqsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<HaldusSqsOptions>().Configure(configure);
        services.AddSingleton<IHaldusEventPublisher, HaldusSqsPublisher>();
        return services;
    }
}
