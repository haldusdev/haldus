using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Content;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus.IntegrationTests;

/// <summary>İmzanın, AWS belgelerindeki örnek isteklerle aynı sonucu verdiği; bir sunucu gerekmez.</summary>
public sealed class S3SignerTests
{
    // AWS'nin "Signature Calculations for the Authorization Header" belgesindeki örnek kimlik; gerçek bir hesaba ait değildir.
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly DateTimeOffset Now = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Signer_MatchesTheExamplesOfTheAwsDocumentation()
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/test.txt");
        get.Headers.Range = new RangeHeaderValue(0, 9);
        Assert.Equal(
            "host;range;x-amz-content-sha256;x-amz-date/f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            Sign(get, EmptyHash));
        Assert.Equal("20130524T000000Z", Assert.Single(get.Headers.GetValues("x-amz-date")));
        Assert.Equal(EmptyHash, Assert.Single(get.Headers.GetValues("x-amz-content-sha256")));
        Assert.Equal("examplebucket.s3.amazonaws.com", get.Headers.Host);

        // Yoldaki özel karakter kodlanır, gövdenin özeti ve istekteki öteki başlıklar imzaya girer.
        using var put = new HttpRequestMessage(HttpMethod.Put, "https://examplebucket.s3.amazonaws.com/test$file.text");
        put.Headers.Date = Now;
        put.Headers.Add("x-amz-storage-class", "REDUCED_REDUNDANCY");
        var body = Encoding.UTF8.GetBytes("Welcome to Amazon S3.");
        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", S3Signer.HashOf(body));
        Assert.Equal(
            "date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class/98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd",
            Sign(put, S3Signer.HashOf(body)));

        // Değeri olmayan ve birden çok sorgu parametresi.
        using var lifecycle = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/?lifecycle");
        Assert.EndsWith("/fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543", Sign(lifecycle, EmptyHash), StringComparison.Ordinal);

        using var list = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/?prefix=J&max-keys=2");
        Assert.EndsWith("/34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7", Sign(list, EmptyHash), StringComparison.Ordinal);
    }

    [Fact]
    public void Signer_NeverPutsTheSecretKeyOnTheRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "http://localhost:9000/bucket/Attachment/abc?partNumber=1&uploadId=a%2Fb%3D");
        S3Signer.Sign(request, S3Signer.UnsignedPayload, AccessKey, SecretKey, "eu-central-1", Now);

        Assert.DoesNotContain(SecretKey, request.ToString(), StringComparison.Ordinal);
        Assert.Equal("localhost:9000", request.Headers.Host);
        Assert.StartsWith($"Credential={AccessKey}/20130524/eu-central-1/s3/aws4_request,", request.Headers.Authorization!.Parameter, StringComparison.Ordinal);

        // Aynı istek yeniden imzalanırsa önceki imza imzalanan başlıklara karışmaz.
        var first = request.Headers.Authorization.Parameter;
        S3Signer.Sign(request, S3Signer.UnsignedPayload, AccessKey, SecretKey, "eu-central-1", Now);
        Assert.Equal(first, request.Headers.Authorization!.Parameter);
    }

    /// <summary>İmzalanan başlıklar ve imza; kimlik ve kapsam her örnekte aynıdır.</summary>
    private static string Sign(HttpRequestMessage request, string payloadHash)
    {
        S3Signer.Sign(request, payloadHash, AccessKey, SecretKey, "us-east-1", Now);

        const string credential = $"Credential={AccessKey}/20130524/us-east-1/s3/aws4_request,SignedHeaders=";
        var authorization = request.Headers.Authorization!;
        Assert.Equal(S3Signer.Algorithm, authorization.Scheme);
        Assert.StartsWith(credential, authorization.Parameter, StringComparison.Ordinal);

        return authorization.Parameter![credential.Length..].Replace(",Signature=", "/", StringComparison.Ordinal);
    }
}

