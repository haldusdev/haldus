using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Account;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

/// <summary>
/// Alan eklentileri: yalnızca oluştururken yazılan alan, doğal anahtar, liste sıralaması, anahtar biçimi ve
/// metadata çantası.
/// </summary>
[Collection(ShopCollection.Name)]
public sealed partial class FieldExtensionEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly string _ada = $"ada-{Guid.NewGuid():N}";
    private readonly string _bob = $"bob-{Guid.NewGuid():N}";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [GeneratedRegex("^list_[0-9a-hjkmnp-tv-z]{26}$")]
    private static partial Regex ListKey();

    // ── Yalnızca oluştururken yazılan alan ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateOnlyField_IsWrittenOnCreateAndIgnoredAfterwards()
    {
        var code = NewCode();
        var coupon = await ReadAsync<CouponResponse>(
            await SendAsync(HttpMethod.Post, "/coupons", new { code, percent = 10, maxRedemptions = 3 }));

        // Güncelleme isteklerinde bu alanlar yoktur; gönderilseler de kayda yazılmazlar.
        var replaced = await ReadAsync<CouponResponse>(await SendAsync(
            HttpMethod.Put, $"/coupons/{coupon.Id}", new { code = NewCode(), percent = 90, maxRedemptions = 5 }));
        var patched = await ReadAsync<CouponResponse>(await SendAsync(
            HttpMethod.Patch, $"/coupons/{coupon.Id}", new { code = NewCode(), percent = 50 }));

        Assert.Equal((code, 10, 3), (coupon.Code, coupon.Percent, coupon.MaxRedemptions));
        Assert.Equal((code, 10, 5), (replaced.Code, replaced.Percent, replaced.MaxRedemptions));
        Assert.Equal((code, 10, 5), (patched.Code, patched.Percent, patched.MaxRedemptions));
    }

    // ── Doğal anahtar ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NaturalKey_ReadsTheRecordByItsValue()
    {
        var code = NewCode();
        var coupon = await ReadAsync<CouponResponse>(await SendAsync(HttpMethod.Post, "/coupons", new { code, percent = 10 }));

        var found = await ReadAsync<CouponResponse>(await SendAsync(HttpMethod.Get, $"/coupons/by-code/{code}"));
        var missing = await SendAsync(HttpMethod.Get, $"/coupons/by-code/{NewCode()}");

        Assert.Equal(coupon.Id, found.Id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task NaturalKey_RejectsADuplicateInTheRequestsLanguage()
    {
        var code = NewCode();
        await ReadAsync<CouponResponse>(await SendAsync(HttpMethod.Post, "/coupons", new { code, percent = 10 }));

        var english = await SendAsync(HttpMethod.Post, "/coupons", new { code, percent = 20 });
        var turkish = await SendAsync(HttpMethod.Post, "/coupons", new { code, percent = 20 }, language: "tr");

        Assert.Equal(HttpStatusCode.Conflict, english.StatusCode);

        var problem = (await english.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!;
        Assert.Equal("Already exists", problem.Title);
        Assert.Equal($"Another Coupon already has the code '{code}'.", problem.Detail);
        Assert.Equal("Kayıt zaten var", (await turkish.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Title);
    }

    [Fact]
    public async Task NaturalKey_CreatedConcurrently_IsTakenByExactlyOneRequest()
    {
        var code = NewCode();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => SendAsync(HttpMethod.Post, "/coupons", new { code, percent = 10 })));

        // Ön kontrolü birlikte geçen istekler indekse takılır; o da 500 değil çakışmadır.
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task NaturalKey_IsStillHeldByARecordThatIsNoLongerVisible()
    {
        var code = NewCode();
        var expired = await SendAsync(
            HttpMethod.Post, "/coupons", new { code, percent = 10, expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });

        var reused = await SendAsync(HttpMethod.Post, "/coupons", new { code, percent = 10 });
        var read = await SendAsync(HttpMethod.Get, $"/coupons/by-code/{code}");

        // Süresi dolan kupon okunamaz, ama kodu boşa çıkmaz: aynı kod yeniden dağıtılamaz.
        Assert.Equal(HttpStatusCode.Created, expired.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task NaturalKey_IsUniqueWithinTheOwnerAndCheckedOnEveryWrite()
    {
        var first = await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Post, "/me/shopping-lists", new { name = "Groceries" }, _ada));
        var second = await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Post, "/me/shopping-lists", new { name = "Hardware" }, _ada));

        var duplicate = await SendAsync(HttpMethod.Post, "/me/shopping-lists", new { name = "Groceries" }, _ada);
        var replaced = await SendAsync(HttpMethod.Put, $"/me/shopping-lists/{second.Id}", new { name = "Groceries" }, _ada);
        var patched = await SendAsync(HttpMethod.Patch, $"/me/shopping-lists/{second.Id}", new { name = "Groceries" }, _ada);
        var unchanged = await SendAsync(HttpMethod.Put, $"/me/shopping-lists/{second.Id}", new { name = "Hardware" }, _ada);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, replaced.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, patched.StatusCode);

        // Kayıt kendi değeriyle çakışmaz.
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);

        // Aynı ad başka bir kullanıcıda serbesttir ve herkes adıyla kendi kaydını okur.
        var bobs = await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Post, "/me/shopping-lists", new { name = "Groceries" }, _bob));
        var readByAda = await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Get, "/me/shopping-lists/by-name/Groceries", user: _ada));
        var readByBob = await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Get, "/me/shopping-lists/by-name/Groceries", user: _bob));

        Assert.Equal(first.Id, readByAda.Id);
        Assert.Equal(bobs.Id, readByBob.Id);
    }

    // ── Sıralama ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sortable_OrdersTheListByTheRequestedFields()
    {
        foreach (var name in new[] { "Bravo", "Alpha", "Charlie" })
        {
            await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Post, "/me/shopping-lists", new { name }, _ada));

            // Oluşturulma zamanları ayrışsın.
            await Task.Delay(5, CancellationToken);
        }

        Assert.Equal(["Alpha", "Bravo", "Charlie"], await NamesAsync("sort=name"));
        Assert.Equal(["Charlie", "Bravo", "Alpha"], await NamesAsync("sort=-name"));
        Assert.Equal(["Charlie", "Alpha", "Bravo"], await NamesAsync("sort=-createdAt"));
        Assert.Equal(["Bravo", "Alpha", "Charlie"], await NamesAsync("sort=createdAt,-name"));

        // Sıralama sayfalamadan önce uygulanır.
        Assert.Equal(["Bravo"], await NamesAsync("sort=name&page=2&pageSize=1"));
    }

    [Fact]
    public async Task Sortable_RejectsAFieldThatWasNotDeclared()
    {
        var unknown = await SendAsync(HttpMethod.Get, "/me/shopping-lists?sort=ownerId", user: _ada);
        var tooMany = await SendAsync(HttpMethod.Get, "/me/shopping-lists?sort=name,createdAt,-name", user: _ada);
        var turkish = await SendAsync(HttpMethod.Get, "/me/shopping-lists?sort=ownerId", user: _ada, language: "tr");

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        var problem = (await unknown.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!;
        Assert.Equal(["Cannot sort by 'ownerId'. Sortable fields: name, createdAt."], problem.Errors["sort"]);

        var translated = (await turkish.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!;
        Assert.Equal(["'ownerId' alanına göre sıralanamaz. Sıralanabilir alanlar: name, createdAt."], translated.Errors["sort"]);
    }

    // ── Anahtar biçimi ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrefixedKey_IdentifiesTheRecordInEveryAddress()
    {
        var created = await SendAsync(HttpMethod.Post, "/me/shopping-lists", new { name = "Keys" }, _ada);
        var list = await ReadAsync<ShoppingListResponse>(created);

        Assert.Matches(ListKey(), list.Id);
        Assert.Equal($"/me/shopping-lists/{list.Id}", created.Headers.Location!.OriginalString);

        // Önekli kimlik, altındaki kaynağın adresinde ve yabancı anahtarında da taşınır.
        var item = await ReadAsync<ShoppingListItemResponse>(
            await SendAsync(HttpMethod.Post, $"/me/shopping-lists/{list.Id}/shopping-list-items", new { text = "Milk" }, _ada));
        var read = await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Get, $"/me/shopping-lists/{list.Id}", user: _ada));

        Assert.Equal(list.Id, item.ShoppingListId);
        Assert.Equal("Keys", read.Name);
    }

    [Fact]
    public async Task PrefixedKey_IsAlsoGeneratedForRecordsAddedByTheApplicationItself()
    {
        string? id = null;

        await fixture.WithDbContextAsync(async context =>
        {
            // Bir isteğin dışında çalışan kod, kimin adına çalıştığını kendisi söyler.
            using var identity = HaldusAmbient.Use(tenantId: null, _ada);
            var list = new ShoppingList { Name = "Added directly" };

            context.ShoppingLists.Add(list);
            await context.SaveChangesAsync(CancellationToken);
            id = list.Id;
        });

        Assert.Matches(ListKey(), id!);

        var read = await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Get, $"/me/shopping-lists/{id}", user: _ada));
        Assert.Equal("Added directly", read.Name);
    }

    [Fact]
    public async Task PrefixedKeys_SortInTheOrderTheyWereCreated()
    {
        var keys = new List<string>();

        for (var index = 0; index < 5; index++)
        {
            keys.Add(HaldusKeys.NewPrefixed("ord"));
            await Task.Delay(3, CancellationToken);
        }

        Assert.Equal(keys, keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.All(keys, key => Assert.Equal("ord_".Length + HaldusKeys.BodyLength, key.Length));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task RandomKey_DoesNotRevealWhenTheRecordWasCreated()
    {
        var product = await CreateProductAsync();

        var note = await ReadAsync<ProductNoteResponse>(
            await SendAsync(HttpMethod.Post, $"/products/{product.Id}/product-notes", new { text = "Private" }, _ada));

        // Varsayılan anahtar zamana göre sıralı GUID'dir (v7); bu kaynak rastgele olanı (v4) seçti.
        Assert.Equal(7, product.Id.Version);
        Assert.Equal(4, note.Id.Version);
    }

    // ── Metadata ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Metadata_IsStoredWithTheRecordAndStartsEmpty()
    {
        var plain = await CreateProductAsync();
        var tagged = await CreateProductAsync(new Dictionary<string, string> { ["source"] = "import", ["batch"] = "42" });

        var read = await ReadAsync<ProductResponse>(await SendAsync(HttpMethod.Get, $"/products/{tagged.Id}"));

        Assert.Empty(plain.Metadata);
        Assert.Equal("import", read.Metadata["source"]);
        Assert.Equal("42", read.Metadata["batch"]);
        Assert.Equal(2, read.Metadata.Count);
    }

    [Fact]
    public async Task Metadata_PatchMergesAndPutReplaces()
    {
        var product = await CreateProductAsync(new Dictionary<string, string> { ["keep"] = "1", ["change"] = "old", ["drop"] = "x" });
        var url = $"/products/{product.Id}";

        // Gönderilen anahtar eklenir veya değişir, null gönderilen silinir, gönderilmeyen kalır.
        var merged = await ReadAsync<ProductResponse>(await SendAsync(
            HttpMethod.Patch, url, new { metadata = new Dictionary<string, string?> { ["change"] = "new", ["add"] = "2", ["drop"] = null } }));

        Assert.Equal(
            new Dictionary<string, string> { ["keep"] = "1", ["change"] = "new", ["add"] = "2" },
            merged.Metadata);

        // Çantadan söz etmeyen bir PATCH ona dokunmaz.
        var untouched = await ReadAsync<ProductResponse>(await SendAsync(HttpMethod.Patch, url, new { price = 15 }));
        Assert.Equal(3, untouched.Metadata.Count);

        // PUT çantanın tamamını değiştirir.
        var replaced = await ReadAsync<ProductResponse>(await SendAsync(
            HttpMethod.Put, url, new { name = product.Name, price = 15, metadata = new Dictionary<string, string> { ["only"] = "this" } }));
        Assert.Equal(new Dictionary<string, string> { ["only"] = "this" }, replaced.Metadata);

        var persisted = await ReadAsync<ProductResponse>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal(new Dictionary<string, string> { ["only"] = "this" }, persisted.Metadata);

        // Çantanın kendisi null gönderilirse boşaltılır.
        var cleared = await ReadAsync<ProductResponse>(await SendAsync(HttpMethod.Patch, url, new { metadata = (object?)null }));
        Assert.Empty(cleared.Metadata);
    }

    [Fact]
    public async Task Metadata_IsLimitedInSizeAlsoAfterAMerge()
    {
        var full = Enumerable.Range(0, HaldusMetadata.MaxEntries).ToDictionary(index => $"key{index}", index => "value");
        var tooMany = Enumerable.Range(0, HaldusMetadata.MaxEntries + 1).ToDictionary(index => $"key{index}", index => "value");

        var oversized = await SendAsync(HttpMethod.Post, "/products", new { name = NewName(), price = 1, metadata = tooMany });
        var longKey = await SendAsync(
            HttpMethod.Post, "/products", new { name = NewName(), price = 1, metadata = new Dictionary<string, string> { [new string('k', 41)] = "v" } });
        var longValue = await SendAsync(
            HttpMethod.Post, "/products", new { name = NewName(), price = 1, metadata = new Dictionary<string, string> { ["k"] = new string('v', 501) } });

        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longValue.StatusCode);

        var problem = (await oversized.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!;
        Assert.Equal(
            ["The field Metadata may hold at most 50 entries; keys must be 1 to 40 characters and values at most 500 characters."],
            problem.Errors["metadata"]);

        // Tek tek geçerli iki istek birleşince sınırı aşabilir; o da reddedilir ve hiçbir şey kaydedilmez.
        var product = await CreateProductAsync(full);
        var overflow = await SendAsync(
            HttpMethod.Patch, $"/products/{product.Id}", new { metadata = new Dictionary<string, string> { ["one-more"] = "value" } });
        var unchanged = await ReadAsync<ProductResponse>(await SendAsync(HttpMethod.Get, $"/products/{product.Id}"));

        Assert.Equal(HttpStatusCode.BadRequest, overflow.StatusCode);
        Assert.Equal(HaldusMetadata.MaxEntries, unchanged.Metadata.Count);
        Assert.False(unchanged.Metadata.ContainsKey("one-more"));
    }

    private async Task<string[]> NamesAsync(string query)
    {
        var page = await ReadAsync<PagedResult<ShoppingListResponse>>(
            await SendAsync(HttpMethod.Get, $"/me/shopping-lists?{query}", user: _ada));

        return [.. page.Items.Select(item => item.Name)];
    }

    private async Task<ProductResponse> CreateProductAsync(Dictionary<string, string>? metadata = null) =>
        await ReadAsync<ProductResponse>(await SendAsync(HttpMethod.Post, "/products", new { name = NewName(), price = 10, metadata }));

    private static string NewName() => $"Extended {Guid.NewGuid():N}";

    private static string NewCode() => $"C-{Guid.NewGuid():N}";

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string? user = null, string? language = null)
    {
        using var request = new HttpRequestMessage(method, url);

        if (user is not null)
        {
            request.Headers.Add(DemoIdentity.UserHeader, user);
        }

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
