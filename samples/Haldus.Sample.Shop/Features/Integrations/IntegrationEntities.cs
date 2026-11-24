using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

namespace Haldus.Sample.Shop.Features.Integrations;

/// <summary>İstemci uygulamaların kullandığı API anahtarı. Sırrı yalnızca oluşturulurken ve döndürülürken görünür.</summary>
public partial class ApiKey
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";
}

/// <summary>
/// Olayların gönderildiği adres. Gönderiler sırla imzalanır; bu yüzden sır, özetinin yanında şifrelenmiş olarak da
/// saklanır ve sunucu onu açabilir.
/// </summary>
public partial class WebhookEndpoint
{
    [Required]
    [Url]
    [MaxLength(500)]
    public string Url { get; set; } = "";
}

/// <summary><c>POST /webhook-endpoints/{id}/sign-payload</c>: bir gönderiyi uç noktanın sırrıyla imzalar.</summary>
public sealed record SignPayload
{
    [Required]
    [MaxLength(10_000)]
    public string Payload { get; init; } = "";
}

public sealed record PayloadSignature(string Signature);

/// <summary>Sırrı açar ve gönderinin HMAC-SHA-256 imzasını hesaplar; sırrın kendisi hiçbir yanıtta yer almaz.</summary>
public sealed class SignPayloadHandler(IServiceProvider services)
    : IEntityActionHandler<WebhookEndpoint, SignPayload, PayloadSignature>
{
    public Task<PayloadSignature> HandleAsync(WebhookEndpoint entity, SignPayload request, CancellationToken cancellationToken)
    {
        var secret = HaldusSecretProtection.Reveal(services, entity);
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.Payload));

        return Task.FromResult(new PayloadSignature(Convert.ToHexStringLower(signature)));
    }
}

public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
}

/// <summary>
/// Bir kişiye gönderilen davet. Davet edilen kişinin henüz hesabı yoktur; daveti, e-postadaki bağlantının taşıdığı
/// token ile okur ve kabul eder.
/// </summary>
public partial class Invitation
{
    [Required]
    [EmailAddress]
    [MaxLength(200)]
    public string Email { get; set; } = "";
}
