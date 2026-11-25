using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Haldus;

/// <summary><c>AddHaldusKafkaPublisher</c> ayarları.</summary>
public sealed class HaldusKafkaOptions
{
    /// <summary>Kümeye ilk bağlanılan adresler (<c>localhost:9092</c>; birden çoksa virgülle ayrılır).</summary>
    public string BootstrapServers { get; set; } = "";

    /// <summary>Olayların yazıldığı konu. Var olmalıdır; Haldus konu oluşturmaz.</summary>
    public string Topic { get; set; } = "";

    public string ClientId { get; set; } = "haldus-events";

    /// <summary>
    /// Gönderilecek olay türleri; boşsa hepsi. Tür tam adıyla (<c>order.created</c>) veya kaynağın bütün olayları
    /// için <c>order.*</c> biçiminde yazılır. Süzülen olay teslim edilmiş sayılır.
    /// </summary>
    public IList<string> EventTypes { get; } = [];

    /// <summary>Bir olayın, küme onu onaylayana kadar en çok ne kadar bekleneceği (istemcinin yeniden denemeleri dahil).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// İstemcinin ayarlarını, Haldus kendi ayarlarını yazdıktan sonra değiştirir. Güvenlik ayarları buradan verilir
    /// (<c>SecurityProtocol</c>, <c>SaslMechanism</c>, <c>SaslUsername</c>, <c>SaslPassword</c>, <c>SslCaLocation</c>).
    /// </summary>
    public Action<ProducerConfig>? ConfigureProducer { get; set; }
}

