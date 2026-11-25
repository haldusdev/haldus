using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Account;
using Haldus.Sample.Shop.Features.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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

        // Kiracıya ait olmayan kaynaklar (ürünler) her hesaba açıktır, ama yine giriş ister.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/products", token: second.Token)).StatusCode);
    }

    [Fact]
    public async Task AnonymousRequests_ReachOnlyWhatIsDeclaredOpen()
    {
        // Giriş yapılmadan hiçbir kaynak okunamaz ve yazılamaz; kiracıya ait olmayanlar da.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/products", new { name = "Free", price = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/categories", new { name = "Free" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/products/batch-delete", new { ids = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/shipping-quote?weightKg=1")).StatusCode);

        // Açık olduğunu kendisi söyleyenler: misafir sepeti (sonrasında token'ıyla tanınır) ve API şeması.
        var basket = await SendAsync(HttpMethod.Post, "/guest-baskets", new { guestBasketItems = new[] { new { productName = "Keyboard", quantity = 1 } } });
        Assert.Equal(HttpStatusCode.Created, basket.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/openapi/v1.json")).StatusCode);

        // Giriş yapan hesap kiracıya ait olmayan kaynağı yazar.
        var account = await RegisterAsync();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/products", new { name = $"Signed {Guid.NewGuid():N}", price = 1 }, account.Token)).StatusCode);
    }

    [Fact]
    public async Task RefreshToken_IsSingleUseAndReusingItClosesTheSession()
    {
        var email = NewEmail();
        var first = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/register", new { email, password = Password }));
        var second = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = first.RefreshToken }));
        var third = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = second.RefreshToken }));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/auth/me", token: third.AccessToken)).StatusCode);

        // Kullanılmış bir yenileme token'ı yeniden sunulursa (çalınmış olabilir) oturumun tamamı kapanır: en yeni
        // token'lar da artık geçmez ve yeniden giriş gerekir.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = first.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/auth/me", token: third.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = third.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = Password })).StatusCode);

        // Aynı token'ı aynı anda sunan isteklerden yalnızca biri yenilenir.
        var session = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = Password }));
        var race = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = session.RefreshToken })));
        Assert.Single(race, response => response.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public void RateLimit_CountsAnIpv6SubscriberAsOne()
    {
        // Bir abonenin /64 bloğundaki bütün adresler tek sayılır; farklı bloklar ve IPv4 adresleri ayrıdır.
        Assert.Equal(
            AuthEndpoints.PartitionOf(System.Net.IPAddress.Parse("2001:db8:1:2::1")),
            AuthEndpoints.PartitionOf(System.Net.IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff")));
        Assert.NotEqual(
            AuthEndpoints.PartitionOf(System.Net.IPAddress.Parse("2001:db8:1:2::1")),
            AuthEndpoints.PartitionOf(System.Net.IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("192.0.2.7", AuthEndpoints.PartitionOf(System.Net.IPAddress.Parse("::ffff:192.0.2.7")));
        Assert.NotEqual(
            AuthEndpoints.PartitionOf(System.Net.IPAddress.Parse("192.0.2.7")),
            AuthEndpoints.PartitionOf(System.Net.IPAddress.Parse("192.0.2.8")));
    }

    [Fact]
    public void DemoIdentity_IsRefusedOutsideDevelopment()
    {
        // Kimliği başlıklardan okuyan kip üretimde yanlışlıkla açılamaz: uygulama hiç açılmaz.
        using var factory = new WebApplicationFactory<ShopMessages>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("Shop:DemoIdentity", "true"));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Shop:DemoIdentity", exception.Message);
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

        // Geçerli token aynı kaynakları açar.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/products", token: account.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/orders", token: account.Token)).StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheSessionOnTheServer()
    {
        var email = NewEmail();
        var first = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/register", new { email, password = Password }));
        var second = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = Password }));
        var third = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = Password }));

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/auth/logout", token: first.AccessToken)).StatusCode);

        // Çıkış yapılan oturumun erişim token'ı da yenileme token'ı da artık kabul edilmez; süresinin dolması beklenmez.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/auth/me", token: first.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/orders", token: first.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/products", token: first.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = first.RefreshToken })).StatusCode);

        // Aynı hesabın öteki oturumları etkilenmez.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/auth/me", token: second.AccessToken)).StatusCode);

        // "Her yerden çık" hepsini kapatır.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/auth/logout-all", token: second.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/auth/me", token: second.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/auth/me", token: third.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = third.RefreshToken })).StatusCode);

        // Hesap yerinde durur: yeniden giriş yapılır.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = Password })).StatusCode);
    }

    [Fact]
    public async Task ForgottenPassword_IsResetWithTheMailedCode()
    {
        const string NewPassword = "a brand new passphrase";
        var email = NewEmail();
        var before = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/register", new { email, password = Password }));

        // Yanıt, e-postanın kayıtlı olup olmadığını söylemez; kayıtlı olmayana kod gönderilmez.
        var unknown = NewEmail();
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/auth/password/forgot", new { email = unknown })).StatusCode);
        Assert.Null(fixture.Mail.CodeFor(unknown, AccountCodePurpose.PasswordReset));

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/auth/password/forgot", new { email = email.ToUpperInvariant() })).StatusCode);
        var code = await ResetCodeAsync(email);
        Assert.Matches("^[0-9]{6}$", code);

        // Yanlış kod, kayıtlı olmayan e-posta ve kısa parola reddedilir.
        var wrong = code == "000000" ? "000001" : "000000";
        Assert.Equal(["code"], await ErrorsAsync(await SendAsync(HttpMethod.Post, "/auth/password/reset", new { email, code = wrong, newPassword = NewPassword })));
        Assert.Equal(["code"], await ErrorsAsync(await SendAsync(HttpMethod.Post, "/auth/password/reset", new { email = unknown, code, newPassword = NewPassword })));
        Assert.Equal(["newPassword"], await ErrorsAsync(await SendAsync(HttpMethod.Post, "/auth/password/reset", new { email, code, newPassword = "short" })));

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/auth/password/reset", new { email, code, newPassword = NewPassword })).StatusCode);

        // Eski parola ve eski oturumlar artık geçmez; kod ikinci kez kullanılamaz.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/auth/me", token: before.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = before.RefreshToken })).StatusCode);
        Assert.Equal(["code"], await ErrorsAsync(await SendAsync(HttpMethod.Post, "/auth/password/reset", new { email, code, newPassword = Password })));

        // Yeni parola geçer; kodu e-postasından okuyan kişi adresi de doğrulamış olur.
        var after = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = NewPassword }));
        Assert.True((await ReadAsync<AccountInfo>(await SendAsync(HttpMethod.Get, "/auth/me", token: after.AccessToken))).EmailVerified);
    }

    [Fact]
    public async Task MailedCode_HasAnAttemptBudgetThatANewCodeDoesNotRefill()
    {
        await using var factory = fixture.CreateAuthenticatedFactory(
            ("Shop:Auth:RequestsPerMinute", "100000"),
            ("Shop:Auth:CodeWindowSeconds", "3"));
        using var client = factory.CreateClient();
        var email = NewEmail();
        await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email, password = Password }));
        await SendAsync(client, HttpMethod.Post, "/auth/password/forgot", new { email });
        var code = await ResetCodeAsync(email);
        var wrong = code == "000000" ? "000001" : "000000";

        // Beş yanlış denemeden sonra doğru kod da kabul edilmez.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(["code"], await ErrorsAsync(await SendAsync(client, HttpMethod.Post, "/auth/password/reset", new { email, code = wrong, newPassword = Password })));
        }

        Assert.Equal(["code"], await ErrorsAsync(await SendAsync(client, HttpMethod.Post, "/auth/password/reset", new { email, code, newPassword = Password })));

        // Yeni bir kod istemek deneme hakkını yenilemez: "yeni kod iste, beş kez dene" döngüsüyle altı haneli kod bulunamaz.
        await SendAsync(client, HttpMethod.Post, "/auth/password/forgot", new { email });
        var second = await ResetCodeAsync(email, mails: 2);
        Assert.Equal(["code"], await ErrorsAsync(await SendAsync(client, HttpMethod.Post, "/auth/password/reset", new { email, code = second, newPassword = Password })));

        // Gönderim de sınırlıdır: pencerede üç koddan sonra yeni e-posta gitmez (yanıt yine aynıdır).
        await SendAsync(client, HttpMethod.Post, "/auth/password/forgot", new { email });
        await ResetCodeAsync(email, mails: 3);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client, HttpMethod.Post, "/auth/password/forgot", new { email })).StatusCode);
        await Task.Delay(300, CancellationToken);
        Assert.Equal(3, fixture.Mail.CountFor(email, AccountCodePurpose.PasswordReset));

        // Pencere dolunca hak yenilenir: yeni kod gönderilir ve kabul edilir.
        await Task.Delay(TimeSpan.FromSeconds(3.2), CancellationToken);
        await SendAsync(client, HttpMethod.Post, "/auth/password/forgot", new { email });
        var renewed = await ResetCodeAsync(email, mails: 4);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Post, "/auth/password/reset", new { email, code = renewed, newPassword = Password })).StatusCode);
    }

    [Fact]
    public async Task ChangingThePassword_CountsWrongGuessesTowardsTheLockout()
    {
        await using var factory = fixture.CreateAuthenticatedFactory(
            ("Shop:Auth:RequestsPerMinute", "100000"),
            ("Shop:Auth:MaxFailedLogins", "3"),
            ("Shop:Auth:LockoutSeconds", "600"));
        using var client = factory.CreateClient();
        var email = NewEmail();
        var session = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email, password = Password }));

        // Ele geçirilmiş bir erişim token'ı parolayı bu uçta sınırsız deneyemez: denemeler girişle aynı sayaca yazılır.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(
                ["currentPassword"],
                await ErrorsAsync(await SendAsync(client, HttpMethod.Post, "/auth/password/change", new { currentPassword = $"guess number {attempt}", newPassword = Password }, session.AccessToken)));
        }

        Assert.Equal(
            ["currentPassword"],
            await ErrorsAsync(await SendAsync(client, HttpMethod.Post, "/auth/password/change", new { currentPassword = Password, newPassword = "a different passphrase" }, session.AccessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Post, "/auth/login", new { email, password = Password })).StatusCode);
    }
    [Fact]
    public async Task Email_IsVerifiedWithTheMailedCode()
    {
        var email = NewEmail();
        var session = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/register", new { email, password = Password }));
        Assert.False((await ReadAsync<AccountInfo>(await SendAsync(HttpMethod.Get, "/auth/me", token: session.AccessToken))).EmailVerified);

        var code = fixture.Mail.CodeFor(email, AccountCodePurpose.EmailVerification)!;
        var wrong = code == "000000" ? "000001" : "000000";
        Assert.Equal(["code"], await ErrorsAsync(await SendAsync(HttpMethod.Post, "/auth/email/verify", new { code = wrong }, session.AccessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/email/verify", new { code })).StatusCode);

        // Yeniden gönderme yeni bir kod üretir; eskisi artık geçmez.
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/auth/email/resend", token: session.AccessToken)).StatusCode);
        var renewed = fixture.Mail.CodeFor(email, AccountCodePurpose.EmailVerification)!;

        if (renewed != code)
        {
            Assert.Equal(["code"], await ErrorsAsync(await SendAsync(HttpMethod.Post, "/auth/email/verify", new { code }, session.AccessToken)));
        }

        // Doğrulama yeni bir token verir; aynı oturumdur.
        var verified = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/email/verify", new { code = renewed }, session.AccessToken));
        Assert.True((await ReadAsync<AccountInfo>(await SendAsync(HttpMethod.Get, "/auth/me", token: verified.AccessToken))).EmailVerified);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/auth/me", token: session.AccessToken)).StatusCode);

        // Doğrulanmış hesapta bu uç kod sınamaz ve yeni token da vermez.
        var again = await SendAsync(HttpMethod.Post, "/auth/email/verify", new { code = "000000" }, verified.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Empty(await again.Content.ReadAsByteArrayAsync(CancellationToken));

        // Doğrulanmış hesaba yeniden kod gönderilmez.
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/auth/email/resend", token: verified.AccessToken)).StatusCode);
        Assert.Equal(renewed, fixture.Mail.CodeFor(email, AccountCodePurpose.EmailVerification));
    }

    [Fact]
    public async Task ChangingThePassword_ClosesTheOtherSessions()
    {
        const string NewPassword = "another long passphrase";
        var email = NewEmail();
        var current = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/register", new { email, password = Password }));
        var other = await ReadAsync<AccessTokenResponse>(await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = Password }));

        Assert.Equal(
            ["currentPassword"],
            await ErrorsAsync(await SendAsync(HttpMethod.Post, "/auth/password/change", new { currentPassword = "not the password", newPassword = NewPassword }, current.AccessToken)));
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Post, "/auth/password/change", new { currentPassword = Password, newPassword = NewPassword }, current.AccessToken)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/auth/me", token: current.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/auth/me", token: other.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/auth/login", new { email, password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task Account_IsLockedAfterRepeatedFailures()
    {
        await using var factory = fixture.CreateAuthenticatedFactory(
            ("Shop:Auth:RequestsPerMinute", "100000"),
            ("Shop:Auth:MaxFailedLogins", "3"),
            ("Shop:Auth:LockoutSeconds", "2"));
        using var client = factory.CreateClient();
        var email = NewEmail();
        await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email, password = Password }));

        // Denemeler aynı anda gelse de sayılır: sınır kadar deneme parolayı sınar, gerisi kilide takılır.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            SendAsync(client, HttpMethod.Post, "/auth/login", new { email, password = "wrong password" })));
        Assert.All(attempts, attempt => Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode));

        // Kilitli hesapta doğru parola da kabul edilmez ve yanıt yanlış paroladakiyle aynıdır.
        var locked = await SendAsync(client, HttpMethod.Post, "/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal(await DetailAsync(attempts[0]), await DetailAsync(locked));

        // Kilit süresi dolunca hesap açılır.
        await Task.Delay(TimeSpan.FromSeconds(2.5), CancellationToken);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/auth/login", new { email, password = Password })).StatusCode);

        // Başarılı giriş sayımı sıfırlar: iki yanlış denemeden sonra doğru parola geçer.
        await SendAsync(client, HttpMethod.Post, "/auth/login", new { email, password = "wrong password" });
        await SendAsync(client, HttpMethod.Post, "/auth/login", new { email, password = "wrong password" });
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/auth/login", new { email, password = Password })).StatusCode);
    }

    [Fact]
    public async Task CredentialEndpoints_AreRateLimitedPerAddress()
    {
        await using var factory = fixture.CreateAuthenticatedFactory(("Shop:Auth:RequestsPerMinute", "3"));
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Post, "/auth/login", new { email = NewEmail(), password = Password })).StatusCode);
        }

        // Sınır hesap başına değil adres başınadır: başka bir hesabı denemek de sayılır.
        var limited = await SendAsync(client, HttpMethod.Post, "/auth/login", new { email = NewEmail(), password = Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.False(string.IsNullOrEmpty(await DetailAsync(limited)));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, HttpMethod.Post, "/auth/password/forgot", new { email = NewEmail() })).StatusCode);

        // Parola denemeyen uçlar sınırın dışındadır.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Post, "/auth/refresh", new { refreshToken = "garbage" })).StatusCode);
    }

    [Fact]
    public async Task UnverifiedAccount_CanBeKeptOutUntilTheEmailIsVerified()
    {
        await using var factory = fixture.CreateAuthenticatedFactory(
            ("Shop:Auth:RequestsPerMinute", "100000"),
            ("Shop:Auth:RequireVerifiedEmail", "true"));
        using var client = factory.CreateClient();
        var email = NewEmail();
        var session = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email, password = Password }));

        // Doğrulanmamış hesap yalnızca kimlik uçlarını kullanır.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, "/orders", token: session.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, "/products", token: session.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: session.AccessToken)).StatusCode);

        var code = fixture.Mail.CodeFor(email, AccountCodePurpose.EmailVerification)!;
        var verified = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/email/verify", new { code }, session.AccessToken));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/orders", token: verified.AccessToken)).StatusCode);

        // Eski erişim token'ı doğrulanmamış claim'i taşır ve dışarıda kalır; yenilenen oturum geçer.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, "/orders", token: session.AccessToken)).StatusCode);
        var renewed = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/refresh", new { refreshToken = verified.RefreshToken }));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/orders", token: renewed.AccessToken)).StatusCode);

        // Token'ı atmak da işe yaramaz: giriş yapılmadan hiçbir kaynağa ulaşılmaz.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/products")).StatusCode);
    }

    /// <summary>Sıfırlama e-postası yanıt beklemeden gönderilir; adrese <paramref name="mails"/> e-posta gidene kadar beklenir.</summary>
    private async Task<string> ResetCodeAsync(string email, int mails = 1)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);

        while (fixture.Mail.CountFor(email, AccountCodePurpose.PasswordReset) < mails)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The reset code was not mailed.");
            await Task.Delay(25, CancellationToken);
        }

        return fixture.Mail.CodeFor(email, AccountCodePurpose.PasswordReset)!;
    }
    private static async Task<string[]> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(CancellationToken);
        return [.. problem.GetProperty("errors").EnumerateObject().Select(error => char.ToLowerInvariant(error.Name[0]) + error.Name[1..])];
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

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? token = null) =>
        SendAsync(_client, method, url, body, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body = null, string? token = null)
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

        return await client.SendAsync(request, CancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken)}");

        return (await response.Content.ReadFromJsonAsync<T>(CancellationToken))!;
    }
}