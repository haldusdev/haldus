using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Haldus.Sample.Shop.Features.Content;
using Haldus.Sample.Shop.Features.Supply;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus.IntegrationTests;

/// <summary>Dosyalar (<c>MapFile</c>) ve takılabilirler (<c>AttachedTo</c>, <c>Attachable</c>, <c>Commentable</c>, <c>Taggable</c>).</summary>
[Collection(ShopCollection.Name)]
public sealed class FileEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ── Dosya ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task File_IsCreatedFirstAndItsContentUploadedAfterwards()
    {
        var article = await CreateArticleAsync();
        var files = $"/articles/{article.Id}/attachments";

        // Kayıt içeriksiz doğar; boyut, tür ve durum istekle yazılamaz.
        var created = await SendAsync(HttpMethod.Post, files, new { fileName = "notes.txt", caption = "Meeting", size = 999, status = "Uploaded", storageKey = "x/y" });
        var file = await ReadAsync<AttachmentResponse>(created);
        var url = $"{files}/{file.Id}";

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal((FileStatus.Pending, 0L, null, null, article.Id), (file.Status, file.Size, file.ContentType, file.Checksum, file.ArticleId));
        Assert.DoesNotContain("storageKey", await created.Content.ReadAsStringAsync(CancellationToken), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{url}/content")).StatusCode);

        var bytes = Encoding.UTF8.GetBytes("hello, haldus");
        var uploaded = await ReadAsync<AttachmentResponse>(await UploadAsync($"{url}/content", bytes, "text/plain; charset=utf-8"));

        Assert.Equal(
            (FileStatus.Uploaded, (long)bytes.Length, "text/plain", Convert.ToHexStringLower(SHA256.HashData(bytes))),
            (uploaded.Status, uploaded.Size, uploaded.ContentType, uploaded.Checksum));
        Assert.NotNull(uploaded.UploadedAt);

        // İçerik, yükleyenin bildirdiği türle ama tarayıcıda açılmayacak biçimde iner.
        var download = await SendAsync(HttpMethod.Get, $"{url}/content");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync(CancellationToken));
        Assert.Equal("text/plain", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("notes.txt", download.Content.Headers.ContentDisposition.FileNameStar ?? download.Content.Headers.ContentDisposition.FileName);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal($"\"{uploaded.Checksum}\"", download.Headers.ETag!.Tag);

        // İçeriğin bir parçası da istenebilir.
        using var ranged = new HttpRequestMessage(HttpMethod.Get, $"{url}/content");
        ranged.Headers.Range = new RangeHeaderValue(0, 4);
        var part = await _client.SendAsync(ranged, CancellationToken);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("hello", await part.Content.ReadAsStringAsync(CancellationToken));

        // Yeniden yüklenen içerik eskisinin yerine geçer.
        var replaced = await ReadAsync<AttachmentResponse>(await UploadAsync($"{url}/content", Encoding.UTF8.GetBytes("v2"), "text/plain"));
        Assert.Equal(2L, replaced.Size);
        Assert.Equal("v2", await (await SendAsync(HttpMethod.Get, $"{url}/content")).Content.ReadAsStringAsync(CancellationToken));

        // Kaydın kendi alanları sıradan bir kaynak gibi yazılır.
        var renamed = await ReadAsync<AttachmentResponse>(await SendAsync(HttpMethod.Patch, url, new { fileName = "minutes.txt" }));
        Assert.Equal(("minutes.txt", "Meeting", 2L), (renamed.FileName, renamed.Caption, renamed.Size));
    }

    [Fact]
    public async Task File_RejectsContentItDoesNotAccept()
    {
        var article = await CreateArticleAsync();
        var file = await ReadAsync<AttachmentResponse>(await SendAsync(HttpMethod.Post, $"/articles/{article.Id}/attachments", new { fileName = "big.bin" }));
        var url = $"/articles/{article.Id}/attachments/{file.Id}/content";

        var wrongType = await UploadAsync(url, [1, 2, 3], "application/zip");
        var tooLarge = await UploadAsync(url, new byte[1024 * 1024 + 1], "text/plain");
        var empty = await UploadAsync(url, [], "text/plain");
        var turkish = await UploadAsync(url, [1], "application/zip", language: "tr");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("Dosya türüne izin verilmiyor", (await turkish.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Title);

        // Reddedilen içerik kayda hiçbir şey yazmaz.
        var unchanged = await ReadAsync<AttachmentResponse>(await SendAsync(HttpMethod.Get, $"/articles/{article.Id}/attachments/{file.Id}"));
        Assert.Equal((FileStatus.Pending, 0L), (unchanged.Status, unchanged.Size));

        // Jokerli tür kabul edilir; tam sınırdaki içerik de.
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(url, new byte[1024 * 1024], "image/png")).StatusCode);

        // Reddedilen bir yükleme var olan içeriğe dokunmaz.
        var kept = await ReadAsync<AttachmentResponse>(await UploadAsync(url, Encoding.UTF8.GetBytes("keep me"), "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(url, [], "text/plain")).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await UploadAsync(url, new byte[1024 * 1024 + 1], "text/plain")).StatusCode);

        var after = await ReadAsync<AttachmentResponse>(await SendAsync(HttpMethod.Get, $"/articles/{article.Id}/attachments/{file.Id}"));
        Assert.Equal((kept.Size, kept.Checksum), (after.Size, after.Checksum));
        Assert.Equal("keep me", await (await SendAsync(HttpMethod.Get, url)).Content.ReadAsStringAsync(CancellationToken));

        // Depoda yalnızca geçerli içerik durur: yerine geçilen ve reddedilen içerikler kalmaz.
        await fixture.WithDbContextAsync(async context =>
        {
            var key = (await context.Set<Attachment>().SingleAsync(item => item.Id == file.Id, CancellationToken)).StorageKey!;
            var stored = await context.Set<HaldusFileContent>().Where(content => content.Key.StartsWith("Attachment/")).Select(content => content.Key).ToListAsync(CancellationToken);

            Assert.Contains(key, stored);
            Assert.Equal(await context.Set<Attachment>().CountAsync(item => item.StorageKey != null, CancellationToken), stored.Count);
        });

        // Dosya adı zorunludur.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, $"/articles/{article.Id}/attachments", new { caption = "nameless" })).StatusCode);
    }

    [Fact]
    public async Task File_DeletedRecordTakesItsContentWithIt()
    {
        var article = await CreateArticleAsync();
        var file = await ReadAsync<AttachmentResponse>(await SendAsync(HttpMethod.Post, $"/articles/{article.Id}/attachments", new { fileName = "gone.txt" }));
        var url = $"/articles/{article.Id}/attachments/{file.Id}";
        await ReadAsync<AttachmentResponse>(await UploadAsync($"{url}/content", [1, 2, 3], "text/plain"));

        var key = string.Empty;
        await fixture.WithDbContextAsync(async context =>
        {
            key = (await context.Set<Attachment>().SingleAsync(item => item.Id == file.Id, CancellationToken)).StorageKey!;
            Assert.True(await context.Set<HaldusFileContent>().AnyAsync(content => content.Key == key, CancellationToken));
        });

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{url}/content")).StatusCode);

        await fixture.WithDbContextAsync(async context =>
            Assert.False(await context.Set<HaldusFileContent>().AnyAsync(content => content.Key == key, CancellationToken)));
    }

    [Fact]
    public async Task FileSystemStorage_WritesReadsAndDeletesUnderItsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "haldus-tests-" + Guid.NewGuid().ToString("N"));
        var storage = new HaldusFileSystemStorage(root);

        try
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("on disk"));
            await storage.WriteAsync(null!, "Attachment/abc123", content, CancellationToken);

            await using (var read = await storage.OpenReadAsync(null!, "Attachment/abc123", CancellationToken))
            {
                Assert.NotNull(read);
                using var reader = new StreamReader(read);
                Assert.Equal("on disk", await reader.ReadToEndAsync(CancellationToken));
            }

            // Yazma bitmeden yerine taşınmaz; geçici dosya geride kalmaz.
            Assert.Single(Directory.GetFiles(Path.Combine(root, "Attachment")));

            await storage.DeleteAsync(null!, "Attachment/abc123", CancellationToken);
            Assert.Null(await storage.OpenReadAsync(null!, "Attachment/abc123", CancellationToken));

            // Klasörün dışını gösteren bir anahtar hiçbir koşulda kabul edilmez.
            foreach (var key in new[] { "../outside", "a/../../b", "/etc/passwd", "a//b", "C:\\x", "a/b.txt" })
            {
                await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync(null!, key, CancellationToken));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ── Takılabilirler ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AttachedRecord_LivesUnderEachOfItsHostsAndBelongsToExactlyOne()
    {
        var article = await CreateArticleAsync();
        var other = await CreateArticleAsync();
        var supplier = await ReadAsync<SupplierResponse>(await SendAsync(HttpMethod.Post, "/suppliers", new { name = "Host" }));

        var onArticle = await ReadAsync<CommentResponse>(await SendAsync(HttpMethod.Post, $"/articles/{article.Id}/comments", new { text = "On the article" }));
        var onSupplier = await ReadAsync<CommentResponse>(await SendAsync(HttpMethod.Post, $"/suppliers/{supplier.Id}/comments", new { text = "On the supplier" }));

        // Kayıt takıldığı kaydı gösterir; öteki hedefin alanı boştur.
        Assert.Equal((article.Id, null), (onArticle.ArticleId, onArticle.SupplierId));
        Assert.Equal((null, supplier.Id), (onSupplier.ArticleId, onSupplier.SupplierId));

        // Her hedef yalnızca kendi kayıtlarını listeler; başka bir hedefin adresinden kayıt bulunamaz.
        var articleComments = await ReadAsync<PagedResult<CommentResponse>>(await SendAsync(HttpMethod.Get, $"/articles/{article.Id}/comments"));
        var supplierComments = await ReadAsync<PagedResult<CommentResponse>>(await SendAsync(HttpMethod.Get, $"/suppliers/{supplier.Id}/comments"));
        Assert.Equal([onArticle.Id], articleComments.Items.Select(item => item.Id));
        Assert.Null(onArticle.ParentId);
        Assert.Equal([onSupplier.Id], supplierComments.Items.Select(item => item.Id));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/articles/{other.Id}/comments/{onArticle.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/suppliers/{supplier.Id}/comments/{onArticle.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/articles/{article.Id}/comments/{onArticle.Id}")).StatusCode);

        // Hedef yoksa kayıt da yazılamaz; hedef istekle seçilemez.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/articles/{Guid.NewGuid()}/comments", new { text = "Nowhere" })).StatusCode);
        var forged = await ReadAsync<CommentResponse>(await SendAsync(
            HttpMethod.Post, $"/articles/{article.Id}/comments", new { text = "Forged", supplierId = supplier.Id, articleId = other.Id }));
        Assert.Equal((article.Id, null), (forged.ArticleId, forged.SupplierId));

        // Yorumlar birbirine yanıt olabilir; ama yanıtlanan yorum aynı kayda takılı olmalıdır.
        var reply = await ReadAsync<CommentResponse>(await SendAsync(
            HttpMethod.Post, $"/articles/{article.Id}/comments", new { text = "A reply", parentId = onArticle.Id }));
        Assert.Equal((onArticle.Id, article.Id), (reply.ParentId, reply.ArticleId));

        var crossed = await SendAsync(HttpMethod.Post, $"/articles/{article.Id}/comments", new { text = "Crossed", parentId = onSupplier.Id });
        Assert.Equal(HttpStatusCode.BadRequest, crossed.StatusCode);
        Assert.Equal(["parentId"], (await crossed.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!.Errors.Keys);

        var replies = await ReadAsync<PagedResult<CommentResponse>>(await SendAsync(HttpMethod.Get, $"/articles/{article.Id}/comments?parentId={onArticle.Id}"));
        Assert.Equal([reply.Id], replies.Items.Select(item => item.Id));

        // Yorum yalnızca eklenir.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(HttpMethod.Put, $"/articles/{article.Id}/comments/{onArticle.Id}", new { text = "Edited" })).StatusCode);

        // Veritabanı da tam olarak bir hedef ister.
        await fixture.WithDbContextAsync(async context =>
        {
            var orphan = new Comment { Id = Guid.NewGuid(), Text = "No host" };
            context.Add(orphan);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(CancellationToken));
        });

        // Takıldığı kayıt silinince takılı kayıtlar da gider; öteki hedefinkiler kalır.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/articles/{article.Id}")).StatusCode);

        await fixture.WithDbContextAsync(async context =>
        {
            Assert.False(await context.Set<Comment>().IgnoreQueryFilters().AnyAsync(comment => comment.ArticleId == article.Id, CancellationToken));
            Assert.True(await context.Set<Comment>().IgnoreQueryFilters().AnyAsync(comment => comment.Id == onSupplier.Id, CancellationToken));
        });
    }

    [Fact]
    public async Task Taggable_LinksRecordsToASharedSetOfTags()
    {
        var article = await CreateArticleAsync();
        var first = $"tag-{Guid.NewGuid():N}";
        var second = $"tag-{Guid.NewGuid():N}";

        foreach (var code in new[] { first, second })
        {
            await ReadAsync<LabelResponse>(await SendAsync(HttpMethod.Put, $"/labels/{code}", new { name = code }));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, $"/articles/{article.Id}/labels/{first}")).StatusCode);

        // Etiketler topluca da değiştirilir.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, $"/articles/{article.Id}/labels", new { ids = new[] { first, second } })).StatusCode);

        var labels = await ReadAsync<List<LabelResponse>>(await SendAsync(HttpMethod.Get, $"/articles/{article.Id}/labels"), paged: true);
        Assert.Equal(new[] { first, second }.Order(), labels.Select(label => label.Code).Order());

        var tagged = await ReadAsync<List<ArticleResponse>>(await SendAsync(HttpMethod.Get, $"/labels/{first}/articles"), paged: true);
        Assert.Contains(tagged, item => item.Id == article.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/articles/{article.Id}/labels/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/articles/{article.Id}/labels/{first}")).StatusCode);
    }

    [Fact]
    public void Taggable_AppliesTheConventionsOfTheResourceToItsTagEndpoints()
    {
        // Yetkilendirme gibi kurallar kaynağın builder'ına eklenir; etiket bağının uçları kaynağın adresinin
        // dışında eşlense de aynı kuralları almalıdır, zincirde Taggable'dan önce de sonra da yazılsa.
        foreach (var before in new[] { true, false })
        {
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            builder.Services.AddAuthorization();
            builder.Services.AddDbContext<Haldus.Sample.Shop.AppDbContext>(options => options.UseNpgsql("Host=unused"));
            builder.Services.AddHaldus<Haldus.Sample.Shop.AppDbContext>();

            using var app = builder.Build();
            app.MapLookup<Label>();

            if (before)
            {
                app.MapCrud<Article>().RequireAuthorization().Taggable<Label>();
            }
            else
            {
                app.MapCrud<Article>().Taggable<Label>().RequireAuthorization();
            }

            var endpoints = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
                .ToList();

            var tagging = endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.Contains("/labels", StringComparison.Ordinal)
                && endpoint.RoutePattern.RawText.Contains("articles", StringComparison.Ordinal)).ToList();
            var lookup = endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/labels", StringComparison.Ordinal)
                && !endpoint.RoutePattern.RawText.Contains("articles", StringComparison.Ordinal)).ToList();

            Assert.NotEmpty(tagging);
            Assert.All(tagging, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()));

            // Kural yalnızca o kaynağın uçlarına uygulanır; etiket kaynağının kendi uçları ona kalmıştır.
            Assert.NotEmpty(lookup);
            Assert.All(lookup, endpoint => Assert.Null(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()));
        }
    }

    private async Task<ArticleResponse> CreateArticleAsync() =>
        await ReadAsync<ArticleResponse>(await SendAsync(
            HttpMethod.Post, "/articles", new { title = "Host article", slug = $"article-{Guid.NewGuid():N}" }));

    private async Task<HttpResponseMessage> UploadAsync(string url, byte[] bytes, string contentType, string? language = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, bool paged = false)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken)}");

        if (paged)
        {
            // Liste yanıtının öğeleri; sayfalanmış ya da düz liste olabilir.
            var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(CancellationToken);
            var items = json.ValueKind == System.Text.Json.JsonValueKind.Array ? json : json.GetProperty("items");
            return System.Text.Json.JsonSerializer.Deserialize<T>(items.GetRawText(), System.Text.Json.JsonSerializerOptions.Web)!;
        }

        return (await response.Content.ReadFromJsonAsync<T>(CancellationToken))!;
    }
}
