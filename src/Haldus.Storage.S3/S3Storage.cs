using System.Buffers;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Haldus;

public sealed class HaldusS3StorageOptions
{
    /// <summary>
    /// Servisin adresi (<c>http://localhost:9000</c>, <c>https://s3.eu-central-1.amazonaws.com</c>); yol içeremez.
    /// Boşsa AWS'nin <see cref="Region"/> bölgesindeki adresi kullanılır.
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>İmzaya giren bölge. MinIO'nun varsayılanı da <c>us-east-1</c>'dir.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>İçeriklerin yazıldığı kova. Var olmalıdır; Haldus kova oluşturmaz.</summary>
    public string Bucket { get; set; } = "";

    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    /// <summary>Bütün anahtarların önüne eklenen yol (<c>shop/files</c>); kovayı başka uygulamalarla paylaşmak için.</summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Kova adreste yol olarak yazılır (<c>host/kova/anahtar</c>); MinIO'nun ve çoğu S3 uyumlu servisin beklediği
    /// biçimdir. Kapatılırsa kova alan adına taşınır (<c>kova.host/anahtar</c>).
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// Uzunluğu bilinmeyen bir içerik bu boyda parçalar halinde yüklenir; yükleme başına bellekte en çok bu kadar
    /// bayt tutulur. S3 son parça dışındakilerin en az 5 MB olmasını ister ve en çok 10 000 parça kabul eder.
    /// </summary>
    public int PartSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Tek bir isteğin, yanıtın başlıkları gelene kadarki süresi. Bir parçanın yüklenmesini kapsar; uzunluğu bilinen
    /// içerik tek istekle yüklendiği için büyük dosyalarda (ör. dışa aktarma) yüklemenin tamamını kapsar.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Servisin reddettiği bir istek. Metin servisin hata kodunu ve açıklamasını taşır; kimlik bilgisi taşımaz.</summary>
public sealed class HaldusS3StorageException : Exception
{
    internal HaldusS3StorageException(string operation, string bucket, HttpStatusCode statusCode, string? errorCode, string? detail)
        : base($"S3 {operation} failed for bucket '{bucket}': {(int)statusCode} {errorCode ?? statusCode.ToString()}{(detail is null ? string.Empty : $" ({detail})")}")
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>Servisin hata kodu (<c>SignatureDoesNotMatch</c>, <c>NoSuchBucket</c>...); yanıt bir S3 hatası değilse boştur.</summary>
    public string? ErrorCode { get; }
}

/// <summary>
/// İçeriği S3 uyumlu bir nesne deposunda (AWS S3, MinIO...) tutar. İstekler <see cref="HttpClient"/> ile gönderilir ve
/// AWS Signature Version 4 ile imzalanır. İçerik veritabanının dışında durduğu için dosya kaydıyla aynı
/// transaction'da kalıcı olmaz: kaydı kaydedilemeyen bir yüklemenin nesnesi depoda kalabilir.
/// </summary>
public sealed partial class HaldusS3Storage : IHaldusFileStorage
{
    public const string HttpClientName = "Haldus.Storage.S3";

    private const int MinPartSize = 5 * 1024 * 1024;
    private const int MaxPartSize = 1024 * 1024 * 1024;
    private const int MaxParts = 10_000;
    private const long MaxSinglePutBytes = 5L * 1024 * 1024 * 1024;

    private static readonly string EmptyHash = S3Signer.HashOf([]);

    private readonly IHttpClientFactory _clients;
    private readonly ILogger _logger;
    private readonly string _root;
    private readonly bool _encrypted;
    private readonly string _bucket;
    private readonly string _region;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly int _partSize;

