using System.Buffers;
using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
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
    /// Servisin adresi (<c>http://localhost:9000</c>); AWS dışındaki S3 uyumlu servisler (MinIO...) içindir ve yol
    /// içeremez. Boşsa AWS'nin <see cref="Region"/> bölgesindeki adresi kullanılır.
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>Bölge. MinIO'nun varsayılanı da <c>us-east-1</c>'dir.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>İçeriklerin yazıldığı kova. Var olmalıdır; Haldus kova oluşturmaz.</summary>
    public string Bucket { get; set; } = "";

    /// <summary>
    /// Erişim anahtarı. <see cref="SecretKey"/> ile birlikte verilir; ikisi de boşsa kimlik bilgisi AWS SDK'nın
    /// varsayılan zincirinden okunur (ortam değişkenleri, profil, kapsayıcı veya sunucu rolü).
    /// </summary>
    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    /// <summary>Geçici kimlik bilgisinin oturum token'ı; yalnızca <see cref="AccessKey"/> ile birlikte anlamlıdır.</summary>
    public string? SessionToken { get; set; }

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

    /// <summary>Tek bir isteğin süresi. Bir parçanın veya tek istekle yüklenen bir içeriğin tamamını kapsar.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// AWS SDK istemcisinin ayarlarını, Haldus kendi ayarlarını yazdıktan sonra değiştirir (vekil sunucu, yeniden
    /// deneme sayısı, kendi <c>HttpClient</c> üreticisi...).
    /// </summary>
    public Action<AmazonS3Config>? ConfigureClient { get; set; }
}

/// <summary>Servisin reddettiği bir istek. Metin servisin hata kodunu ve açıklamasını taşır; gizli anahtarı taşımaz.</summary>
public sealed class HaldusS3StorageException : Exception
{
    internal HaldusS3StorageException(string operation, string bucket, AmazonS3Exception inner)
        : base($"S3 {operation} failed for bucket '{bucket}': {(int)inner.StatusCode} {inner.ErrorCode ?? inner.StatusCode.ToString()} ({inner.Message})", inner)
    {
        StatusCode = inner.StatusCode;
        ErrorCode = inner.ErrorCode;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>Servisin hata kodu (<c>SignatureDoesNotMatch</c>, <c>NoSuchBucket</c>...).</summary>
    public string? ErrorCode { get; }
}

/// <summary>
/// İçeriği S3 uyumlu bir nesne deposunda (AWS S3, MinIO...) tutar; servisle AWS SDK (<c>AWSSDK.S3</c>) konuşur.
/// İçerik veritabanının dışında durduğu için dosya kaydıyla aynı transaction'da kalıcı olmaz: kaydı kaydedilemeyen
/// bir yüklemenin nesnesi depoda kalabilir.
/// </summary>
public sealed partial class HaldusS3Storage : IHaldusFileStorage, IDisposable
{
    private const int MinPartSize = 5 * 1024 * 1024;
    private const int MaxPartSize = 1024 * 1024 * 1024;
    private const int MaxParts = 10_000;
    private const long MaxSinglePutBytes = 5L * 1024 * 1024 * 1024;

    private readonly AmazonS3Client _client;
    private readonly ILogger _logger;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly bool _encrypted;
    private readonly int _partSize;

