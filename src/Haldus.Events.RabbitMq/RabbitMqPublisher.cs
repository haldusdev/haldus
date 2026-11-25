using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Haldus;

/// <summary><c>AddHaldusRabbitMqPublisher</c> ayarları.</summary>
public sealed class HaldusRabbitMqOptions
{
    public string HostName { get; set; } = "localhost";

    /// <summary>Boşsa protokolün varsayılanı kullanılır (5672; TLS ile 5671).</summary>
    public int? Port { get; set; }

    public string VirtualHost { get; set; } = "/";

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    /// <summary>Bağlantı TLS ile kurulur; sunucunun sertifikası <see cref="HostName"/> adına doğrulanır.</summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// Olayların gönderildiği exchange. Var olmalıdır; Haldus exchange, kuyruk ve bağ oluşturmaz. Boş bırakılırsa
    /// varsayılan exchange kullanılır ve <see cref="RoutingKey"/> hedef kuyruğun adı olmalıdır.
    /// </summary>
    public string Exchange { get; set; } = "";

    /// <summary>
    /// Yönlendirme anahtarı. Boşsa olayın türü kullanılır (<c>order.created</c>); bir topic exchange'de tüketiciler
    /// böylece türe göre bağlanır (<c>order.*</c>).
    /// </summary>
    public string? RoutingKey { get; set; }

    /// <summary>
    /// Gönderilecek olay türleri; boşsa hepsi. Tür tam adıyla (<c>order.created</c>) veya kaynağın bütün olayları
    /// için <c>order.*</c> biçiminde yazılır. Süzülen olay teslim edilmiş sayılır.
    /// </summary>
    public IList<string> EventTypes { get; } = [];

    /// <summary>Bağlantının kurulması dahil, bir gönderimin aracı onaylayana kadar en çok ne kadar süreceği.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Bağlantı ayarlarını, Haldus kendi ayarlarını yazdıktan sonra değiştirir (sertifika, kalp atışı...).</summary>
    public Action<ConnectionFactory>? ConfigureConnection { get; set; }
}