/// <summary>S3 deposu (<c>AddHaldusS3Storage</c>), gerçek bir MinIO sunucusuna karşı.</summary>
[Collection(ShopCollection.Name)]
public sealed class S3StorageTests(ShopFixture fixture, MinioFixture minio) : IClassFixture<MinioFixture>, IAsyncDisposable
{
    private readonly List<WebApplicationFactory<ShopMessages>> _applications = [];
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task File_IsStoredInTheBucketThroughTheFileEndpoints()
    {
        var client = CreateClient(minio.Plain.Configure);
        var file = await CreateAsync(client, "notes.txt");
        var url = $"/media-files/{file.Id}/content";
        var first = Encoding.UTF8.GetBytes("stored in a bucket");

        var uploaded = await ReadAsync<MediaFileResponse>(await UploadAsync(client, url, first));
        Assert.Equal((FileStatus.Uploaded, first.LongLength, Convert.ToHexStringLower(SHA256.HashData(first))), (uploaded.Status, uploaded.Size, uploaded.Checksum));

        // İçerik kovada, kaydın anahtarıyla durur ve uç onu oradan gönderir.
        var firstKey = await StorageKeyAsync(file.Id);
        Assert.StartsWith("MediaFile/", firstKey, StringComparison.Ordinal);
        Assert.Equal(first, await minio.Plain.ReadObjectAsync(firstKey));

        var download = await SendAsync(client, HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(first, await download.Content.ReadAsByteArrayAsync(CancellationToken));
        Assert.Equal("text/plain", download.Content.Headers.ContentType?.MediaType);

        // Yeniden yüklenen içerik yeni bir nesnedir; eskisi kovadan kaldırılır.
        var second = RandomNumberGenerator.GetBytes(48 * 1024);
        await ReadAsync<MediaFileResponse>(await UploadAsync(client, url, second));
        var secondKey = await StorageKeyAsync(file.Id);

        Assert.NotEqual(firstKey, secondKey);
        Assert.Null(await minio.Plain.ReadObjectAsync(firstKey));
        Assert.Equal(second, await minio.Plain.ReadObjectAsync(secondKey));
        Assert.Equal(second, await (await SendAsync(client, HttpMethod.Get, url)).Content.ReadAsByteArrayAsync(CancellationToken));

        // Reddedilen yüklemeler (boş; gövdesi okunurken sınırı aşan) kovada iz bırakmaz ve içeriğe dokunmaz.
        var before = await minio.Plain.ListKeysAsync();
        using var tooLarge = new MultipartFormDataContent { { Part(new byte[64 * 1024 + 1]), "file", "big.txt" } };

        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, url, [])).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await SendAsync(client, HttpMethod.Put, url, tooLarge)).StatusCode);
        Assert.Equal(before, await minio.Plain.ListKeysAsync());
        Assert.Equal(second, await (await SendAsync(client, HttpMethod.Get, url)).Content.ReadAsByteArrayAsync(CancellationToken));

        // Kayıt silinince nesnesi de gider.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Delete, $"/media-files/{file.Id}")).StatusCode);
        Assert.Null(await minio.Plain.ReadObjectAsync(secondKey));
        Assert.DoesNotContain(secondKey, await minio.Plain.ListKeysAsync());
    }

    [Fact]
    public async Task KeyPrefix_PlacesEveryObjectUnderIt()
    {
        var prefix = $"shared/app_{Guid.NewGuid():N}";
        var client = CreateClient(options =>
        {
            minio.Plain.Configure(options);
            options.KeyPrefix = $"/{prefix}/";
        });

        var file = await CreateAsync(client, "prefixed.txt");
        var url = $"/media-files/{file.Id}/content";
        var bytes = Encoding.UTF8.GetBytes("under a prefix");
        await ReadAsync<MediaFileResponse>(await UploadAsync(client, url, bytes));

        // Kayıt önekten habersizdir; önek yalnızca nesnenin kovadaki adındadır.
        var key = await StorageKeyAsync(file.Id);
        Assert.StartsWith("MediaFile/", key, StringComparison.Ordinal);
        Assert.Equal([$"{prefix}/{key}"], await minio.Plain.ListKeysAsync(prefix + "/"));
        Assert.Null(await minio.Plain.ReadObjectAsync(key));
        Assert.Equal(bytes, await (await SendAsync(client, HttpMethod.Get, url)).Content.ReadAsByteArrayAsync(CancellationToken));

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Delete, $"/media-files/{file.Id}")).StatusCode);
        Assert.Empty(await minio.Plain.ListKeysAsync(prefix + "/"));
    }

    [Fact]
    public async Task WrongSecretKey_FailsLoudlyAndStoresNothing()
    {
        var wrong = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var storage = minio.CreateStorage(options =>
        {
            minio.Plain.Configure(options);
            options.SecretKey = wrong;
        });

        using var content = new MemoryStream([1, 2, 3]);
        var failures = new[]
        {
            await Assert.ThrowsAsync<HaldusS3StorageException>(() => storage.WriteAsync(null!, "MediaFile/denied", content, CancellationToken)),
            await Assert.ThrowsAsync<HaldusS3StorageException>(() => storage.WriteAsync(null!, "MediaFile/denied", new ForwardOnlyStream([1, 2, 3]), CancellationToken)),
            await Assert.ThrowsAsync<HaldusS3StorageException>(() => storage.OpenReadAsync(null!, "MediaFile/denied", CancellationToken)),
            await Assert.ThrowsAsync<HaldusS3StorageException>(() => storage.DeleteAsync(null!, "MediaFile/denied", CancellationToken)),
        };

        Assert.All(failures, failure =>
        {
            Assert.Equal((HttpStatusCode.Forbidden, "SignatureDoesNotMatch"), (failure.StatusCode, failure.ErrorCode));
            Assert.Contains(MinioFixture.Bucket, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(wrong, failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(minio.Plain.SecretKey, failure.ToString(), StringComparison.Ordinal);
        });

        // Bilinmeyen bir erişim anahtarı ve olmayan bir kova da "içerik yok" sayılmaz.
        var unknown = minio.CreateStorage(options =>
        {
            minio.Plain.Configure(options);
            options.AccessKey = "nobody";
        });
        var missing = minio.CreateStorage(options =>
        {
            minio.Plain.Configure(options);
            options.Bucket = "no-such-bucket";
        });

        Assert.Equal("InvalidAccessKeyId", (await Assert.ThrowsAsync<HaldusS3StorageException>(() => unknown.OpenReadAsync(null!, "MediaFile/denied", CancellationToken))).ErrorCode);
        Assert.Equal("NoSuchBucket", (await Assert.ThrowsAsync<HaldusS3StorageException>(() => missing.OpenReadAsync(null!, "MediaFile/denied", CancellationToken))).ErrorCode);
        Assert.Equal("NoSuchBucket", (await Assert.ThrowsAsync<HaldusS3StorageException>(() => missing.DeleteAsync(null!, "MediaFile/denied", CancellationToken))).ErrorCode);

        // Uçtan gelen yükleme de başarılı görünmez: kayıt içeriksiz kalır ve kovaya hiçbir şey yazılmaz.
        var before = await minio.Plain.ListKeysAsync();
        var client = CreateClient(options =>
        {
            minio.Plain.Configure(options);
            options.SecretKey = wrong;
        });
        var file = await CreateAsync(client, "denied.txt");

        var denied = await UploadAsync(client, $"/media-files/{file.Id}/content", [1, 2, 3]);
        var problem = await denied.Content.ReadAsStringAsync(CancellationToken);

        // Hatanın ayrıntısı (geliştirme ortamında yanıta da yazılır) servisin kodunu taşır, gizli anahtarı taşımaz.
        Assert.Equal(HttpStatusCode.InternalServerError, denied.StatusCode);
        Assert.Contains("SignatureDoesNotMatch", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(wrong, problem, StringComparison.Ordinal);
        Assert.DoesNotContain(minio.Plain.SecretKey, problem, StringComparison.Ordinal);

        var unchanged = await ReadAsync<MediaFileResponse>(await SendAsync(client, HttpMethod.Get, $"/media-files/{file.Id}"));
        Assert.Equal((FileStatus.Pending, 0L), (unchanged.Status, unchanged.Size));
        Assert.Equal(before, await minio.Plain.ListKeysAsync());
    }

    [Fact]
    public async Task ContentOfUnknownLength_IsUploadedInPartsWithoutBeingBufferedWhole()
    {
        const int partSize = 5 * 1024 * 1024;
        var storage = minio.CreateStorage(options =>
        {
            minio.Plain.Configure(options);
            options.PartSize = partSize;
        });

        // İki tam parça ve bir artık: uzunluğu bilinmeyen içerik çok parçalı yüklemeyle yazılır.
        var bytes = RandomNumberGenerator.GetBytes((2 * partSize) + 123);
        await storage.WriteAsync(null!, "MediaFile/parts", new ForwardOnlyStream(bytes), CancellationToken);

        Assert.EndsWith("-3\"", await minio.Plain.ETagAsync("MediaFile/parts"), StringComparison.Ordinal);

        await using (var read = await storage.OpenReadAsync(null!, "MediaFile/parts", CancellationToken))
        {
            Assert.NotNull(read);
            Assert.Equal(SHA256.HashData(bytes), await SHA256.HashDataAsync(read, CancellationToken));
        }

        // Tam bir parça boyundaki içerik tek parçalı bir yüklemedir; bir parçadan küçüğü tek istekle yazılır.
        await storage.WriteAsync(null!, "MediaFile/exact", new ForwardOnlyStream(bytes.AsSpan(0, partSize).ToArray()), CancellationToken);
        await storage.WriteAsync(null!, "MediaFile/small", new ForwardOnlyStream(bytes.AsSpan(0, 1000).ToArray()), CancellationToken);
        Assert.EndsWith("-1\"", await minio.Plain.ETagAsync("MediaFile/exact"), StringComparison.Ordinal);
        Assert.DoesNotContain("-", await minio.Plain.ETagAsync("MediaFile/small"), StringComparison.Ordinal);
        Assert.Equal(bytes.AsSpan(0, partSize).ToArray(), await minio.Plain.ReadObjectAsync("MediaFile/exact"));
        Assert.Equal(bytes.AsSpan(0, 1000).ToArray(), await minio.Plain.ReadObjectAsync("MediaFile/small"));

        // Okuması yarıda kesilen içerik (HaldusFiles boyut sınırını böyle bildirir) kendi hatasıyla döner; yarım
        // yükleme kovada ne nesne ne de bekleyen parça bırakır.
        var failure = await Assert.ThrowsAsync<ContentRejectedException>(() =>
            storage.WriteAsync(null!, "MediaFile/aborted", new ForwardOnlyStream(bytes, failAfter: partSize + 1000), CancellationToken));
        Assert.Equal("rejected while reading", failure.Message);
        Assert.Null(await storage.OpenReadAsync(null!, "MediaFile/aborted", CancellationToken));
        Assert.Empty(await minio.Plain.ListPendingUploadsAsync());

        foreach (var key in new[] { "MediaFile/parts", "MediaFile/exact", "MediaFile/small" })
        {
            await storage.DeleteAsync(null!, key, CancellationToken);
        }
    }

    [Fact]
    public async Task SeekableContent_IsStreamedFromItsPositionAndLeftOpen()
    {
        var storage = minio.CreateStorage(minio.Plain.Configure);
        var bytes = RandomNumberGenerator.GetBytes(200_000);

        using var content = new MemoryStream(bytes) { Position = 7 };
        await storage.WriteAsync(null!, "ProductExport/seekable_1", content, CancellationToken);

        // Akış çağırana aittir: yazdıktan sonra da kullanılabilir (dışa aktarma uzunluğunu sonradan okur).
        Assert.Equal((bytes.LongLength, bytes.LongLength), (content.Length, content.Position));
        Assert.Equal(bytes.AsSpan(7).ToArray(), await minio.Plain.ReadObjectAsync("ProductExport/seekable_1"));

        // Aynı anahtara yazmak içeriği değiştirir.
        using var replacement = new MemoryStream([4, 5, 6]);
        await storage.WriteAsync(null!, "ProductExport/seekable_1", replacement, CancellationToken);
        Assert.Equal([4, 5, 6], await minio.Plain.ReadObjectAsync("ProductExport/seekable_1"));

        // Olmayan içerik null'dır; silmek yinelenebilir.
        await storage.DeleteAsync(null!, "ProductExport/seekable_1", CancellationToken);
        await storage.DeleteAsync(null!, "ProductExport/seekable_1", CancellationToken);
        Assert.Null(await storage.OpenReadAsync(null!, "ProductExport/seekable_1", CancellationToken));

        // Adresin anlamını değiştirebilecek bir anahtar hiçbir koşulda kabul edilmez.
        foreach (var key in new[] { "../outside", "a/../../b", "/etc/passwd", "a//b", "a/b.txt", "a?uploads", "a%2Fb", "a b", "" })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.OpenReadAsync(null!, key, CancellationToken));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.DeleteAsync(null!, key, CancellationToken));
        }
    }

    [Fact]
    public async Task Https_StreamsSeekableContentWithAnUnsignedPayload()
    {
        var storage = minio.CreateStorage(minio.Secure.Configure, minio.Secure.CreateHandler);
        var bytes = RandomNumberGenerator.GetBytes(300_000);

        using (var content = new MemoryStream(bytes))
        {
            await storage.WriteAsync(null!, "ProductExport/over_tls", content, CancellationToken);
        }

        Assert.Equal(bytes, await minio.Secure.ReadObjectAsync("ProductExport/over_tls"));

        // Uzunluğu bilinmeyen içerik HTTPS'te de özeti imzalanarak yazılır.
        await storage.WriteAsync(null!, "ProductExport/over_tls_buffered", new ForwardOnlyStream(bytes), CancellationToken);
        Assert.Equal(bytes, await minio.Secure.ReadObjectAsync("ProductExport/over_tls_buffered"));

        await using var read = await storage.OpenReadAsync(null!, "ProductExport/over_tls", CancellationToken);
        Assert.NotNull(read);
        Assert.Equal(SHA256.HashData(bytes), await SHA256.HashDataAsync(read, CancellationToken));
    }

    [Fact]
    public async Task VirtualHostStyle_MovesTheBucketIntoTheHostName()
    {
        // Sunucu kovayı alan adından okuyacak biçimde çalışır (MINIO_DOMAIN); ad çözümlemesi yerine bağlantı
        // doğrudan konteynere açılır.
        var storage = minio.CreateStorage(
            options =>
            {
                minio.Plain.Configure(options);
                options.ServiceUrl = new Uri($"http://{MinioFixture.Domain}:9000");
                options.ForcePathStyle = false;
                options.KeyPrefix = "virtual";
            },
            minio.Plain.CreateHandler);

        var bytes = RandomNumberGenerator.GetBytes(100_000);
        await storage.WriteAsync(null!, "MediaFile/hosted", new ForwardOnlyStream(bytes), CancellationToken);
        Assert.Equal(["virtual/MediaFile/hosted"], await minio.Plain.ListKeysAsync("virtual/"));

        await using (var read = await storage.OpenReadAsync(null!, "MediaFile/hosted", CancellationToken))
        {
            Assert.NotNull(read);
            Assert.Equal(SHA256.HashData(bytes), await SHA256.HashDataAsync(read, CancellationToken));
        }

        await storage.DeleteAsync(null!, "MediaFile/hosted", CancellationToken);
        Assert.Empty(await minio.Plain.ListKeysAsync("virtual/"));
    }

    [Fact]
    public void Options_AreValidatedWhenTheStorageIsFirstUsed()
    {
        var invalid = new Action<HaldusS3StorageOptions>[]
        {
            options => options.Bucket = "",
            options => options.Bucket = "Has_Upper",
            options => options.SecretKey = "",
            options => options.AccessKey = "",
            options => options.Region = " ",
            options => options.PartSize = 1024,
            options => options.KeyPrefix = "a/../b",
            options => options.ServiceUrl = new Uri("http://localhost:9000/base"),
            options => options.ServiceUrl = new Uri("ftp://localhost"),
            options => options.ServiceUrl = new Uri("relative", UriKind.Relative),
        };

        foreach (var adjust in invalid)
        {
            // Kaydetmek hata vermez (uygulama, şema üretimi sırasında kimlik bilgisi olmadan da açılır); ilk kullanım verir.
            var failure = Assert.Throws<InvalidOperationException>(() => minio.CreateStorage(options =>
            {
                minio.Plain.Configure(options);
                adjust(options);
            }));

            Assert.StartsWith("The S3 storage is not configured correctly", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(minio.Plain.SecretKey, failure.ToString(), StringComparison.Ordinal);
        }

        // Adres verilmezse AWS'nin bölgedeki adresi kullanılır; kayıt, önceki deponun yerine geçer.
        var services = new ServiceCollection();
        services.AddSingleton<IHaldusFileStorage, HaldusDatabaseFileStorage>();
        services.AddHaldusS3Storage(options =>
        {
            options.Bucket = "bucket";
            options.AccessKey = "key";
            options.SecretKey = "secret";
        });

        using var provider = services.BuildServiceProvider();
        Assert.IsType<HaldusS3Storage>(Assert.Single(provider.GetServices<IHaldusFileStorage>()));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var application in _applications)
        {
            await application.DisposeAsync();
        }
    }

    private HttpClient CreateClient(Action<HaldusS3StorageOptions> configure)
    {
        var application = fixture.CreateFactory(services => services.AddHaldusS3Storage(configure));
        _applications.Add(application);
        return application.CreateClient();
    }

    private async Task<string> StorageKeyAsync(Guid id)
    {
        var key = string.Empty;

        await fixture.WithDbContextAsync(async context =>
            key = (await context.Set<MediaFile>().IgnoreQueryFilters().SingleAsync(file => file.Id == id, CancellationToken)).StorageKey!);

        return key;
    }

    private static ByteArrayContent Part(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        return content;
    }

    private async Task<MediaFileResponse> CreateAsync(HttpClient client, string fileName) =>
        await ReadAsync<MediaFileResponse>(await SendAsync(client, HttpMethod.Post, "/media-files", JsonContent.Create(new { fileName })));

    private Task<HttpResponseMessage> UploadAsync(HttpClient client, string url, byte[] bytes) =>
        SendAsync(client, HttpMethod.Put, url, Part(bytes));

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add(DemoIdentity.TenantHeader, _tenant.ToString());
        return await client.SendAsync(request, CancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken)}");

        return (await response.Content.ReadFromJsonAsync<T>(CancellationToken))!;
    }

    private sealed class ContentRejectedException() : Exception("rejected while reading");

    /// <summary>İstek gövdesi gibi: uzunluğu bilinmez, başa sarılamaz; istenirse belirli bir bayttan sonra okuması kesilir.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes, int? failAfter = null) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= failAfter)
            {
                throw new ContentRejectedException();
            }

            // Gerçek bir gövde gibi küçük parçalarla gelir; depo parçayı kendisi doldurmalıdır.
            var count = Math.Min(Math.Min(buffer.Length, 60_000), (failAfter ?? bytes.Length) - _position);
            count = Math.Min(count, bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// İki MinIO sunucusu: biri düz HTTP ile (ve kovayı alan adından da okuyacak biçimde), öteki testin ürettiği bir
/// sertifikayla HTTPS üzerinden. Kimlik bilgileri her çalıştırmada rastgele üretilir.
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string Bucket = "haldus-tests";
    public const string Domain = "minio.test";

    // Varsayılan (us-east-1) dışında bir bölge: imzaya ayarlardaki bölgenin girdiği böylece sınanmış olur.
    private const string Region = "eu-test-1";

    // MinIO'nun resmi imajı (minio/minio) Docker Hub'dan kaldırıldı; Chainguard'ın derlediği imaj kullanılır.
    private static readonly string Image = Environment.GetEnvironmentVariable("HALDUS_TEST_MINIO_IMAGE") ?? "cgr.dev/chainguard/minio:latest";

    private readonly List<ServiceProvider> _providers = [];

    public MinioFixture()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

        Plain = new Server(certificate: null, privateKey: null);
        Secure = new Server(certificate, key.ExportPkcs8PrivateKeyPem());
    }

    public Server Plain { get; }

    public Server Secure { get; }

    /// <summary>Uygulamanın kuracağı gibi, servislerden kurulan bir depo.</summary>
    public IHaldusFileStorage CreateStorage(Action<HaldusS3StorageOptions> configure, Func<SocketsHttpHandler>? handler = null)
    {
        var services = new ServiceCollection();
        services.AddHaldusS3Storage(configure);

        if (handler is not null)
        {
            services.AddHttpClient(HaldusS3Storage.HttpClientName).ConfigurePrimaryHttpMessageHandler(handler);
        }

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IHaldusFileStorage>();
    }

    public async ValueTask InitializeAsync() => await Task.WhenAll(Plain.StartAsync(), Secure.StartAsync());

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        await Plain.DisposeAsync();
        await Secure.DisposeAsync();
    }

    public sealed class Server : IAsyncDisposable
    {
        private readonly IContainer _container;
        private readonly string? _thumbprint;
        private HttpClient? _http;

        internal Server(X509Certificate2? certificate, string? privateKey)
        {
            _thumbprint = certificate?.GetCertHashString(HashAlgorithmName.SHA256);

            var builder = new ContainerBuilder(Image)
                .WithPortBinding(9000, assignRandomHostPort: true)
                .WithEnvironment("MINIO_ROOT_USER", AccessKey)
                .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
                .WithEnvironment("MINIO_SITE_REGION", Region)
                .WithEnvironment("MINIO_DOMAIN", Domain);

            _container = (certificate is null
                    ? builder.WithCommand("server", "/data")
                    : builder
                        .WithResourceMapping(Encoding.ASCII.GetBytes(certificate.ExportCertificatePem()), FilePath.Of("/certs/public.crt"))
                        .WithResourceMapping(Encoding.ASCII.GetBytes(privateKey!), FilePath.Of("/certs/private.key"))
                        .WithCommand("server", "--certs-dir", "/certs", "/data"))
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                    .ForPort(9000)
                    .ForPath("/minio/health/live")
                    .UsingTls(certificate is not null)
                    .UsingHttpMessageHandler(CreateHandler())))
                .Build();
        }

        public string AccessKey { get; } = "haldus" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

        public string SecretKey { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

        private Uri Endpoint => new($"{(_thumbprint is null ? "http" : "https")}://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}");

        public void Configure(HaldusS3StorageOptions options)
        {
            options.ServiceUrl = Endpoint;
            options.Region = Region;
            options.Bucket = Bucket;
            options.AccessKey = AccessKey;
            options.SecretKey = SecretKey;
        }

        /// <summary>
        /// Bağlantıyı, adresteki ad ne olursa olsun bu sunucuya açar ve yalnızca testin ürettiği sertifikayı kabul eder.
        /// </summary>
        public SocketsHttpHandler CreateHandler()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

                    try
                    {
                        await socket.ConnectAsync(_container.Hostname, _container.GetMappedPublicPort(9000), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };

            handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
                presented is not null && _thumbprint is not null && presented.GetCertHashString(HashAlgorithmName.SHA256) == _thumbprint;

            return handler;
        }

        internal async Task StartAsync()
        {
            await _container.StartAsync();
            _http = new HttpClient(CreateHandler());

            using var created = await SendAsync(HttpMethod.Put, $"/{Bucket}");
            Assert.True(created.IsSuccessStatusCode, $"{(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        }

        public async Task<byte[]?> ReadObjectAsync(string key)
        {
            using var response = await SendAsync(HttpMethod.Get, $"/{Bucket}/{key}");

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task<string> ETagAsync(string key)
        {
            using var response = await SendAsync(HttpMethod.Head, $"/{Bucket}/{key}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return response.Headers.ETag!.Tag;
        }

        public Task<List<string>> ListKeysAsync(string prefix = "") =>
            ListAsync($"/{Bucket}?list-type=2&prefix={Uri.EscapeDataString(prefix)}", "Key");

        /// <summary>Başlatılıp tamamlanmamış çok parçalı yüklemelerin anahtarları.</summary>
        public Task<List<string>> ListPendingUploadsAsync() => ListAsync($"/{Bucket}?uploads", "Upload");

        private async Task<List<string>> ListAsync(string address, string element)
        {
            using var response = await SendAsync(HttpMethod.Get, address);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

            return XDocument.Parse(body).Descendants().Where(node => node.Name.LocalName == element).Select(node => node.Value).Order(StringComparer.Ordinal).ToList();
        }

        // Kovayı hazırlamak ve içine bakmak için, depodan bağımsız imzalı istekler.
        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string address)
        {
            using var request = new HttpRequestMessage(method, new Uri(Endpoint, address));
            S3Signer.Sign(request, S3Signer.HashOf([]), AccessKey, SecretKey, Region, DateTimeOffset.UtcNow);

            return await (_http ?? throw new InvalidOperationException("The server has not been started.")).SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _http?.Dispose();
            await _container.DisposeAsync();
        }
    }
}
