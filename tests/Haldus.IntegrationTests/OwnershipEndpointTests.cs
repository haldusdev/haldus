using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Account;
using Haldus.Sample.Shop.Features.Catalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

/// <summary>Kullanıcıya ait kaynaklar ve geçerli kullanıcıya bağlı bağlar.</summary>
[Collection(ShopCollection.Name)]
public sealed class OwnershipEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly string _ada = $"ada-{Guid.NewGuid():N}";
    private readonly string _bob = $"bob-{Guid.NewGuid():N}";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OwnedResource_LivesUnderMeAndBelongsToWhoeverCreatedIt()
    {
        // Sahip istek gövdesinden yazılamaz; gönderilse de yok sayılır.
        var created = await SendAsync(HttpMethod.Post, "/me/shopping-lists", _ada, new { name = "Weekend", ownerId = _bob });
        var list = await ReadAsync<ShoppingListResponse>(created);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/me/shopping-lists/{list.Id}", created.Headers.Location!.OriginalString);

        // Sahip isteği yapanın kendisidir; yanıtta yinelenmez.
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(CancellationToken));
        Assert.False(body.RootElement.TryGetProperty("ownerId", out _));

        await fixture.WithDbContextAsync(async context =>
        {
            var stored = await context.ShoppingLists
                .IgnoreQueryFilters([HaldusFilters.Owner])
                .SingleAsync(item => item.Id == list.Id, CancellationToken);

            Assert.Equal(_ada, stored.OwnerId);
        });

        var mine = await ReadAsync<PagedResult<ShoppingListResponse>>(await SendAsync(HttpMethod.Get, "/me/shopping-lists", _ada));
        Assert.Equal([list.Id], mine.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task OwnedResource_CannotBeSeenOrChangedByAnotherUser()
    {
        var list = await ReadAsync<ShoppingListResponse>(
            await SendAsync(HttpMethod.Post, "/me/shopping-lists", _ada, new { name = "Private" }));
        var url = $"/me/shopping-lists/{list.Id}";

        var read = await SendAsync(HttpMethod.Get, url, _bob);
        var replaced = await SendAsync(HttpMethod.Put, url, _bob, new { name = "Taken" });
        var patched = await SendAsync(HttpMethod.Patch, url, _bob, new { name = "Taken" });
        var deleted = await SendAsync(HttpMethod.Delete, url, _bob);
        var theirs = await ReadAsync<PagedResult<ShoppingListResponse>>(await SendAsync(HttpMethod.Get, "/me/shopping-lists", _bob));

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, replaced.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, patched.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        Assert.Empty(theirs.Items);

        var untouched = await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Get, url, _ada));
        Assert.Equal("Private", untouched.Name);

        // Sahibi ise her şeyi yapabilir; güncelleme sahipliği değiştirmez.
        var renamed = await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Put, url, _ada, new { name = "Renamed" }));
        Assert.Equal("Renamed", renamed.Name);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, url, _bob)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, url, _ada)).StatusCode);
    }

    [Fact]
    public async Task OwnedResource_WithoutAUser_IsRejectedInTheRequestsLanguage()
    {
        var list = await SendAsync(HttpMethod.Get, "/me/shopping-lists", user: null);
        var create = await SendAsync(HttpMethod.Post, "/me/shopping-lists", user: null, new { name = "Nobody's" });
        var turkish = await SendAsync(HttpMethod.Get, "/me/shopping-lists", user: null, language: "tr");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal("Sign-in required", (await list.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Title);
        Assert.Equal("Oturum gerekli", (await turkish.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Title);
    }

    [Fact]
    public async Task ResourceUnderAnOwnedParent_FollowsItsAddressAndItsVisibility()
    {
        var list = await ReadAsync<ShoppingListResponse>(
            await SendAsync(HttpMethod.Post, "/me/shopping-lists", _ada, new { name = "Groceries" }));
        var url = $"/me/shopping-lists/{list.Id}/shopping-list-items";

        var created = await SendAsync(HttpMethod.Post, url, _ada, new { text = "Milk" });
        var item = await ReadAsync<ShoppingListItemResponse>(created);

        Assert.Equal($"{url}/{item.Id}", created.Headers.Location!.OriginalString);
        Assert.Equal(list.Id, item.ShoppingListId);

        // Liste Bob'a görünmediği için kalemleri de görünmez ve altına yazılamaz.
        var theirs = await ReadAsync<PagedResult<ShoppingListItemResponse>>(await SendAsync(HttpMethod.Get, url, _bob));
        var read = await SendAsync(HttpMethod.Get, $"{url}/{item.Id}", _bob);
        var added = await SendAsync(HttpMethod.Post, url, _bob, new { text = "Sneaked in" });
        var anonymous = await SendAsync(HttpMethod.Get, url, user: null);

        Assert.Empty(theirs.Items);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, added.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var mine = await ReadAsync<PagedResult<ShoppingListItemResponse>>(await SendAsync(HttpMethod.Get, url, _ada));
        Assert.Equal(["Milk"], mine.Items.Select(entry => entry.Text));
    }

    [Fact]
    public async Task OwnedResourceOutsideMe_KeepsItsAddressButShowsOnlyTheCallersRecords()
    {
        var product = await CreateProductAsync();
        var url = $"/products/{product.Id}/product-notes";

        var adas = await ReadAsync<ProductNoteResponse>(await SendAsync(HttpMethod.Post, url, _ada, new { text = "Buy for mum" }));
        var bobs = await ReadAsync<ProductNoteResponse>(await SendAsync(HttpMethod.Post, url, _bob, new { text = "Too expensive" }));

        var seenByAda = await ReadAsync<PagedResult<ProductNoteResponse>>(await SendAsync(HttpMethod.Get, url, _ada));
        var seenByBob = await ReadAsync<PagedResult<ProductNoteResponse>>(await SendAsync(HttpMethod.Get, url, _bob));
        var peek = await SendAsync(HttpMethod.Get, $"{url}/{adas.Id}", _bob);
        var anonymous = await SendAsync(HttpMethod.Get, url, user: null);

        Assert.Equal([adas.Id], seenByAda.Items.Select(note => note.Id));
        Assert.Equal([bobs.Id], seenByBob.Items.Select(note => note.Id));
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task LinkForCurrentUser_IsSetListedAndRemovedPerUser()
    {
        var product = await CreateProductAsync();
        var other = await CreateProductAsync();
        var url = $"/me/favorites/{product.Id}";

        var linked = await SendAsync(HttpMethod.Put, url, _ada);
        var repeated = await SendAsync(HttpMethod.Put, url, _ada);
        var exists = await SendAsync(HttpMethod.Get, url, _ada);
        var absent = await SendAsync(HttpMethod.Get, $"/me/favorites/{other.Id}", _ada);

        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, exists.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);

        // Liste bağları değil, bağlı kayıtları döner.
        var favorites = await ReadAsync<PagedResult<ProductResponse>>(await SendAsync(HttpMethod.Get, "/me/favorites", _ada));
        Assert.Equal([product.Id], favorites.Items.Select(item => item.Id));
        Assert.Equal(product.Name, favorites.Items[0].Name);

        // Başka bir kullanıcı bağı ne görür ne de kaldırabilir.
        var theirs = await ReadAsync<PagedResult<ProductResponse>>(await SendAsync(HttpMethod.Get, "/me/favorites", _bob));
        var peek = await SendAsync(HttpMethod.Get, url, _bob);
        var stolen = await SendAsync(HttpMethod.Delete, url, _bob);

        Assert.Empty(theirs.Items);
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Get, url, _ada)).StatusCode);

        var removed = await SendAsync(HttpMethod.Delete, url, _ada);
        var removedAgain = await SendAsync(HttpMethod.Delete, url, _ada);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removedAgain.StatusCode);
    }

    [Fact]
    public async Task LinkForCurrentUser_NeedsAUserAndAnExistingRecord()
    {
        var product = await CreateProductAsync();

        var anonymous = await SendAsync(HttpMethod.Put, $"/me/favorites/{product.Id}", user: null);
        var missing = await SendAsync(HttpMethod.Put, $"/me/favorites/{Guid.NewGuid()}", _ada);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task LinkForCurrentUser_SetConcurrently_EndsUpAsOneLink()
    {
        var product = await CreateProductAsync();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => SendAsync(HttpMethod.Put, $"/me/favorites/{product.Id}", _ada)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));

        await fixture.WithDbContextAsync(async context =>
        {
            var count = await context.Favorites
                .IgnoreQueryFilters([HaldusFilters.Owner])
                .CountAsync(link => link.ProductId == product.Id, CancellationToken);

            Assert.Equal(1, count);
        });
    }

    [Fact]
    public async Task LinkForCurrentUser_DoesNotKeepTheRecordFromBeingDeleted()
    {
        var product = await CreateProductAsync();
        await SendAsync(HttpMethod.Put, $"/me/favorites/{product.Id}", _ada);
        await SendAsync(HttpMethod.Put, $"/me/favorites/{product.Id}", _bob);

        var deleted = await SendAsync(HttpMethod.Delete, $"/products/{product.Id}", user: null);

        // Bağ yalnızca bir işarettir: kayıt silinince herkesin bağı onunla birlikte gider.
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var favorites = await ReadAsync<PagedResult<ProductResponse>>(await SendAsync(HttpMethod.Get, "/me/favorites", _ada));
        Assert.Empty(favorites.Items);

        await fixture.WithDbContextAsync(async context =>
        {
            var left = await context.Favorites.IgnoreQueryFilters().CountAsync(link => link.ProductId == product.Id, CancellationToken);
            Assert.Equal(0, left);
        });
    }

    private async Task<ProductResponse> CreateProductAsync() =>
        await ReadAsync<ProductResponse>(
            await SendAsync(HttpMethod.Post, "/products", user: null, new { name = $"Owned {Guid.NewGuid():N}", price = 10 }));

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string? user, object? body = null, string? language = null)
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
