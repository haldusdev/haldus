using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop.Features.Content;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

/// <summary>Başka dillere çevrilebilen alanlar (<c>Translatable</c>).</summary>
[Collection(ShopCollection.Name)]
public sealed class TranslationEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Translations_AreWrittenPerLocaleAndReadOnRequest()
    {
        var article = await CreateAsync("Launch", "Hello");
        var url = $"/articles/{article.Id}";
        Assert.Empty(article.Translations);

        var turkish = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Put, $"{url}/translations/tr", new { title = "Lansman", body = "Merhaba" }));

        // Alanın kendisi kaynak dildeki metni taşımaya devam eder; çeviri yanında durur.
        Assert.Equal(("Launch", "Hello"), (turkish.Title, turkish.Body));
        Assert.Equal("Lansman", turkish.Translations["tr"]["title"]);
        Assert.Equal("Merhaba", turkish.Translations["tr"]["body"]);

        // Dil etiketi küçük harfe çevrilir; yalnızca çevrilen alan saklanır.
        var brazilian = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Put, $"{url}/translations/pt-BR", new { title = "Lançamento" }));
        Assert.Equal(new[] { "pt-br", "tr" }, brazilian.Translations.Keys.Order());
        Assert.Equal(["title"], brazilian.Translations["pt-br"].Keys);

        // İstenmedikçe okuma kaynak dildedir; böylece okuduğunu geri yazan istemci kaynak metni ezmez.
        var plain = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal(("Launch", "Hello"), (plain.Title, plain.Body));

        var inTurkish = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"{url}?locale=tr"));
        Assert.Equal(("Lansman", "Merhaba"), (inTurkish.Title, inTurkish.Body));

        // Bölgesel etiket daha genel dile düşer; çevrilmemiş alan kaynak dilde kalır; bilinmeyen dil kaynak dildir.
        var regional = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"{url}?locale=tr-TR"));
        var partial = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"{url}?locale=pt-br"));
        var unknown = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"{url}?locale=de"));
        Assert.Equal("Lansman", regional.Title);
        Assert.Equal(("Lançamento", "Hello"), (partial.Title, partial.Body));
        Assert.Equal(("Launch", "Hello"), (unknown.Title, unknown.Body));

        var list = await ReadAsync<PagedResult<ArticleResponse>>(await SendAsync(HttpMethod.Get, "/articles?locale=tr&pageSize=100"));
        Assert.Equal("Lansman", list.Items.Single(item => item.Id == article.Id).Title);

        // Kaynak metni değiştirmek çevirileri değiştirmez.
        var renamed = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Patch, url, new { title = "Launch day" }));
        Assert.Equal("Launch day", renamed.Title);
        Assert.Equal("Lansman", renamed.Translations["tr"]["title"]);

        // Yeniden yazmak o dilin çevirilerini tamamen değiştirir; boş bırakılan alanın çevirisi kalkar.
        var replaced = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Put, $"{url}/translations/tr", new { title = "Lansman günü" }));
        Assert.Equal(["title"], replaced.Translations["tr"].Keys);

        var removed = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Delete, $"{url}/translations/pt-BR"));
        Assert.Equal(["tr"], removed.Translations.Keys);

        // Hiçbir alanı kalmayan dil çantadan çıkar.
        var emptied = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Put, $"{url}/translations/tr", new { title = "" }));
        Assert.Empty(emptied.Translations);
    }

    [Fact]
    public async Task Translations_AreValidatedLikeTheFieldsTheyTranslate()
    {
        var article = await CreateAsync("Launch", "Hello");
        var url = $"/articles/{article.Id}";

        var badLocale = await SendAsync(HttpMethod.Put, $"{url}/translations/turkish!", new { title = "Lansman" });
        Assert.Equal(["locale"], await ErrorsAsync(badLocale));
        Assert.Equal(["locale"], await ErrorsAsync(await SendAsync(HttpMethod.Delete, $"{url}/translations/x")));

        // Çeviri, çevirdiği alanın uzunluk sınırını taşır; ama hiçbir alan zorunlu değildir.
        var tooLong = await SendAsync(HttpMethod.Put, $"{url}/translations/tr", new { title = new string('x', 201) });
        Assert.Equal(["title"], (await ErrorsAsync(tooLong)).Select(key => key.ToLowerInvariant()));
        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Put, $"{url}/translations/tr", new { body = "Merhaba" }));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/articles/{Guid.NewGuid()}/translations/tr", new { title = "Yok" })).StatusCode);

        // Çevirisi olmayan bir dili kaldırmak hata değildir.
        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Delete, $"{url}/translations/de"));

        // Geçersiz bir dil etiketiyle okuma, dil istenmemiş gibi kaynak dildeki metni döndürür.
        var odd = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"{url}?locale=not%20a%20locale"));
        Assert.Equal("Launch", odd.Title);

        var turkish = await SendAsync(HttpMethod.Put, $"{url}/translations/1", new { title = "Lansman" }, language: "tr");
        var problem = await turkish.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.Equal("'1' bir dil etiketi değil; 'tr' veya 'pt-BR' gibi yazın.", Assert.Single(problem!.Errors["locale"]));
    }

    [Fact]
    public async Task Translations_AreLimitedPerRecord()
    {
        var article = await CreateAsync("Launch", "Hello");
        var url = $"/articles/{article.Id}";
        var locales = Enumerable.Range(0, HaldusTranslations.MaxLocales + 1).Select(index => $"{(char)('a' + (index / 26))}{(char)('a' + (index % 26))}").ToList();

        foreach (var locale in locales.Take(HaldusTranslations.MaxLocales))
        {
            await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Put, $"{url}/translations/{locale}", new { title = locale }));
        }

        Assert.Equal(["locale"], await ErrorsAsync(await SendAsync(HttpMethod.Put, $"{url}/translations/{locales[^1]}", new { title = "One too many" })));

        // Var olan bir dilin yeniden yazılması sınıra takılmaz.
        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Put, $"{url}/translations/{locales[0]}", new { title = "Again" }));
    }

    [Fact]
    public async Task Translations_TravelWithThePublishedCopyAndTheRevisions()
    {
        var article = await CreateAsync("Launch", "Hello");
        var url = $"/articles/{article.Id}";

        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Put, $"{url}/translations/tr", new { title = "Lansman" }));
        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, $"{url}/publish"));

        // Yayındaki hal çevirileri de taşır ve istenen dilde okunur.
        var live = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"/articles/published/{article.Id}?locale=tr"));
        Assert.Equal("Lansman", live.Title);

        // Taslağın çevirisi değişir; yayındaki hal bir sonraki yayına kadar aynı kalır.
        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Put, $"{url}/translations/tr", new { title = "Açılış" }));
        var stillLive = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"/articles/published/{article.Id}?locale=tr"));
        var listed = await ReadAsync<PagedResult<ArticleResponse>>(await SendAsync(HttpMethod.Get, "/articles/published?locale=tr&pageSize=100"));
        Assert.Equal("Lansman", stillLive.Title);
        Assert.Equal("Lansman", listed.Items.Single(item => item.Id == article.Id).Title);

        // Çeviri yazmak kaydın bir yazmasıdır: sürümü saklanır ve geri yüklenebilir. (Sürümler yeniden eskiye: ikinci
        // çeviri, yayın, ilk çeviri, oluşturma.)
        var revisions = await ReadAsync<PagedResult<RevisionInfo>>(await SendAsync(HttpMethod.Get, $"{url}/revisions"));
        Assert.Equal(4, revisions.TotalCount);

        var firstTranslation = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Get, $"{url}/revisions/{revisions.Items[2].Id}"));
        Assert.Equal("Lansman", firstTranslation.Translations["tr"]["title"]);

        var restored = await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, $"{url}/revisions/{revisions.Items[3].Id}/rollback"));
        Assert.Empty(restored.Translations);
    }

    private async Task<ArticleResponse> CreateAsync(string title, string body) =>
        await ReadAsync<ArticleResponse>(await SendAsync(HttpMethod.Post, "/articles", new { title, slug = $"article-{Guid.NewGuid():N}", body }));

    private static async Task<IEnumerable<string>> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!.Errors.Keys;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? language = null)
    {
        using var request = new HttpRequestMessage(method, url);

        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
        }

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