    // Ayarlar uygulama açılırken değil ilk kullanımda doğrulanır: build sırasındaki şema üretimi uygulamayı
    // kimlik bilgileri olmadan başlatır ve orada durmamalıdır.
    public HaldusS3Storage(IOptions<HaldusS3StorageOptions> options, ILogger<HaldusS3Storage>? logger = null)
    {
        _logger = logger ?? NullLogger<HaldusS3Storage>.Instance;
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        Require(!string.IsNullOrWhiteSpace(settings.Region), "Region is required.");
        Require(
            string.IsNullOrEmpty(settings.AccessKey) == string.IsNullOrEmpty(settings.SecretKey),
            "AccessKey and SecretKey are given together, or both are left empty to use the default AWS credentials.");
        Require(
            settings.Bucket.Length is >= 3 and <= 63 && settings.Bucket.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.'),
            "Bucket must be 3 to 63 lowercase letters, digits, '-' and '.'.");
        Require(settings.PartSize is >= MinPartSize and <= MaxPartSize, "PartSize must be between 5 MiB and 1 GiB.");

        var service = settings.ServiceUrl;
        Require(
            service is null
                || (service.IsAbsoluteUri && service.Scheme is "http" or "https" && service.AbsolutePath == "/" && service.Query.Length == 0 && service.UserInfo.Length == 0),
            "ServiceUrl must be an absolute http or https address without a path, query or user info.");

        var prefix = settings.KeyPrefix?.Trim('/');
        Require(string.IsNullOrEmpty(prefix) || IsKey(prefix), "KeyPrefix is made of letters, digits, '-' and '_' separated by '/'.");

        var config = new AmazonS3Config { ForcePathStyle = settings.ForcePathStyle, Timeout = settings.Timeout };

        if (service is null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }
        else
        {
            config.ServiceURL = service.GetLeftPart(UriPartial.Authority);
            config.AuthenticationRegion = settings.Region;

            // SDK'nın yeni varsayılanı her yüklemeye bir bütünlük özeti ekler; S3 uyumlu servislerin hepsi onu tanımaz.
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }

        settings.ConfigureClient?.Invoke(config);

        _client = string.IsNullOrEmpty(settings.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(
                string.IsNullOrEmpty(settings.SessionToken)
                    ? new BasicAWSCredentials(settings.AccessKey, settings.SecretKey)
                    : new SessionAWSCredentials(settings.AccessKey, settings.SecretKey, settings.SessionToken),
                config);

        _bucket = settings.Bucket;
        _partSize = settings.PartSize;
        _encrypted = service is null || service.Scheme == "https";
        _prefix = string.IsNullOrEmpty(prefix) ? string.Empty : prefix + "/";
    }

    /// <summary>
    /// Uzunluğu bilinen (aranabilir) içerik belleğe alınmadan tek bir istekle akıtılır; HTTPS'te gövde imzaya girmez
    /// (bütünlüğü TLS korur), düz HTTP'de SDK onu önce bir kez okuyup özetler. Uzunluğu bilinmeyen içerik (istek
    /// gövdesi) parça parça belleğe alınır ve çok parçalı yüklemeyle gönderilir.
    /// </summary>
    public async Task WriteAsync(DbContext context, string key, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var name = NameOf(key);

        if (content.CanSeek && content.Length - content.Position <= MaxSinglePutBytes)
        {
            await PutAsync(name, content, cancellationToken);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(_partSize);

        try
        {
            var filled = await content.ReadAtLeastAsync(buffer.AsMemory(0, _partSize), _partSize, throwOnEndOfStream: false, cancellationToken);

            if (filled < _partSize)
            {
                using var whole = new MemoryStream(buffer, 0, filled, writable: false);
                await PutAsync(name, whole, cancellationToken);
                return;
            }

            await WriteInPartsAsync(name, content, buffer, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>İçerik akıtılır; aranabilir değildir, bu yüzden içeriğin bir parçası (<c>Range</c>) istenemez.</summary>
    public async Task<Stream?> OpenReadAsync(DbContext context, string key, CancellationToken cancellationToken)
    {
        try
        {
            // Yanıt ayrıca kapatılmaz: bağlantıyı tutan akıştır ve onu çağıran kapatır.
            var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = NameOf(key) }, cancellationToken);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception exception)
        {
            // Olmayan nesne "içerik yok" demektir; olmayan kova ise bir yapılandırma hatasıdır ve sessizce geçilmez.
            return exception is { StatusCode: HttpStatusCode.NotFound, ErrorCode: not "NoSuchBucket" }
                ? null
                : throw new HaldusS3StorageException("GetObject", _bucket, exception);
        }
    }

    public async Task DeleteAsync(DbContext context, string key, CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = NameOf(key) }, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception is { StatusCode: HttpStatusCode.NotFound, ErrorCode: not "NoSuchBucket" })
        {
            // Zaten yok.
        }
        catch (AmazonS3Exception exception)
        {
            throw new HaldusS3StorageException("DeleteObject", _bucket, exception);
        }
    }

    public void Dispose() => _client.Dispose();

    private async Task PutAsync(string name, Stream content, CancellationToken cancellationToken)
    {
        try
        {
            await _client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = name,
                    InputStream = content,

                    // Akış çağırana aittir ve bulunduğu yerden okunur.
                    AutoCloseStream = false,
                    AutoResetStreamPosition = false,
                    DisablePayloadSigning = _encrypted,
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception)
        {
            throw new HaldusS3StorageException("PutObject", _bucket, exception);
        }
    }

    private async Task WriteInPartsAsync(string name, Stream content, byte[] buffer, CancellationToken cancellationToken)
    {
        string uploadId;

        try
        {
            uploadId = (await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = _bucket, Key = name }, cancellationToken)).UploadId;
        }
        catch (AmazonS3Exception exception)
        {
            throw new HaldusS3StorageException("CreateMultipartUpload", _bucket, exception);
        }