/// <summary>
/// Olayları bir Kafka konusuna yazan yayıncı; kümeyle Confluent'in istemcisi (<c>Confluent.Kafka</c>) konuşur. Mesajın
/// anahtarı kayıttır (<c>{kaynak}:{kayıt}</c>): bir kaydın olayları aynı bölüme düşer ve sırayla tüketilir. Mesaj,
/// eşlenik kopyaların hepsi onu yazana kadar (<c>acks=all</c>) teslim edilmiş sayılmaz. Küme reddederse veya
/// ulaşılamazsa hata fırlatır; olay kutuda kalır ve sonra yeniden denenir. Teslimat bu yüzden en az bir kezdir:
/// tüketici yineleneni <c>event-id</c> başlığından tanımalıdır.
/// </summary>
public sealed partial class HaldusKafkaPublisher : IHaldusEventPublisher, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lazy<IProducer<string, byte[]>> _producer;
    private readonly string _topic;
    private readonly string[] _eventTypes;
    private readonly TimeSpan _timeout;

    // Ayarlar yayıncı kurulurken doğrulanır; istemci ilk olayda kurulur. Build sırasındaki şema üretimi yayıncıları
    // hiç kurmaz, gerçek bir açılışta ise yanlış ayar uygulamayı ilk olaydan önce durdurur.
    public HaldusKafkaPublisher(IOptions<HaldusKafkaOptions> options, ILogger<HaldusKafkaPublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        ILogger log = logger ?? NullLogger<HaldusKafkaPublisher>.Instance;

        var settings = options.Value;
        Require(!string.IsNullOrWhiteSpace(settings.BootstrapServers), "BootstrapServers is required.");
        Require(
            settings.Topic.Length is > 0 and <= 249 && settings.Topic.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'),
            "Topic is required and is made of up to 249 letters, digits, '.', '_' and '-'.");
        Require(settings.Timeout >= TimeSpan.FromSeconds(1) && settings.Timeout <= TimeSpan.FromHours(1), "Timeout must be between one second and one hour.");
        Require(settings.EventTypes.All(type => !string.IsNullOrWhiteSpace(type)), "EventTypes cannot contain an empty entry.");

        var config = new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            ClientId = settings.ClientId,
            Acks = Acks.All,

            // Yeniden denenen bir gönderim bölüme iki kez yazılmaz ve sıra bozulmaz.
            EnableIdempotence = true,

            // Olaylar tek tek ve sırayla beklenir; biriktirip toplu göndermek yalnızca gecikme eklerdi.
            LingerMs = 0,
            MessageTimeoutMs = (int)settings.Timeout.TotalMilliseconds,

            // Java istemcisinin varsayılanıyla aynı bölümleme: aynı konuya yazan başka uygulamalarla aynı anahtar aynı bölüme düşer.
            Partitioner = Partitioner.Murmur2Random,
        };

        settings.ConfigureProducer?.Invoke(config);

        // İstemci kendi günlüğünü varsayılan olarak hata akışına yazar; uygulamanın günlüğüne yönlendirilir.
        _producer = new Lazy<IProducer<string, byte[]>>(() => new ProducerBuilder<string, byte[]>(config)
            .SetLogHandler((_, message) => LogClient(log, Level(message.Level), message.Facility, message.Message))
            .Build());
        _topic = settings.Topic;
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

        var message = new Message<string, byte[]>
        {
            Key = $"{published.Resource}:{published.RecordId}",

            // Gövde webhook ve SQS yayıncılarının gönderdiğiyle aynı JSON'dur.
            Value = JsonSerializer.SerializeToUtf8Bytes(published, Json),
            Timestamp = new Timestamp(published.OccurredAt),
            Headers =
            [
                new Header("event-id", Encoding.UTF8.GetBytes(published.Id.ToString(CultureInfo.InvariantCulture))),
                new Header("event-type", Encoding.UTF8.GetBytes(published.Type)),
                new Header("resource", Encoding.UTF8.GetBytes(published.Resource)),
            ],
        };

        // İstemci mesajı kendi süresi (MessageTimeoutMs) dolunca hatayla bırakır; buradaki sınır onun biraz ötesindedir
        // ve yalnızca yanıt hiç gelmezse devreye girer.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout + TimeSpan.FromSeconds(5));

        try
        {
            var result = await _producer.Value.ProduceAsync(_topic, message, timeout.Token);

            if (result.Status != PersistenceStatus.Persisted)
            {
                throw new InvalidOperationException($"The Kafka topic '{_topic}' did not persist the message ({result.Status}).");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Zaman aşımı bir iptal istisnasıdır; dağıtıcı onu sürecin kapanması sanmamalıdır.
            throw new TimeoutException($"The Kafka topic '{_topic}' did not acknowledge the message in time.");
        }
    }

    public void Dispose()
    {
        if (_producer.IsValueCreated)
        {
            // Yoldaki mesajlara kısa bir süre tanınır; gönderilemeyen olay zaten kutudadır.
            _producer.Value.Flush(TimeSpan.FromSeconds(5));
            _producer.Value.Dispose();
        }
    }

    // librdkafka syslog düzeylerini kullanır: 0-3 hata, 4 uyarı, 5-6 bilgi, 7 ayrıntı.
    private static LogLevel Level(SyslogLevel level) => level switch
    {
        <= SyslogLevel.Error => LogLevel.Error,
        SyslogLevel.Warning => LogLevel.Warning,
        SyslogLevel.Notice or SyslogLevel.Info => LogLevel.Information,
        _ => LogLevel.Debug,
    };

    [LoggerMessage(Message = "Kafka client {Facility}: {Text}")]
    private static partial void LogClient(ILogger logger, LogLevel level, string facility, string text);

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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("The Kafka publisher is not configured correctly: " + message);
        }
    }
}

public static class HaldusKafkaServiceCollectionExtensions
{
    /// <summary>
    /// Olayları (<c>.PublishesEvents()</c>) bir Kafka konusuna yazar. Konu önceden oluşturulmuş olmalıdır. Bir
    /// uygulamada tek bir konu kaydedilir.
    /// </summary>
    public static IServiceCollection AddHaldusKafkaPublisher(this IServiceCollection services, Action<HaldusKafkaOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<HaldusKafkaOptions>().Configure(configure);
        services.AddSingleton<IHaldusEventPublisher, HaldusKafkaPublisher>();
        return services;
    }
}
