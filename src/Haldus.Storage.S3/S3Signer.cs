using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Haldus;

/// <summary>
/// AWS Signature Version 4. İsteği, gizli anahtarın kendisini göndermeden onu bildiğini kanıtlayan bir imzayla
/// işaretler; gizli anahtar buradan başka hiçbir yere (başlık, adres, hata metni) yazılmaz.
/// </summary>
/// <remarks>
/// Dosya, imzaya ihtiyaç duyan öteki paketlere kaynak olarak bağlanır (<c>Compile Include</c>): paketler birbirine
/// başvurmaz, her biri kendi kopyasını derler.
/// </remarks>
internal static class S3Signer
{
    internal const string Algorithm = "AWS4-HMAC-SHA256";

    internal const string S3 = "s3";

    /// <summary>Gövdenin özeti imzaya katılmaz; gövdenin bütünlüğünü taşıma katmanı (TLS) korur.</summary>
    internal const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    internal static string HashOf(ReadOnlySpan<byte> payload) => Convert.ToHexStringLower(SHA256.HashData(payload));

    /// <summary>
    /// İsteğe <c>Host</c>, <c>x-amz-date</c> ve <c>Authorization</c> başlıklarını, S3'te ayrıca
    /// <c>x-amz-content-sha256</c> başlığını yazar. İstekte o anda bulunan bütün başlıklar imzalanır; imzadan sonra
    /// eklenen başlık imzasız gider, imzalanmış bir başlığı sonradan değiştirmek ise imzayı bozar.
    /// </summary>
    /// <param name="service">İmzanın kapsamına giren servis adı (<c>s3</c>, <c>sqs</c>).</param>
    internal static void Sign(
        HttpRequestMessage request,
        string payloadHash,
        string accessKey,
        string secretKey,
        string region,
        DateTimeOffset now,
        string service = S3)
    {
        var uri = request.RequestUri ?? throw new ArgumentException("The request has no address.", nameof(request));
        var timestamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = timestamp[..8];
        var scope = $"{date}/{region}/{service}/aws4_request";
        var s3 = service == S3;

        // İmzalanan Host ile gönderilen aynı olmalıdır; bu yüzden HttpClient'ın türetmesine bırakılmaz.
        request.Headers.Authorization = null;
        request.Headers.Host = uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}";

        // Gövdenin özetini başlıkta yalnızca S3 ister; öteki servislerde özet yalnızca imzalanan metne girer.
        if (s3)
        {
            Set(request.Headers, "x-amz-content-sha256", payloadHash);
        }

        Set(request.Headers, "x-amz-date", timestamp);

        var headers = request.Headers
            .Select(header => (Name: header.Key.ToLowerInvariant(), Value: string.Join(",", header.Value.Select(Collapse))))
            .OrderBy(header => header.Name, StringComparer.Ordinal)
            .ToList();
        var signedHeaders = string.Join(';', headers.Select(header => header.Name));

        var canonicalRequest = string.Join(
            '\n',
            request.Method.Method,
            CanonicalPath(uri, encodeTwice: !s3),
            CanonicalQuery(uri),
            string.Concat(headers.Select(header => $"{header.Name}:{header.Value}\n")),
            signedHeaders,
            payloadHash);

        var stringToSign = string.Join('\n', Algorithm, timestamp, scope, HashOf(Encoding.UTF8.GetBytes(canonicalRequest)));

        var key = Encoding.UTF8.GetBytes("AWS4" + secretKey);

        foreach (var part in new[] { date, region, service, "aws4_request" })
        {
            key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(part));
        }

        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
        CryptographicOperations.ZeroMemory(key);

        request.Headers.Authorization = new AuthenticationHeaderValue(
            Algorithm,
            $"Credential={accessKey}/{scope},SignedHeaders={signedHeaders},Signature={signature}");
    }

    private static void Set(HttpRequestHeaders headers, string name, string value)
    {
        headers.Remove(name);
        headers.TryAddWithoutValidation(name, value);
    }

    // S3 yolu bir kez kodlar (diğer AWS servisleri iki kez): her parça çözülüp yalnızca RFC 3986'nın ayrılmamış
    // karakterleri açık kalacak biçimde yeniden kodlanır; öteki servislerde sonuç bir kez daha kodlanır.
    private static string CanonicalPath(Uri uri, bool encodeTwice)
    {
        var path = string.Join('/', uri.AbsolutePath.Split('/').Select(segment => encodeTwice ? Uri.EscapeDataString(Encode(segment)) : Encode(segment)));
        return path.Length == 0 ? "/" : path;
    }

    private static string CanonicalQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?');

        if (query.Length == 0)
        {
            return string.Empty;
        }

        var parameters = query.Split('&').Select(parameter =>
        {
            var separator = parameter.IndexOf('=', StringComparison.Ordinal);

            return separator < 0
                ? (Name: Encode(parameter), Value: string.Empty)
                : (Name: Encode(parameter[..separator]), Value: Encode(parameter[(separator + 1)..]));
        });

        return string.Join('&', parameters
            .OrderBy(parameter => parameter.Name, StringComparer.Ordinal)
            .ThenBy(parameter => parameter.Value, StringComparer.Ordinal)
            .Select(parameter => $"{parameter.Name}={parameter.Value}"));
    }

    private static string Encode(string value) => Uri.EscapeDataString(Uri.UnescapeDataString(value));

    private static string Collapse(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