    // Ayarlar uygulama açılırken değil ilk kullanımda doğrulanır: build sırasındaki şema üretimi uygulamayı
    // kimlik bilgileri olmadan başlatır ve orada durmamalıdır.
    public HaldusS3Storage(IHttpClientFactory clients, IOptions<HaldusS3StorageOptions> options, ILogger<HaldusS3Storage>? logger = null)
    {
        _logger = logger ?? NullLogger<HaldusS3Storage>.Instance;
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        Require(!string.IsNullOrWhiteSpace(settings.Region), "Region is required.");
        Require(!string.IsNullOrEmpty(settings.AccessKey) && !string.IsNullOrEmpty(settings.SecretKey), "AccessKey and SecretKey are required.");
        Require(
            settings.Bucket.Length is >= 3 and <= 63 && settings.Bucket.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.'),
            "Bucket must be 3 to 63 lowercase letters, digits, '-' and '.'.");
        Require(settings.PartSize is >= MinPartSize and <= MaxPartSize, "PartSize must be between 5 MiB and 1 GiB.");

        var service = settings.ServiceUrl ?? new Uri($"https://s3.{settings.Region}.amazonaws.com");
        Require(
            service.IsAbsoluteUri && service.Scheme is "http" or "https" && service.AbsolutePath == "/" && service.Query.Length == 0 && service.UserInfo.Length == 0,
            "ServiceUrl must be an absolute http or https address without a path, query or user info.");

        var prefix = settings.KeyPrefix?.Trim('/');
        Require(string.IsNullOrEmpty(prefix) || IsKey(prefix), "KeyPrefix is made of letters, digits, '-' and '_' separated by '/'.");

        _clients = clients;
        _bucket = settings.Bucket;
        _region = settings.Region;
        _accessKey = settings.AccessKey;
        _secretKey = settings.SecretKey;
        _partSize = settings.PartSize;
        _encrypted = service.Scheme == "https";
        _root = (settings.ForcePathStyle
                ? $"{service.Scheme}://{service.Authority}/{settings.Bucket}/"
                : $"{service.Scheme}://{settings.Bucket}.{service.Authority}/")
            + (string.IsNullOrEmpty(prefix) ? string.Empty : prefix + "/");
    }

    /// <summary>
    /// Uzunluğu bilinen (aranabilir) içerik belleğe alınmadan tek bir istekle akıtılır. Gövdenin özeti imzaya ancak
    /// gövde gönderilmeden önce biliniyorsa girebilir: HTTPS'te özet hesaplanmaz (<c>UNSIGNED-PAYLOAD</c>; bütünlüğü
    /// TLS korur), düz HTTP'de içerik önce bir kez okunup özetlenir, sonra başa sarılıp gönderilir. Uzunluğu
    /// bilinmeyen içerik (istek gövdesi) parça parça belleğe alınır; her parçanın özeti imzalanır.
    /// </summary>
    public async Task WriteAsync(DbContext context, string key, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var address = AddressOf(key);

        if (content.CanSeek && content.Length - content.Position <= MaxSinglePutBytes)
        {
            var start = content.Position;
            var payloadHash = S3Signer.UnsignedPayload;

            if (!_encrypted)
            {
                payloadHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));
                content.Position = start;
            }

            using var body = new BorrowedStreamContent(content, content.Length - start);
            using var response = await SendAsync(HttpMethod.Put, address, body, payloadHash, cancellationToken);
            await EnsureAsync("PutObject", response, cancellationToken);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(_partSize);

