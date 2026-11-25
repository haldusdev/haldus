using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Haldus;

/// <summary>Olayların teslim edildiği bir adres.</summary>
public sealed class HaldusWebhookEndpoint
{
    public HaldusWebhookEndpoint(Uri url, string secret, params string[] eventTypes)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(eventTypes);

        Url = url;
        Secret = secret;
        EventTypes = eventTypes;
    }

    public Uri Url { get; }

    /// <summary>İmzanın anahtarı; alıcıyla paylaşılır ve en az 16 karakter olmalıdır.</summary>
    public string Secret { get; }

    /// <summary>
    /// Teslim edilecek olay türleri; boşsa hepsi. Tür tam adıyla (<c>order.created</c>) veya kaynağın bütün olayları
    /// için <c>order.*</c> biçiminde yazılır.
    /// </summary>
    public IReadOnlyList<string> EventTypes { get; }

    internal bool Accepts(string type)
    {
        if (EventTypes.Count == 0)
        {
            return true;
        }

        foreach (var pattern in EventTypes)
        {
            if (string.Equals(pattern, type, StringComparison.Ordinal)
                || (pattern.EndsWith(".*", StringComparison.Ordinal) && type.StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary><c>AddHaldusWebhookPublisher</c> ayarları.</summary>
public sealed class HaldusWebhookOptions
{
    public IList<HaldusWebhookEndpoint> Endpoints { get; } = [];

    /// <summary>Bir teslimatın en çok ne kadar süreceği.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <c>http://</c> adreslerine izin verir. Varsayılan olarak yalnızca <c>https://</c> kabul edilir: olayın verisi
    /// ağda açık gitmemelidir. Yerel denemeler içindir.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }
}

/// <summary>
/// Webhook imzası. İmzalanan metin <c>{kimlik}.{zaman}.{gövde}</c>, imza HMAC-SHA256'dır ve
/// <c>v1,{base64}</c> biçiminde yazılır (Standard Webhooks düzeni; anahtar <c>whsec_…</c> biçimindeyse o belirtimin
/// hazır doğrulayıcılarıyla da doğrulanır). Alıcı tarafında <see cref="Verify"/> ile doğrulanır.
/// </summary>
public static class HaldusWebhookSignature
{
    public const string IdHeader = "Webhook-Id";
    public const string TimestampHeader = "Webhook-Timestamp";
    public const string SignatureHeader = "Webhook-Signature";

    private const string Version = "v1,";

    public static string Compute(string secret, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentException.ThrowIfNullOrEmpty(id);

        var prefix = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{id}.{timestamp}."));
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));

        return Version + Convert.ToBase64String(HMACSHA256.HashData(KeyOf(secret), signed));
    }

    // Standard Webhooks anahtarı "whsec_" önekiyle ve base64 olarak yazar; öyle yazılmış bir anahtar çözülerek
    // kullanılır ki o belirtimin hazır doğrulayıcıları da imzayı kabul etsin. Başka her anahtar olduğu gibi kullanılır.
    private static byte[] KeyOf(string secret)
    {
        const string Prefix = "whsec_";

        if (secret.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var decoded = new byte[secret.Length];

            if (Convert.TryFromBase64String(secret[Prefix.Length..], decoded, out var written) && written > 0)
            {
                return decoded[..written];
            }
        }

        return Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>
    /// Gelen isteğin imzasını doğrular. Zaman damgası <paramref name="tolerance"/> kadar eskiyse veya ilerideyse
    /// istek reddedilir; böylece ele geçirilmiş bir istek sonradan yeniden gönderilemez. Karşılaştırma sabit sürelidir.
    /// </summary>
    public static bool Verify(
        string secret,
        string id,
        string timestamp,
        ReadOnlySpan<byte> body,
        string signature,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        if (string.IsNullOrEmpty(id)
            || string.IsNullOrEmpty(signature)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || Math.Abs(now.ToUnixTimeSeconds() - seconds) > tolerance.TotalSeconds)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(Compute(secret, id, seconds, body));

        // Başlık, anahtar değişimi sırasında boşlukla ayrılmış birden çok imza taşıyabilir.
        foreach (var candidate in signature.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(candidate)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Olayları yapılandırılmış adreslere imzalı HTTP isteğiyle teslim eden yayıncı. Adres 2xx dışında bir yanıt verirse
/// veya ulaşılamazsa hata fırlatır; olay kutuda kalır ve sonra yeniden denenir. Teslimat bu yüzden en az bir kezdir:
/// alıcı yinelenen olayı <c>Webhook-Id</c> başlığından (olayın kimliği) tanımalıdır. Birden çok adres varsa ve biri
/// başarısız olursa, yeniden denemede başarılı olanlara da yeniden gönderilir.
/// </summary>
public sealed class HaldusWebhookPublisher(IHttpClientFactory clients, IOptions<HaldusWebhookOptions> options, TimeProvider time)
    : IHaldusEventPublisher
{
    internal const string ClientName = "Haldus.Webhooks";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(HaldusEvent published, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);

        var body = JsonSerializer.SerializeToUtf8Bytes(published, Json);
        var id = published.Id.ToString(CultureInfo.InvariantCulture);

        foreach (var endpoint in options.Value.Endpoints)
        {
            if (!endpoint.Accepts(published.Type))
            {
                continue;
            }

            var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add(HaldusWebhookSignature.IdHeader, id);
            request.Headers.Add(HaldusWebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add(HaldusWebhookSignature.SignatureHeader, HaldusWebhookSignature.Compute(endpoint.Secret, id, timestamp, body));

            // Hata mesajı kutudaki hata alanına ve günlüğe yazılır. Adresin yolu, sorgusu ve kullanıcı bilgisi bir
            // anahtar taşıyabilir; yalnızca sunucunun adı yazılır.
            var target = endpoint.Url.IsDefaultPort ? endpoint.Url.Host : $"{endpoint.Url.Host}:{endpoint.Url.Port}";
            HttpResponseMessage response;

            try
            {
                // Yanıtın yalnızca durumu gerekir; gövdesi okunmaz (alıcı, kilit tutulurken büyük bir gövde akıtamaz).
                response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The webhook endpoint {target} did not answer in time.");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"The webhook endpoint {target} answered {(int)response.StatusCode}.");
                }
            }
        }
    }
}

public static class HaldusWebhookServiceCollectionExtensions
{
    /// <summary>
    /// Olayları (<c>.PublishesEvents()</c>) sayılan adreslere imzalı HTTP isteğiyle teslim eder. Adresler uygulamanın
    /// yapılandırmasından gelmelidir: kullanıcıların tanımladığı adreslere istek göndermek sunucuyu iç ağa istek
    /// atmaya zorlayabilir (SSRF) ve ayrıca korunması gerekir; bu yayıncı onu yapmaz.
    /// </summary>
    public static IServiceCollection AddHaldusWebhookPublisher(this IServiceCollection services, Action<HaldusWebhookOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<HaldusWebhookOptions>()
            .Configure(configure)
            .Validate(
                options => options.Endpoints.All(endpoint =>
                    endpoint.Url.IsAbsoluteUri
                    && (endpoint.Url.Scheme == Uri.UriSchemeHttps || (options.AllowInsecureHttp && endpoint.Url.Scheme == Uri.UriSchemeHttp))),
                "Webhook endpoints must be absolute https:// URLs (or http:// when AllowInsecureHttp is set).")
            .Validate(
                options => options.Endpoints.All(endpoint => endpoint.Secret.Length >= 16),
                "Webhook secrets must be at least 16 characters long.")
            .ValidateOnStart();

        // Yönlendirme izlenmez: imzalı gövde, adresin gösterdiği başka bir yere gitmemelidir.
        services.AddHttpClient(HaldusWebhookPublisher.ClientName)
            .ConfigureHttpClient((provider, client) => client.Timeout = provider.GetRequiredService<IOptions<HaldusWebhookOptions>>().Value.Timeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        services.AddSingleton<IHaldusEventPublisher, HaldusWebhookPublisher>();
        return services;
    }
}
