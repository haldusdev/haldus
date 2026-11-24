using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Account;

namespace Haldus.IntegrationTests;

/// <summary>
/// Araştırmadaki sepet satırının (5) uçtan uca hali: kullanıcının sepeti, oturumu olmayan ziyaretçinin token ile
/// ulaşılan sepeti ve ikisini birleştiren entity aksiyonu.
/// </summary>
[Collection(ShopCollection.Name)]
public sealed class BasketEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GuestBasket_IsReachedOnlyWithItsToken()
    {
        var created = await SendAsync(HttpMethod.Post, "/guest-baskets", body: new
        {
            guestBasketItems = new[] { new { productName = "Keyboard", quantity = 1 } },
        });
        var basket = await ReadAsync<GuestBasketResponse>(created);

        // Token yalnızca oluşturma yanıtında görünür.
        Assert.StartsWith("basket_", basket.Token);

        var read = await ReadAsync<GuestBasketResponse>(await SendAsync(HttpMethod.Get, "/guest-baskets/by-token", token: basket.Token));
        Assert.Equal(basket.Id, read.Id);
        Assert.Null(read.Token);
        Assert.Equal("Keyboard", Assert.Single(read.GuestBasketItems).ProductName);

        // Kimlikle okuma ve liste yoktur; sepete token'ı olmayan ulaşamaz.
        Assert.Contains(
            (await SendAsync(HttpMethod.Get, $"/guest-baskets/{basket.Id}")).StatusCode,
            new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Contains(
            (await SendAsync(HttpMethod.Get, "/guest-baskets")).StatusCode,
            new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/guest-baskets/by-token")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, "/guest-baskets/by-token", token: "basket_" + new string('x', 43))).StatusCode);

        // Token ile yalnızca sayılan aksiyon çalışır. Aynı ürün ikinci kez eklenince satır çoğalmaz.
        await ReadAsync<GuestBasketResponse>(await SendAsync(
            HttpMethod.Post, "/guest-baskets/by-token/add-basket-item", token: basket.Token, body: new { productName = "Mouse", quantity = 2 }));
        var added = await ReadAsync<GuestBasketResponse>(await SendAsync(
            HttpMethod.Post, "/guest-baskets/by-token/add-basket-item", token: basket.Token, body: new { productName = "Keyboard", quantity = 3 }));

        Assert.Equal(
            [("Keyboard", 4), ("Mouse", 2)],
            added.GuestBasketItems.Select(item => (item.ProductName, item.Quantity)));

        // Kimlikle çalışan hiçbir adres yoktur: kimliği bilen biri sepete satır ekleyemez, sepeti sonlandıramaz.
        foreach (var (method, url) in new[]
        {
            (HttpMethod.Post, $"/guest-baskets/{basket.Id}/add-basket-item"),
            (HttpMethod.Post, $"/guest-baskets/{basket.Id}/expire"),
            (HttpMethod.Put, $"/guest-baskets/{basket.Id}"),
            (HttpMethod.Patch, $"/guest-baskets/{basket.Id}"),
            (HttpMethod.Delete, $"/guest-baskets/{basket.Id}"),
        })
        {
            var response = await SendAsync(method, url, body: new { productName = "Intruder", quantity = 1 });
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }

        var untouched = await ReadAsync<GuestBasketResponse>(await SendAsync(HttpMethod.Get, "/guest-baskets/by-token", token: basket.Token));
        Assert.Equal(2, untouched.GuestBasketItems.Count);

        // Aksiyonun isteği de doğrulanır.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Post, "/guest-baskets/by-token/add-basket-item", token: basket.Token, body: new { productName = "Cable", quantity = 0 })).StatusCode);
    }

    [Fact]
    public async Task Merge_MovesTheGuestRowsIntoTheUsersBasketAndRemovesTheGuestBasket()
    {
        var user = $"user-{Guid.NewGuid():N}";

        var guest = await ReadAsync<GuestBasketResponse>(await SendAsync(HttpMethod.Post, "/guest-baskets", body: new
        {
            guestBasketItems = new[]
            {
                new { productName = "Keyboard", quantity = 2 },
                new { productName = "Cable", quantity = 1 },
            },
        }));

        var mine = await ReadAsync<BasketResponse>(await SendAsync(HttpMethod.Post, "/me/baskets", user, body: new
        {
            note = "Mine",
            basketItems = new[]
            {
                new { productName = "Mouse", quantity = 1 },
                new { productName = "Keyboard", quantity = 1 },
            },
        }));

        var merged = await ReadAsync<BasketResponse>(await SendAsync(
            HttpMethod.Post, $"/me/baskets/{mine.Id}/merge", user, body: new { guestToken = guest.Token }));

        // Ortak ürünün miktarı toplanır, yeni ürün sona eklenir; var olan satırlar kimliklerini korur.
        Assert.Equal(
            [("Mouse", 1), ("Keyboard", 3), ("Cable", 1)],
            merged.BasketItems.Select(item => (item.ProductName, item.Quantity)));
        Assert.Equal(mine.BasketItems.Select(item => item.Id), merged.BasketItems.Take(2).Select(item => item.Id));
        Assert.Equal("Mine", merged.Note);

        var stored = await ReadAsync<BasketResponse>(await SendAsync(HttpMethod.Get, $"/me/baskets/{mine.Id}", user));
        Assert.Equal(3, stored.BasketItems.Count);

        // Misafir sepeti aynı kaydetmede silinir: token artık hiçbir şeye ulaşmaz ve ikinci kez birleştirilemez.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/guest-baskets/by-token", token: guest.Token)).StatusCode);

        var again = await SendAsync(HttpMethod.Post, $"/me/baskets/{mine.Id}/merge", user, body: new { guestToken = guest.Token });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Contains("guest basket", await again.Content.ReadAsStringAsync(CancellationToken), StringComparison.OrdinalIgnoreCase);

        var unchanged = await ReadAsync<BasketResponse>(await SendAsync(HttpMethod.Get, $"/me/baskets/{mine.Id}", user));
        Assert.Equal(merged.BasketItems.Select(item => item.Quantity), unchanged.BasketItems.Select(item => item.Quantity));
    }

    [Fact]
    public async Task Merge_IsRejectedForAnotherUsersBasketAndLeavesTheGuestBasketAlone()
    {
        var owner = $"user-{Guid.NewGuid():N}";
        var stranger = $"user-{Guid.NewGuid():N}";

        var guest = await ReadAsync<GuestBasketResponse>(await SendAsync(HttpMethod.Post, "/guest-baskets", body: new
        {
            guestBasketItems = new[] { new { productName = "Lamp", quantity = 1 } },
        }));
        var mine = await ReadAsync<BasketResponse>(await SendAsync(HttpMethod.Post, "/me/baskets", owner, body: new { note = "Owner" }));

        // Başkasının sepeti görünmez; birleştirme hiç çalışmaz.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Post, $"/me/baskets/{mine.Id}/merge", stranger, body: new { guestToken = guest.Token })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, $"/me/baskets/{mine.Id}/merge", body: new { guestToken = guest.Token })).StatusCode);

        // Misafir sepeti yerinde durur.
        var read = await ReadAsync<GuestBasketResponse>(await SendAsync(HttpMethod.Get, "/guest-baskets/by-token", token: guest.Token));
        Assert.Equal("Lamp", Assert.Single(read.GuestBasketItems).ProductName);

        // İstek doğrulanır: token'sız birleştirme 400'dür.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Post, $"/me/baskets/{mine.Id}/merge", owner, body: new { guestToken = "" })).StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? user = null, string? token = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);

        if (user is not null)
        {
            request.Headers.Add(DemoIdentity.UserHeader, user);
        }

        if (token is not null)
        {
            request.Headers.Add(HaldusTokenAccess.Header, token);
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