        try
        {
            var filled = await content.ReadAtLeastAsync(buffer.AsMemory(0, _partSize), _partSize, throwOnEndOfStream: false, cancellationToken);

            if (filled < _partSize)
            {
                using var response = await SendAsync(HttpMethod.Put, address, buffer.AsMemory(0, filled), cancellationToken);
                await EnsureAsync("PutObject", response, cancellationToken);
                return;
            }

            await WriteInPartsAsync(address, content, buffer, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>İçerik akıtılır; aranabilir değildir, bu yüzden içeriğin bir parçası (<c>Range</c>) istenemez.</summary>
    public async Task<Stream?> OpenReadAsync(DbContext context, string key, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, AddressOf(key), content: null, EmptyHash, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            // Yanıt ayrıca kapatılmaz: bağlantıyı tutan akıştır ve onu çağıran kapatır.
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }

        using (response)
        {
            var failure = await FailureAsync("GetObject", response, cancellationToken);
            return IsMissing(failure) ? null : throw failure;
        }
    }

    public async Task DeleteAsync(DbContext context, string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, AddressOf(key), content: null, EmptyHash, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var failure = await FailureAsync("DeleteObject", response, cancellationToken);

        if (!IsMissing(failure))
        {
            throw failure;
        }
    }

    private async Task WriteInPartsAsync(string address, Stream content, byte[] buffer, CancellationToken cancellationToken)
    {
        string uploadId;

        using (var started = await SendAsync(HttpMethod.Post, address + "?uploads", ReadOnlyMemory<byte>.Empty, cancellationToken))
        {
            await EnsureAsync("CreateMultipartUpload", started, cancellationToken);
            uploadId = Element(await ReadXmlAsync(started, cancellationToken), "UploadId")
                ?? throw new HaldusS3StorageException("CreateMultipartUpload", _bucket, started.StatusCode, null, "The response has no upload id.");
        }

        var upload = "uploadId=" + Uri.EscapeDataString(uploadId);

        try
        {
            var tags = new List<string>();
            var filled = _partSize;

            while (filled > 0)
            {
                if (tags.Count == MaxParts)
                {
                    throw new InvalidOperationException($"The content does not fit in {MaxParts} parts of {_partSize} bytes; increase PartSize.");
                }

                var number = (tags.Count + 1).ToString(CultureInfo.InvariantCulture);

                using (var part = await SendAsync(HttpMethod.Put, $"{address}?partNumber={number}&{upload}", buffer.AsMemory(0, filled), cancellationToken))
                {
                    await EnsureAsync("UploadPart", part, cancellationToken);
                    tags.Add(part.Headers.TryGetValues("ETag", out var values)
                        ? values.First()
                        : throw new HaldusS3StorageException("UploadPart", _bucket, part.StatusCode, null, "The response has no ETag."));
                }

                // Dolmamış parça içeriğin sonudur; yeniden okumaya gerek yoktur.
                filled = filled < _partSize
                    ? 0
                    : await content.ReadAtLeastAsync(buffer.AsMemory(0, _partSize), _partSize, throwOnEndOfStream: false, cancellationToken);
            }

            var manifest = new XElement(
                "CompleteMultipartUpload",
                tags.Select((tag, index) => new XElement("Part", new XElement("PartNumber", index + 1), new XElement("ETag", tag))));

            using var completed = await SendAsync(
                HttpMethod.Post, $"{address}?{upload}", Encoding.UTF8.GetBytes(manifest.ToString(SaveOptions.DisableFormatting)), cancellationToken);
            await EnsureAsync("CompleteMultipartUpload", completed, cancellationToken);

            // Bu işlem 200 ile başlayıp hatayı gövdede bildirebilir; başarı yalnızca beklenen yanıtla sayılır.
            var result = await ReadXmlAsync(completed, cancellationToken);

            if (result is not { Root.Name.LocalName: "CompleteMultipartUploadResult" })
            {
                throw new HaldusS3StorageException(
                    "CompleteMultipartUpload",
                    _bucket,
                    completed.StatusCode,
                    result is null ? null : Element(result, "Code"),
                    result is null ? "The response is not a CompleteMultipartUploadResult." : Element(result, "Message"));
            }
        }
        catch
        {
            await AbortAsync(address, upload);
            throw;
        }
    }

    // Yarım kalan yüklemenin parçaları kovada yer tutmaya devam eder. Çağıranın iptal belirteci çoktan iptal
    // edilmiş olabileceği için vazgeçme isteği ona bağlanmaz; başarısız olursa asıl hata örtülmez.
    private async Task AbortAsync(string address, string upload)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, $"{address}?{upload}", content: null, EmptyHash, CancellationToken.None);

            if (!response.IsSuccessStatusCode)
            {
                LogAbortFailed(_logger, _bucket, (int)response.StatusCode, null);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            LogAbortFailed(_logger, _bucket, 0, exception);
        }
    }

    // Vazgeçilemeyen yüklemenin parçaları kovada yer tutmaya (ve ücretlendirilmeye) devam eder; sessiz kalmamalıdır.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "An unfinished multipart upload in bucket {Bucket} could not be aborted (status {Status}); its parts stay in the bucket until a lifecycle rule removes them.")]
    private static partial void LogAbortFailed(ILogger logger, string bucket, int status, Exception? exception);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string address, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        SendAsync(method, address, new ReadOnlyMemoryContent(payload), S3Signer.HashOf(payload.Span), cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string address, HttpContent? content, string payloadHash, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, address) { Content = content };