        try
        {
            var parts = new List<PartETag>();
            var filled = _partSize;

            while (filled > 0)
            {
                if (parts.Count == MaxParts)
                {
                    throw new InvalidOperationException($"The content does not fit in {MaxParts} parts of {_partSize} bytes; increase PartSize.");
                }

                var number = parts.Count + 1;

                try
                {
                    using var part = new MemoryStream(buffer, 0, filled, writable: false);
                    var uploaded = await _client.UploadPartAsync(
                        new UploadPartRequest
                        {
                            BucketName = _bucket,
                            Key = name,
                            UploadId = uploadId,
                            PartNumber = number,
                            PartSize = filled,
                            InputStream = part,
                            DisablePayloadSigning = _encrypted,
                        },
                        cancellationToken);

                    parts.Add(new PartETag(number, uploaded.ETag));
                }
                catch (AmazonS3Exception exception)
                {
                    throw new HaldusS3StorageException("UploadPart", _bucket, exception);
                }

                // Dolmamış parça içeriğin sonudur; yeniden okumaya gerek yoktur.
                filled = filled < _partSize
                    ? 0
                    : await content.ReadAtLeastAsync(buffer.AsMemory(0, _partSize), _partSize, throwOnEndOfStream: false, cancellationToken);
            }

            try
            {
                await _client.CompleteMultipartUploadAsync(
                    new CompleteMultipartUploadRequest { BucketName = _bucket, Key = name, UploadId = uploadId, PartETags = parts },
                    cancellationToken);
            }
            catch (AmazonS3Exception exception)
            {
                throw new HaldusS3StorageException("CompleteMultipartUpload", _bucket, exception);
            }
        }
        catch
        {
            // İçeriği okurken çıkan hata (ör. boyut sınırı) olduğu gibi yeniden fırlatılır; çağıran onu tanır.
            await AbortAsync(name, uploadId);
            throw;
        }
    }

    // Yarım kalan yüklemenin parçaları kovada yer tutmaya devam eder. Çağıranın iptal belirteci çoktan iptal
    // edilmiş olabileceği için vazgeçme isteği ona bağlanmaz; başarısız olursa asıl hata örtülmez.
    private async Task AbortAsync(string name, string uploadId)
    {
        try
        {
            await _client.AbortMultipartUploadAsync(
                new AbortMultipartUploadRequest { BucketName = _bucket, Key = name, UploadId = uploadId },
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException or OperationCanceledException)
        {
            LogAbortFailed(_logger, _bucket, exception);
        }
    }

    // Vazgeçilemeyen yüklemenin parçaları kovada yer tutmaya (ve ücretlendirilmeye) devam eder; sessiz kalmamalıdır.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "An unfinished multipart upload in bucket {Bucket} could not be aborted; its parts stay in the bucket until a lifecycle rule removes them.")]
    private static partial void LogAbortFailed(ILogger logger, string bucket, Exception exception);

    // Anahtarı Haldus üretir; yine de adresin anlamını değiştirebilecek bir anahtar (nokta, soru işareti, yüzde)
    // hiçbir koşulda kabul edilmez. Kural dosya sistemi deposundakiyle aynıdır.
    private string NameOf(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return IsKey(key)
            ? _prefix + key
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
}

public static class HaldusS3StorageExtensions
{
    /// <summary>
    /// Dosya içeriklerini (<c>MapFile</c>) veritabanı yerine S3 uyumlu bir nesne deposunda saklar. Kova önceden
    /// oluşturulmuş olmalıdır. Servisle AWS SDK konuşur; istemcinin ayarları
    /// <see cref="HaldusS3StorageOptions.ConfigureClient"/> ile değiştirilebilir.
    /// </summary>
    public static IServiceCollection AddHaldusS3Storage(this IServiceCollection services, Action<HaldusS3StorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<HaldusS3StorageOptions>().Configure(configure);
        services.RemoveAll<IHaldusFileStorage>();
        services.AddSingleton<IHaldusFileStorage, HaldusS3Storage>();
        return services;
    }
}