/// <summary>
/// Olayları bir RabbitMQ exchange'ine gönderen yayıncı; aracıyla resmi istemci (<c>RabbitMQ.Client</c>) konuşur.
/// Mesaj kalıcıdır ve aracı onu onaylayana kadar (publisher confirm) teslim edilmiş sayılmaz; hiçbir kuyruğa
/// yönlendirilemeyen mesaj da teslim edilmemiş sayılır (<c>mandatory</c>). Aracı reddederse veya ulaşılamazsa hata
/// fırlatır; olay kutuda kalır ve sonra yeniden denenir. Teslimat bu yüzden en az bir kezdir: tüketici yineleneni
/// mesajın kimliğinden (<c>message-id</c>) tanımalıdır.
/// </summary>
public sealed class HaldusRabbitMqPublisher : IHaldusEventPublisher, IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _exchange;
    private readonly string? _routingKey;
    private readonly string[] _eventTypes;
    private readonly TimeSpan _timeout;

    private IConnection? _connection;
    private IChannel? _channel;
    private int _disposed;

    // Ayarlar yayıncı kurulurken doğrulanır; bağlantı ilk olayda kurulur. Build sırasındaki şema üretimi
    // yayıncıları hiç kurmaz, gerçek bir açılışta ise yanlış ayar uygulamayı ilk olaydan önce durdurur.
    public HaldusRabbitMqPublisher(IOptions<HaldusRabbitMqOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        Require(!string.IsNullOrWhiteSpace(settings.HostName), "HostName is required.");
        Require(settings.Port is null or (> 0 and <= 65535), "Port must be between 1 and 65535.");
        Require(!string.IsNullOrEmpty(settings.VirtualHost), "VirtualHost is required.");
        Require(settings.Timeout > TimeSpan.Zero && settings.Timeout <= TimeSpan.FromHours(1), "Timeout must be positive and at most one hour.");
        Require(settings.EventTypes.All(type => !string.IsNullOrWhiteSpace(type)), "EventTypes cannot contain an empty entry.");
        Require(
            settings.Exchange.Length > 0 || !string.IsNullOrEmpty(settings.RoutingKey),
            "RoutingKey must name the target queue when Exchange is empty (the default exchange).");

        _factory = new ConnectionFactory
        {
            HostName = settings.HostName,
            Port = settings.Port ?? (settings.UseTls ? AmqpTcpEndpoint.DefaultAmqpSslPort : AmqpTcpEndpoint.UseDefaultPort),
            VirtualHost = settings.VirtualHost,
            UserName = settings.UserName,
            Password = settings.Password,
            ClientProvidedName = "haldus-events",

            // Kopan bağlantı bir sonraki olayda yeniden kurulur; arada gönderilemeyen olay kutuda bekler.
            AutomaticRecoveryEnabled = false,
        };

        if (settings.UseTls)
        {
            _factory.Ssl = new SslOption { Enabled = true, ServerName = settings.HostName };
        }

        settings.ConfigureConnection?.Invoke(_factory);

        _exchange = settings.Exchange;
        _routingKey = string.IsNullOrEmpty(settings.RoutingKey) ? null : settings.RoutingKey;
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

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = published.Id.ToString(CultureInfo.InvariantCulture),
            Type = published.Type,
            Timestamp = new AmqpTimestamp(published.OccurredAt.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                ["resource"] = published.Resource,
                ["record-id"] = published.RecordId,
            },
        };

        // Gövde webhook ve SQS yayıncılarının gönderdiğiyle aynı JSON'dur.
        var body = JsonSerializer.SerializeToUtf8Bytes(published, Json);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            await _gate.WaitAsync(timeout.Token);

            try
            {
                var channel = await ChannelAsync(timeout.Token);

                // Onay izleme açıktır: çağrı aracının onayını bekler; ret (nack) ve yönlendirilemeyen mesaj
                // (return) hata olarak fırlatılır.
                await channel.BasicPublishAsync(_exchange, _routingKey ?? published.Type, mandatory: true, properties, body, timeout.Token);
            }
            catch
            {
                // Hatadan sonra kanalın durumu bilinmez (aracı kanalı kapatmış olabilir); bir sonraki olay yeni bir
                // bağlantıyla başlar.
                await CloseAsync();
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Zaman aşımı bir iptal istisnasıdır; dağıtıcı onu sürecin kapanması sanmamalıdır.
            throw new TimeoutException($"The RabbitMQ broker at '{_factory.HostName}' did not confirm the message in time.");
        }
    }

    // Servis sağlayıcı eşzamanlı da atılabilir (testler, konsol uygulamaları); istemcinin kapatması ise eşzamansızdır.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _gate.WaitAsync();

        try
        {
            await CloseAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await CloseAsync();

        _connection = await _factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        return _channel;
    }

    private async Task CloseAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;

        // Kapatma sırasındaki hata asıl hatayı örtmemelidir; bağlantı zaten kopmuş olabilir.
        try
        {
            if (channel is not null)
            {
                await channel.DisposeAsync();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }

        try
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("The RabbitMQ publisher is not configured correctly: " + message);
        }
    }
}

public static class HaldusRabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// Olayları (<c>.PublishesEvents()</c>) bir RabbitMQ exchange'ine gönderir. Exchange, kuyruklar ve bağları önceden
    /// oluşturulmuş olmalıdır. Bir uygulamada tek bir hedef kaydedilir.
    /// </summary>
    public static IServiceCollection AddHaldusRabbitMqPublisher(this IServiceCollection services, Action<HaldusRabbitMqOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<HaldusRabbitMqOptions>().Configure(configure);
        services.AddSingleton<IHaldusEventPublisher, HaldusRabbitMqPublisher>();
        return services;
    }
}
