using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Account;
using Haldus.Sample.Shop.Features.Auth;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Authentication.BearerToken;

namespace Haldus.IntegrationTests;

/// <summary>
/// Gerçek kimlik doğrulama: parola ile giriş, taşıyıcı token ve Haldus'un kullanıcıyı ve kiracıyı token'daki
/// claim'lerden okuması. Öteki testlerin aksine kimlik başlıklarla verilmez; uygulama varsayılan ayarıyla çalışır.
/// </summary>
[Collection(ShopCollection.Name)]
public sealed class AuthEndpointTests(ShopFixture fixture)
{
    private const string Password = "correct horse battery";

    private readonly HttpClient _client = fixture.CreateAuthenticatedAppClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Register_SignsInAndTheTokenCarriesTheUserAndTheTenant()
    {
        var email = NewEmail();
        var session = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/register", new { email, password = Password }));

        Assert.False(string.IsNullOrEmpty(session.AccessToken));
        Assert.False(string.IsNullOrEmpty(session.RefreshToken));
        Assert.True(session.ExpiresIn > 0);

        var me = await ReadAsync<AccountInfo>(await SendAsync(HttpMethod.Get, "/auth/me", token: session.AccessToken));
        Assert.Equal(email, me.Email);
        Assert.NotEqual(Guid.Empty, me.TenantId);

        // Token'sız istek tanınmaz.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/auth/me", token: "not-a-token")).StatusCode);

        // İstek doğrulanır; aynı e-posta ikinci kez kaydedilemez (büyük-küçük harf fark etmez).
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/auth/register", new { email = NewEmail(), password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/auth/register", new { email = "not-an-email", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/auth/register", new { email = email.ToUpperInvariant(), password = Password })).StatusCode);
    }

    [Fact]
    public async Task Login_ChecksThePasswordAndRefreshRenewsTheSession()
    {
        var email = NewEmail();
        await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/register", new { email, password = Password }));

        // Yanlış parola ile olmayan hesap aynı yanıtı alır.
        var wrong = await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = "wrong password" });
        var unknown = await SendAsync(HttpMethod.Post, "/auth/login", new { email = NewEmail(), password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await DetailAsync(wrong), await DetailAsync(unknown));

        var session = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = Password }));
        var me = await ReadAsync<AccountInfo>(await SendAsync(HttpMethod.Get, "/auth/me", token: session.AccessToken));

        // Yenileme token'ı yeni bir oturum verir; aynı hesaptır. Erişim token'ı yenileme için kullanılamaz.
        var renewed = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = session.RefreshToken }));
        Assert.Equal(me, await ReadAsync<AccountInfo>(await SendAsync(HttpMethod.Get, "/auth/me", token: renewed.AccessToken)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = session.AccessToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = "garbage" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/auth/me", token: session.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Haldus_ReadsTheTenantAndTheUserFromTheToken()
    {
        var first = await RegisterAsync();
        var second = await RegisterAsync();

        // Sipariş kiracıya aittir; kiracı token'daki claim'den gelir.
        var order = await ReadAsync<OrderResponse>(await SendAsync(
            HttpMethod.Post, "/orders", new { customerName = "Ada" }, first.Token));

        var mine = await ReadAsync<PagedResult<OrderResponse>>(await SendAsync(HttpMethod.Get, "/orders?pageSize=100", token: first.Token));
        var theirs = await ReadAsync<PagedResult<OrderResponse>>(await SendAsync(HttpMethod.Get, "/orders?pageSize=100", token: second.Token));
        Assert.Contains(mine.Items, item => item.Id == order.Id);
        Assert.Empty(theirs.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/orders/{order.Id}", token: second.Token)).StatusCode);

        // Kimlik başlıkları artık hiçbir şey ifade etmez: kiracıyı ve kullanıcıyı yalnızca doğrulanmış token tanıtır.
        using var spoofed = new HttpRequestMessage(HttpMethod.Get, $"/orders/{order.Id}");
        spoofed.Headers.Add(DemoIdentity.TenantHeader, first.Account.TenantId.ToString());
        spoofed.Headers.Add(DemoIdentity.UserHeader, first.Account.Id.ToString());
        Assert.NotEqual(HttpStatusCode.OK, (await _client.SendAsync(spoofed, CancellationToken)).StatusCode);

        // Kullanıcıya ait kaynak (/me) kullanıcıyı da token'dan okur.
        var basket = await ReadAsync<BasketResponse>(await SendAsync(HttpMethod.Post, "/me/baskets", new { note = "Mine" }, first.Token));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/me/baskets/{basket.Id}", token: first.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/me/baskets/{basket.Id}", token: second.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/me/baskets")).StatusCode);

        // Herkese açık kaynaklar oturumsuz okunur.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/products")).StatusCode);
    }

    [Fact]
    public async Task RejectedToken_IsUnauthorizedWhateverTheResourceIs()
    {
        var account = await RegisterAsync();
        const string Stale = "not-a-valid-token";

        // Reddedilen token sunan istek anonim sayılmaz: kiracıya ait, kullanıcıya ait ve herkese açık kaynakta da 401
        // alır; istemci oturumunu yenilemesi gerektiğini tek bir yanıttan anlar.
        foreach (var url in new[] { "/orders", "/me/baskets", "/products", "/auth/me" })
        {
            var response = await SendAsync(HttpMethod.Get, url, token: Stale);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString());
        }

        // Token istemeyen kimlik uçları bundan etkilenmez: süresi dolmuş oturumla da giriş yapılır ve oturum yenilenir.
        var login = await SendAsync(HttpMethod.Post, "/auth/login", new { email = account.Account.Email, password = Password }, Stale);
        var session = await ReadAsync<AccessTokenResponse>(login);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = session.RefreshToken }, Stale)).StatusCode);

        // Token sunmayan istek eskisi gibidir: herkese açık kaynak okunur, kiracıya ait kaynak kiracı ister.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/orders")).StatusCode);
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(CancellationToken);
        return problem.GetProperty("detail").GetString();
    }

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private async Task<(string Token, AccountInfo Account)> RegisterAsync()
    {
        var session = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/register", new { email = NewEmail(), password = Password }));
        var account = await ReadAsync<AccountInfo>(await SendAsync(HttpMethod.Get, "/auth/me", token: session.AccessToken));
        return (session.AccessToken, account);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, url);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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