        // İmzanın saati gerçek saat olmalıdır (servis birkaç dakikadan fazla sapmayı reddeder); bu yüzden
        // uygulamanın, testlerde değiştirilebilen TimeProvider servisi kullanılmaz.
        S3Signer.Sign(request, payloadHash, _accessKey, _secretKey, _region, DateTimeOffset.UtcNow);

        return await _clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task EnsureAsync(string operation, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(operation, response, cancellationToken);
        }
    }

    private async Task<HaldusS3StorageException> FailureAsync(string operation, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var error = await ReadXmlAsync(response, cancellationToken) is { Root.Name.LocalName: "Error" } document ? document : null;
        return new HaldusS3StorageException(operation, _bucket, response.StatusCode, Element(error, "Code"), Element(error, "Message"));
    }

    // Olmayan nesne "içerik yok" demektir; olmayan kova ise bir yapılandırma hatasıdır ve sessizce geçilmez.
    private static bool IsMissing(HaldusS3StorageException failure) =>
        failure is { StatusCode: HttpStatusCode.NotFound, ErrorCode: not "NoSuchBucket" };

    // Yanıt beklenen XML olmayabilir (araya giren bir vekil sunucunun sayfası); o zaman yalnızca durum kodu kalır.
    private static async Task<XDocument?> ReadXmlAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int limit = 64 * 1024;
        var buffer = new byte[limit];

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var read = await body.ReadAtLeastAsync(buffer, limit, throwOnEndOfStream: false, cancellationToken);

        try
        {
            return read == 0 ? null : XDocument.Parse(Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? Element(XDocument? document, string name) =>
        document?.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;

    // Anahtarı Haldus üretir; yine de adresin anlamını değiştirebilecek bir anahtar (nokta, soru işareti, yüzde)
    // hiçbir koşulda kabul edilmez. Kural dosya sistemi deposundakiyle aynıdır.
    private string AddressOf(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return IsKey(key)
            ? _root + key
            : throw new ArgumentException("A storage key is made of letters, digits, '-' and '_' separated by '/'.", nameof(key));
    }

    private static bool IsKey(string value) =>
        value.Split('/').All(segment => segment.Length > 0 && segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("The S3 storage is not configured correctly: " + message);
        }
    }

    /// <summary>Akışı kapatmadan gönderen gövde: akış çağırana aittir, <see cref="StreamContent"/> ise onu kapatırdı.</summary>
    private sealed class BorrowedStreamContent(Stream source, long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            source.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            source.CopyToAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long computed)
        {
            computed = length;
            return true;
        }
    }
}

public static class HaldusS3StorageExtensions
{
    /// <summary>
    /// Dosya içeriklerini (<c>MapFile</c>) veritabanı yerine S3 uyumlu bir nesne deposunda saklar. Kova önceden
    /// oluşturulmuş olmalıdır. İstekler <see cref="HaldusS3Storage.HttpClientName"/> adlı istemciyle gönderilir;
    /// uygulama onu <c>AddHttpClient</c> ile ayrıca yapılandırabilir (vekil sunucu, yeniden deneme).
    /// </summary>
    public static IServiceCollection AddHaldusS3Storage(this IServiceCollection services, Action<HaldusS3StorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<HaldusS3StorageOptions>().Configure(configure);

        // Yönlendirme izlenmez: imza ilk adrese aittir, yönlendirilen istek anlaşılmaz bir imza hatasıyla dönerdi.
        // Servisin yönlendirme yanıtı (yanlış bölge, yanlış adres) olduğu gibi hata olur.
        services.AddHttpClient(HaldusS3Storage.HttpClientName)
            .ConfigureHttpClient((provider, client) => client.Timeout = provider.GetRequiredService<IOptions<HaldusS3StorageOptions>>().Value.Timeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        services.RemoveAll<IHaldusFileStorage>();
        services.AddSingleton<IHaldusFileStorage, HaldusS3Storage>();
        return services;
    }
}
