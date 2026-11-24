using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Content;

namespace Haldus.IntegrationTests;

/// <summary>Dosyalarda multipart yükleme ve süreli indirme bağlantıları (<c>SignedLinks</c>).</summary>
[Collection(ShopCollection.Name)]
public sealed class SharedFileEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Content_CanBeUploadedAsTheFilePartOfAMultipartForm()
    {
        var file = await CreateAsync("notes.txt");
        var url = $"/media-files/{file.Id}/content";
        var bytes = Encoding.UTF8.GetBytes("multipart upload");

        // Formun dosya parçası içeriktir; ondan önceki alanlar atlanır ve türü parçanın kendi türüdür.
        using var form = new MultipartFormDataContent
        {
            { new StringContent("ignored"), "caption" },
            { Part(bytes, "text/plain"), "file", "notes.txt" },
        };

        var uploaded = await ReadAsync<MediaFileResponse>(await SendAsync(HttpMethod.Put, url, form));
        Assert.Equal((FileStatus.Uploaded, bytes.LongLength, "text/plain"), (uploaded.Status, uploaded.Size, uploaded.ContentType));

        var downloaded = await SendAsync(HttpMethod.Get, url);
        Assert.Equal(bytes, await downloaded.Content.ReadAsByteArrayAsync(CancellationToken));
        Assert.Equal("text/plain", downloaded.Content.Headers.ContentType?.MediaType);

        // Gövde olarak yüklenen aynı içerik aynı özeti verir: form yalnızca bir taşıma biçimidir.
        var raw = await CreateAsync("raw.txt");
        using var body = Part(bytes, "text/plain");
        var direct = await ReadAsync<MediaFileResponse>(await SendAsync(HttpMethod.Put, $"/media-files/{raw.Id}/content", body));
        Assert.Equal(direct.Checksum, uploaded.Checksum);

        // Sınırlar formda da geçerlidir: tür parçanın türüne, boyut parçanın boyutuna bakar.
        using var wrongType = new MultipartFormDataContent { { Part(bytes, "application/zip"), "file", "a.zip" } };
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await SendAsync(HttpMethod.Put, url, wrongType)).StatusCode);

        using var tooLarge = new MultipartFormDataContent { { Part(new byte[64 * 1024 + 1], "text/plain"), "file", "big.txt" } };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await SendAsync(HttpMethod.Put, url, tooLarge)).StatusCode);

        using var noFile = new MultipartFormDataContent { { new StringContent("only a field"), "caption" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, url, noFile)).StatusCode);

        // Reddedilen yüklemeler var olan içeriğe dokunmaz.
        Assert.Equal(bytes, await (await SendAsync(HttpMethod.Get, url)).Content.ReadAsByteArrayAsync(CancellationToken));
    }

    [Fact]
    public async Task SignedLink_OpensTheContentWithoutASessionUntilItExpiresOrTheContentChanges()
    {
        var file = await CreateAsync("shared.txt");
        var url = $"/media-files/{file.Id}";

        // İçeriği olmayan dosyanın bağlantısı da olmaz.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"{url}/content/link")).StatusCode);

        using var first = Part(Encoding.UTF8.GetBytes("first version"), "text/plain");
        await ReadAsync<MediaFileResponse>(await SendAsync(HttpMethod.Put, $"{url}/content", first));

        var link = await ReadAsync<FileLink>(await SendAsync(HttpMethod.Post, $"{url}/content/link?expiresInSeconds=600"));
        Assert.StartsWith("/media-files/shared-content/", link.Url);
        Assert.InRange(link.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(11));

        // Bağlantıyı taşıyanın oturumu ve kiracısı yoktur; onu bağlantının kendisi tanıtır.
        var shared = await SendAsync(HttpMethod.Get, link.Url, tenant: null);
        Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        Assert.Equal("first version", await shared.Content.ReadAsStringAsync(CancellationToken));
        Assert.Equal("attachment", shared.Content.Headers.ContentDisposition?.DispositionType);

        // Kaydın kendisi ise oturumsuz ve başka kiracıya kapalıdır.
        Assert.NotEqual(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{url}/content", tenant: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{url}/content", tenant: Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"{url}/content/link", tenant: Guid.NewGuid())).StatusCode);

        // Bozuk veya uydurma bir bağlantı hiçbir şeyi açmaz.
        var token = link.Url.Substring(link.Url.LastIndexOf('/') + 1);
        var tampered = token.Substring(0, token.Length - 2) + (token.EndsWith("AA", StringComparison.Ordinal) ? "BB" : "AA");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/media-files/shared-content/{tampered}", tenant: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/media-files/shared-content/not-a-link", tenant: null)).StatusCode);

        // İçerik değişince eski bağlantı geçersiz olur; yeni içerik için yeni bağlantı gerekir.
        using var second = Part(Encoding.UTF8.GetBytes("second version"), "text/plain");
        await ReadAsync<MediaFileResponse>(await SendAsync(HttpMethod.Put, $"{url}/content", second));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, link.Url, tenant: null)).StatusCode);

        var renewed = await ReadAsync<FileLink>(await SendAsync(HttpMethod.Post, $"{url}/content/link"));
        Assert.Equal("second version", await (await SendAsync(HttpMethod.Get, renewed.Url, tenant: null)).Content.ReadAsStringAsync(CancellationToken));

        // Süresi dolan bağlantı da geçersizdir.
        var brief = await ReadAsync<FileLink>(await SendAsync(HttpMethod.Post, $"{url}/content/link?expiresInSeconds=1"));
        await Task.Delay(TimeSpan.FromSeconds(2.2), CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, brief.Url, tenant: null)).StatusCode);

        // Kayıt silinince bağlantısı da ölür.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, renewed.Url, tenant: null)).StatusCode);
    }

    private static ByteArrayContent Part(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    private async Task<MediaFileResponse> CreateAsync(string fileName) =>
        await ReadAsync<MediaFileResponse>(await SendAsync(HttpMethod.Post, "/media-files", JsonContent.Create(new { fileName })));

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content = null) =>
        SendAsync(method, url, content, _tenant);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid? tenant) =>
        SendAsync(method, url, content: null, tenant);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, Guid? tenant)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };

        if (tenant is { } tenantId)
        {
            request.Headers.Add(DemoIdentity.TenantHeader, tenantId.ToString());
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken)}");

        return (await response.Content.ReadFromJsonAsync<T>(CancellationToken))!;
    }
}
