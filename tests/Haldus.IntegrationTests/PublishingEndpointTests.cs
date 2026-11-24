using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop.Features.Content;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

/// <summary>Taslak ve yayın (<c>Publishable</c>).</summary>
[Collection(ShopCollection.Name)]
public sealed class PublishingEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publish_KeepsALiveCopyWhileTheDraftIsEdited()
    {
        var slug = $"article-{Guid.NewGuid():N}";
        var draft = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, "/articles", new
        {
            title = "Launch",
            slug,
            body = "v1",
            articleSections = new[] { new { heading = "Why" }, new { heading = "How" } },
        }));
        var url = $"/articles/{draft.Id}";

        // Yayınlanmamış taslak yayında görünmez.
        Assert.Null(draft.PublishedAt);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/articles/published/{draft.Id}")).StatusCode);

        var published = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, $"{url}/publish"));
        Assert.NotNull(published.PublishedAt);
        Assert.Equal(draft.Id, published.Id);

        // Yayındaki hal taslağın kimliğiyle okunur ve alanlarını, satırlarını, hesaplanan alanlarını taşır.
        var live = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"/articles/published/{draft.Id}"));
        Assert.Equal((draft.Id, "Launch", "v1", 2), (live.Id, live.Title, live.Body, live.SectionCount));
        Assert.Equal(["Why", "How"], live.ArticleSections.Select(section => section.Heading));

        // Taslak değişir; yayındaki hal bir sonraki yayına kadar aynı kalır.
        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Put, url, new
        {
            title = "Launch (rewritten)",
            slug,
            body = "v2",
            articleSections = new[] { new { heading = "Only" } },
        }));

        var stillLive = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"/articles/published/{draft.Id}"));
        Assert.Equal(("Launch", "v1", 2), (stillLive.Title, stillLive.Body, stillLive.SectionCount));

        var republished = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, $"{url}/publish"));
        var updated = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"/articles/published/{draft.Id}"));
        Assert.True(republished.PublishedAt > published.PublishedAt);
        Assert.Equal(("Launch (rewritten)", "v2", 1), (updated.Title, updated.Body, updated.SectionCount));
        Assert.Equal(["Only"], updated.ArticleSections.Select(section => section.Heading));

        var list = await ReadAsync<PagedResult<ArticleResponse>>(await SendAsync(HttpMethod.Get, "/articles/published?pageSize=100"));
        Assert.Equal("Launch (rewritten)", Assert.Single(list.Items, item => item.Id == draft.Id).Title);
    }

    [Fact]
    public async Task PublishedCopy_IsInvisibleToEverythingButThePublishedEndpoints()
    {
        var slug = $"article-{Guid.NewGuid():N}";
        var draft = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, "/articles", new { title = "Hidden copy", slug }));
        var cursor = await CursorAsync();
        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, $"/articles/{draft.Id}/publish"));

        var copyId = Guid.Empty;
        await fixture.WithDbContextAsync(async context =>
        {
            var copies = await context.Set<Article>()
                .IgnoreQueryFilters([HaldusFilters.Publication])
                .Where(article => article.PublishedOfId == draft.Id)
                .ToListAsync(CancellationToken);

            copyId = Assert.Single(copies).Id;
        });

        // Kopyanın kendi anahtarı hiçbir uçta bir kayıt değildir.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/articles/{copyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Patch, $"/articles/{copyId}", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/articles/{copyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/articles/published/{copyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/articles/{copyId}/publish")).StatusCode);

        // Listede, doğal anahtarda ve değişiklik akışında yalnızca taslak vardır.
        var bySlug = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"/articles/by-slug/{slug}"));
        Assert.Equal(draft.Id, bySlug.Id);

        var changes = await ReadAsync<ChangeFeedPage<ArticleResponse>>(await SendAsync(HttpMethod.Get, $"/articles/changes?since={cursor}&pageSize=500"));
        Assert.DoesNotContain(changes.Items, item => item.Id == copyId.ToString());

        // Doğal anahtar taslaklar arasında benzersizdir; kopyanın aynı değeri taşıması yayını engellemez,
        // ama aynı değerle ikinci bir taslak yine oluşturulamaz.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/articles", new { title = "Twin", slug })).StatusCode);

        // Kopyanın sürümü saklanmaz; taslağın sürümleri yalnızca taslağın yazmalarıdır (oluşturma ve yayın).
        var revisions = await ReadAsync<PagedResult<RevisionInfo>>(await SendAsync(HttpMethod.Get, $"/articles/{draft.Id}/revisions"));
        Assert.Equal(2, revisions.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/articles/{copyId}/revisions")).StatusCode);
    }

    [Fact]
    public async Task PublishedRecord_MustBeUnpublishedBeforeItIsDeleted()
    {
        var draft = await ReadAsync<ArticleResponse>(await SendAsync(
            HttpMethod.Post, "/articles", new { title = "Retire me", slug = $"article-{Guid.NewGuid():N}" }));
        var url = $"/articles/{draft.Id}";

        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, $"{url}/publish"));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Delete, url)).StatusCode);

        var unpublished = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, $"{url}/unpublish"));
        Assert.Null(unpublished.PublishedAt);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/articles/published/{draft.Id}")).StatusCode);

        // Yayında olmayan kaydı yayından kaldırmak bir şey değiştirmez.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"{url}/unpublish")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/articles/{Guid.NewGuid()}/publish")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, url)).StatusCode);

        await fixture.WithDbContextAsync(async context =>
            Assert.False(await context.Set<Article>()
                .IgnoreQueryFilters()
                .AnyAsync(article => article.Id == draft.Id || article.PublishedOfId == draft.Id, CancellationToken)));
    }

    [Fact]
    public async Task Publish_Concurrently_LeavesOneCopy()
    {
        var draft = await ReadAsync<ArticleResponse>(await SendAsync(
            HttpMethod.Post, "/articles", new { title = "Raced", slug = $"article-{Guid.NewGuid():N}" }));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => SendAsync(HttpMethod.Post, $"/articles/{draft.Id}/publish")));

        // Yarışı kaybeden istek çakışma alır; hiçbiri sunucu hatasıyla sonuçlanmaz ve taslağın tek kopyası olur.
        Assert.All(responses, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);

        await fixture.WithDbContextAsync(async context =>
            Assert.Equal(1, await context.Set<Article>()
                .IgnoreQueryFilters([HaldusFilters.Publication])
                .CountAsync(article => article.PublishedOfId == draft.Id, CancellationToken)));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/articles/published/{draft.Id}")).StatusCode);
    }

    private async Task<long> CursorAsync()
    {
        var since = 0L;

        while (true)
        {
            var page = await ReadAsync<ChangeFeedPage<ArticleResponse>>(await SendAsync(HttpMethod.Get, $"/articles/changes?since={since}&pageSize=500"));
            since = page.Next;

            if (!page.HasMore)
            {
                return since;
            }
        }
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

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken)}");

        return (await response.Content.ReadFromJsonAsync<T>(CancellationToken))!;
    }
}