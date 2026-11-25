using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Auth;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Haldus.IntegrationTests;

/// <summary>
/// Bir kimlik sağlayıcıyla giriş: sağlayıcının JWT erişim token'larının kabulü (<c>Shop:Auth:Jwt</c>) ve backend'in
/// kendi yönlendirmeli girişi (<c>Shop:Auth:Oidc</c>). Sağlayıcı testin içindedir: keşif belgesini, imza anahtarını
/// ve token ucunu sunan bir işleyici; token'lar gerçekten imzalanır ve ASP.NET Core'un kendi doğrulayıcılarından geçer.
/// </summary>
[Collection(ShopCollection.Name)]
public sealed class ExternalSignInTests(ShopFixture fixture)
{
    private const string Password = "correct horse battery";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProviderToken_OpensALocalAccountWithItsOwnTenantOnce()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        var email = NewEmail();
        var subject = NewSubject();
        var token = provider.AccessToken(subject, email, emailVerified: true);

        var account = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: token));
        Assert.Equal((email, true, false), (account.Email, account.EmailVerified, account.IsStaff));
        Assert.NotEqual(Guid.Empty, account.TenantId);

        // Aynı kişi aynı hesaba döner; yeni bir token yeni bir hesap açmaz.
        var again = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject, email, emailVerified: true)));
        Assert.Equal((account.Id, account.TenantId), (again.Id, again.TenantId));

        // Haldus kullanıcıyı ve kiracıyı bu kimlikten okur: kiracıya ait kaynak yazılır ve yalnızca o kiracıya görünür.
        var order = await SendAsync(client, HttpMethod.Post, "/orders", new { customerName = "Ada" }, token, idempotencyKey: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, order.StatusCode);

        var other = provider.AccessToken(NewSubject(), NewEmail(), emailVerified: true);
        var theirs = await ReadAsync<JsonElement>(await SendAsync(client, HttpMethod.Get, "/orders", token: other));
        Assert.Equal(0, theirs.GetProperty("totalCount").GetInt32());
        var mine = await ReadAsync<JsonElement>(await SendAsync(client, HttpMethod.Get, "/orders", token: token));
        Assert.Equal(1, mine.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ProviderToken_IsRejectedUnlessItIsMeantForThisApiAndSignedByTheProvider()
    {
        using var provider = new FakeProvider();
        using var stranger = RSA.Create(2048);
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        var subject = NewSubject();

        var rejected = new Dictionary<string, string>
        {
            ["another audience"] = provider.AccessToken(subject, audience: "another-api"),
            ["another issuer"] = provider.AccessToken(subject, issuer: "https://evil.test"),
            ["expired"] = provider.AccessToken(subject, expiresIn: TimeSpan.FromMinutes(-10)),
            ["another key"] = provider.AccessToken(subject, key: stranger),
            ["unsigned"] = FakeProvider.Unsigned(provider.AccessToken(subject)),
            ["tampered"] = FakeProvider.Tampered(provider.AccessToken(subject), subject, NewSubject()),
            ["no subject"] = provider.AccessToken(subject: null),

            // Verildiği anı söylemeyen token ilk istekten reddedilir ("her yerden çık" o ana dayanır).
            ["no issue time"] = provider.AccessToken(subject, iat: false, nbf: false),

            // Başka bir alan onun yerine geçmez: `nbf` sabit ya da geriye çekilmiş olabilir.
            ["only a not-before time"] = provider.AccessToken(subject, iat: false),

            // Kabul edilen saat farkından daha ileride verilmiş görünen token (doğrulayıcı `iat`'e bakmaz).
            ["issued too far ahead"] = provider.AccessToken(subject, issuedIn: TimeSpan.FromMinutes(10), nbf: false),
        };

        foreach (var (reason, token) in rejected)
        {
            var response = await SendAsync(client, HttpMethod.Get, "/auth/me", token: token);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{reason}: {(int)response.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject))).StatusCode);
    }

    [Fact]
    public async Task StaffRole_FollowsTheAddressOnlyWhenTheProviderVerifiedIt()
    {
        using var provider = new FakeProvider();
        var staff = NewEmail();
        var claimed = NewEmail();
        await using var factory = CreateFactory(provider, ("Shop:Auth:Staff:0", staff), ("Shop:Auth:Staff:1", claimed));
        using var client = factory.CreateClient();
        var product = new { name = $"Kettle {Guid.NewGuid():N}", price = 10 };

        // Sağlayıcının doğruladığı adres personel listesindeyse ortak kaynak yazılır.
        var verified = provider.AccessToken(NewSubject(), staff, emailVerified: true);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client, HttpMethod.Post, "/products", product, verified)).StatusCode);

        // Doğrulanmamış adres yalnızca bir iddiadır: hesaba yazılmaz ve rol getirmez.
        var unverified = provider.AccessToken(NewSubject(), claimed, emailVerified: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Post, "/products", product, unverified)).StatusCode);
        var account = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: unverified));
        Assert.Equal((false, false), (account.EmailVerified, account.IsStaff));
        Assert.NotEqual(claimed, account.Email);
    }

    [Fact]
    public async Task SignOutEverywhere_AlsoEndsTheTokensTheProviderHasAlreadyIssued()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        var subject = NewSubject();
        var token = provider.AccessToken(subject);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Post, "/auth/logout-all", token: token)).StatusCode);

        // Sağlayıcının token'ının sunucuda iptal edilecek bir oturumu yoktur; hesap, o andan önce verilenleri reddeder.
        // Çalınmış bir token "her yerden çık"tan sonra, süresi dolmamış olsa da, işe yaramaz.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: token)).StatusCode);

        // Sağlayıcıda yeniden giriş yapınca alınan token geçer.
        await Task.Delay(TimeSpan.FromSeconds(1.2), CancellationToken);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject))).StatusCode);
    }

    [Fact]
    public async Task SignOutEverywhere_EndsTheTokenThatAsksForItEvenWhenTheClockOfTheProviderIsAhead()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        var subject = NewSubject();

        // Sağlayıcının saati yarım dakika ileride: token "şimdi"den sonra verilmiş görünür.
        var token = provider.AccessToken(subject, issuedIn: TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Post, "/auth/logout-all", token: token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: token)).StatusCode);

        // Sınır, isteği yapan token'ın verildiği andır; sağlayıcının ondan sonra verdiği token geçer.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject, issuedIn: TimeSpan.FromSeconds(40)))).StatusCode);
    }

    [Fact]
    public async Task SignOutEverywhere_NeverMovesTheLimitBack()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(
            provider,
            ("Shop:Auth:Oidc:Authority", FakeProvider.Issuer),
            ("Shop:Auth:Oidc:ClientId", FakeProvider.ServerClient),
            ("Shop:Auth:Oidc:ClientSecret", FakeProvider.ServerSecret));
        using var client = factory.CreateClient();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        var subject = NewSubject();

        // Saati ileride bir yoldan gelen çıkış sınırı ileriye koyar.
        var ahead = provider.AccessToken(subject, issuedIn: TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Post, "/auth/logout-all", token: ahead)).StatusCode);

        // Aynı kişi çerezle girip (bizim saatimizle) yeniden "her yerden çık" der: sınır "şimdi"ye geri çekilmez;
        // çekilseydi, ilk çıkışın öldürdüğü token'lar (aşağıdaki gibi, arada verilmiş görünenler) yeniden geçerdi.
        Assert.Equal(HttpStatusCode.Redirect, (await ServerSignInAsync(browser, provider, subject, NewEmail())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(browser, HttpMethod.Post, "/auth/logout-all", ownRequest: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject, issuedIn: TimeSpan.FromSeconds(15)))).StatusCode);
    }

    [Fact]
    public async Task VerifiedAddress_IsTakenWhenItArrivesAfterTheAccountWasOpened()
    {
        using var provider = new FakeProvider();
        var staff = NewEmail();
        await using var factory = CreateFactory(provider, ("Shop:Auth:Staff:0", staff));
        using var client = factory.CreateClient();
        var subject = NewSubject();

        // Erişim token'ı çoğu zaman adresi taşımaz: hesap adressiz ve doğrulanmamış açılır.
        var opened = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject)));
        Assert.Equal((false, false), (opened.EmailVerified, opened.IsStaff));

        // Doğrulanmamış bir iddia yine yazılmaz.
        var claimed = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject, staff, emailVerified: false)));
        Assert.Equal((opened.Email, false), (claimed.Email, claimed.EmailVerified));

        // Sağlayıcı adresi sonradan doğrulanmış olarak söylerse hesaba yazılır; yoksa hesap sonsuza kadar
        // doğrulanmamış kalırdı (adresini bizim kodumuzla da doğrulayamaz).
        var verified = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject, staff, emailVerified: true)));
        Assert.Equal((opened.Id, opened.TenantId, staff, true, true), (verified.Id, verified.TenantId, verified.Email, verified.EmailVerified, verified.IsStaff));

        // Yazılan adres kalır: sonraki token başka bir adres söylese de değişmez, söylemese de silinmez.
        var later = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject, NewEmail(), emailVerified: true)));
        Assert.Equal((staff, true), (later.Email, later.EmailVerified));
        Assert.Equal(staff, (await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject)))).Email);

        // Adres bir parola hesabında dursa da sonradan yazılır (benzersizlik yalnızca parola hesapları arasındadır).
        var held = NewEmail();
        await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email = held, password = Password }));
        var other = NewSubject();
        Assert.NotEqual(held, (await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(other)))).Email);
        var adopted = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(other, held, emailVerified: true)));
        Assert.Equal((held, true), (adopted.Email, adopted.EmailVerified));
    }

    [Fact]
    public async Task EmailClaim_IsNotBelievedUnlessTheProviderIsTrustedForIt()
    {
        using var provider = new FakeProvider();
        var staff = NewEmail();

        // Varsayılan: sağlayıcının "doğrulandı" beyanına güvenilmez. Herkesin hesap açabildiği bir sağlayıcıda bu
        // beyan, personel adresini yazan herkese rol verirdi.
        await using var factory = CreateFactory(provider, ("Shop:Auth:Staff:0", staff), ("Shop:Auth:Jwt:TrustEmailVerified", "false"));
        using var client = factory.CreateClient();
        var token = provider.AccessToken(NewSubject(), staff, emailVerified: true);

        var account = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: token));
        Assert.Equal((false, false), (account.EmailVerified, account.IsStaff));
        Assert.NotEqual(staff, account.Email);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SendAsync(client, HttpMethod.Post, "/products", new { name = $"Kettle {Guid.NewGuid():N}", price = 10 }, token)).StatusCode);

        // Güvenilmeyen sağlayıcının beyanı adresin asıl sahibini de etkilemez: parola ile kaydolup adresini kodla
        // doğrulayınca rolü alır.
        var owner = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email = staff, password = Password }));
        var verified = await ReadAsync<AccessTokenResponse>(await SendAsync(
            client, HttpMethod.Post, "/auth/email/verify", new { code = fixture.Mail.CodeFor(staff, AccountCodePurpose.EmailVerification) }, owner.AccessToken));
        Assert.Equal(
            HttpStatusCode.Created,
            (await SendAsync(client, HttpMethod.Post, "/products", new { name = $"Kettle {Guid.NewGuid():N}", price = 10 }, verified.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task AddressOfAPasswordAccount_IsNotTakenOverByAnExternalIdentity()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        var email = NewEmail();

        var session = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email, password = Password }));
        var local = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: session.AccessToken));

        // Sağlayıcı aynı adresi doğrulamış olsa da var olan hesaba bağlanılmaz: dış kimlik kendi ayrı hesabını ve
        // kiracısını alır. Adres iki hesapta da durur (benzersizlik yalnızca parola hesapları arasındadır).
        var external = await ReadAsync<AccountInfo>(
            await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(NewSubject(), email, emailVerified: true)));

        Assert.NotEqual(local.Id, external.Id);
        Assert.NotEqual(local.TenantId, external.TenantId);
        Assert.Equal((email, true), (external.Email, external.EmailVerified));

        // Parola hesabı olduğu gibi kalır: doğrulanmış sayılmaz, kendi token'ları ve yenilemesi çalışır.
        var same = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: session.AccessToken));
        Assert.Equal((local.Id, false), (same.Id, same.EmailVerified));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/auth/refresh", new { refreshToken = session.RefreshToken })).StatusCode);

        // Adresle çalışan parola uçları yalnızca parola hesabını görür: giriş ona gider, kayıt adresi alınmış sayar,
        // sıfırlama kodu ona gönderilir (dış hesaba parola koyulamaz).
        var signedIn = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/login", new { email, password = Password }));
        Assert.Equal(local.Id, (await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: signedIn.AccessToken))).Id);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, "/auth/register", new { email, password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client, HttpMethod.Post, "/auth/password/forgot", new { email })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(
            client, HttpMethod.Post, "/auth/password/reset", new { email, code = await ResetCodeAsync(email), newPassword = $"{Password} again" })).StatusCode);
        var afterReset = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/login", new { email, password = $"{Password} again" }));
        Assert.Equal(local.Id, (await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: afterReset.AccessToken))).Id);
    }

    [Fact]
    public async Task AddressHeldByAnUnverifiedPasswordAccount_DoesNotKeepItsOwnerFromTheRole()
    {
        using var provider = new FakeProvider();
        var staff = NewEmail();
        await using var factory = CreateFactory(provider, ("Shop:Auth:Staff:0", staff));
        using var client = factory.CreateClient();
        var product = new { name = $"Kettle {Guid.NewGuid():N}", price = 10 };

        // Biri personel adresiyle önceden bir parola hesabı açar. Adresi doğrulayamadığı için rol alamaz...
        var squatter = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email = staff, password = Password }));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Post, "/products", product, squatter.AccessToken)).StatusCode);

        // ...ve adresin sahibinin, sağlayıcının doğruladığı adresle gelince rolü almasını da engelleyemez. (Adres bütün
        // hesaplar arasında benzersizken sahibi adressiz açılıyor ve hiçbir zaman personel olamıyordu.)
        var owner = provider.AccessToken(NewSubject(), staff, emailVerified: true);
        var account = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: owner));
        Assert.Equal((staff, true, true), (account.Email, account.EmailVerified, account.IsStaff));
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client, HttpMethod.Post, "/products", product, owner)).StatusCode);

        // Parola hesabı yine rol alamaz (token'ı yenilense de).
        var renewed = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/refresh", new { refreshToken = squatter.RefreshToken }));
        Assert.False((await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: renewed.AccessToken))).IsStaff);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SendAsync(client, HttpMethod.Post, "/products", product with { name = $"Kettle {Guid.NewGuid():N}" }, renewed.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task StaffAddressHeldByAProviderAccount_GivesNoRoleThroughAPassword()
    {
        using var provider = new FakeProvider();
        var first = NewEmail();
        var second = NewEmail();
        await using var factory = CreateFactory(provider, ("Shop:Auth:Staff:0", first), ("Shop:Auth:Staff:1", second));
        using var client = factory.CreateClient();

        static object Product() => new { name = $"Kettle {Guid.NewGuid():N}", price = 10 };

        async Task<AccountInfo> MeAsync(string token) => await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: token));

        async Task<HttpStatusCode> WriteAsync(string token) => (await SendAsync(client, HttpMethod.Post, "/products", Product(), token)).StatusCode;

        // 1. Personel sağlayıcıyla girer: adresinin yetkilisi artık sağlayıcıdır.
        var staffToken = provider.AccessToken(NewSubject(), first, emailVerified: true);
        var staff = await MeAsync(staffToken);
        Assert.True(staff.IsStaff);

        // Personelin posta kutusunu bir kez okuyabilen biri aynı adresle bir parola hesabı açar ve kodla doğrular.
        // Hesap açılır (adres parola hesapları arasında boştur) ve doğrulanır, ama rol alamaz: alsaydı, role
        // sağlayıcının denetimlerinin (iki adımlı doğrulama, hesabın kapatılması) dışında kalan bir parolayla ulaşırdı.
        var registered = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email = first, password = Password }));
        var verified = await ReadAsync<AccessTokenResponse>(await SendAsync(
            client, HttpMethod.Post, "/auth/email/verify", new { code = fixture.Mail.CodeFor(first, AccountCodePurpose.EmailVerification) }, registered.AccessToken));
        var account = await MeAsync(verified.AccessToken);
        Assert.NotEqual(staff.Id, account.Id);
        Assert.Equal((first, true, false), (account.Email, account.EmailVerified, account.IsStaff));
        Assert.Equal(HttpStatusCode.Forbidden, await WriteAsync(verified.AccessToken));

        // Yeniden giriş ve parola sıfırlama da rol getirmez; ikisi de parola hesabında kalır (sağlayıcının hesabına
        // parolayla ulaşılamaz).
        var again = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/login", new { email = first, password = Password }));
        Assert.Equal(HttpStatusCode.Forbidden, await WriteAsync(again.AccessToken));
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client, HttpMethod.Post, "/auth/password/forgot", new { email = first })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(
            client, HttpMethod.Post, "/auth/password/reset", new { email = first, code = await ResetCodeAsync(first), newPassword = $"{Password} again" })).StatusCode);
        var reset = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/login", new { email = first, password = $"{Password} again" }));
        Assert.Equal((account.Id, false), ((await MeAsync(reset.AccessToken)).Id, (await MeAsync(reset.AccessToken)).IsStaff));
        Assert.Equal(HttpStatusCode.Forbidden, await WriteAsync(reset.AccessToken));

        // Sağlayıcının hesabı olduğu gibidir.
        Assert.Equal(HttpStatusCode.Created, await WriteAsync(staffToken));

        // 2. Sıra tersine: parola hesabı önce açılır, adresini doğrular ve personeldir.
        var early = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email = second, password = Password }));
        var earlyVerified = await ReadAsync<AccessTokenResponse>(await SendAsync(
            client, HttpMethod.Post, "/auth/email/verify", new { code = fixture.Mail.CodeFor(second, AccountCodePurpose.EmailVerification) }, early.AccessToken));
        Assert.Equal(HttpStatusCode.Created, await WriteAsync(earlyVerified.AccessToken));

        // Sağlayıcı aynı adresi doğrulanmış olarak getirince yetki ona geçer: parola hesabı, token'ı yenilenince rolünü
        // kaybeder (kuralın sırayla ilgisi yoktur; rol her token verilişinde yeniden sorulur).
        Assert.True((await MeAsync(provider.AccessToken(NewSubject(), second, emailVerified: true))).IsStaff);
        var later = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/refresh", new { refreshToken = earlyVerified.RefreshToken }));
        Assert.False((await MeAsync(later.AccessToken)).IsStaff);
        Assert.Equal(HttpStatusCode.Forbidden, await WriteAsync(later.AccessToken));
    }

    [Fact]
    public async Task AccountThatCannotBeWritten_IsAnErrorOfOursNotARejectedSignIn()
    {
        using var provider = new FakeProvider();
        var accounts = new FailingAccounts();
        await using var factory = CreateFactory(
                provider,
                ("Shop:Auth:Oidc:Authority", FakeProvider.Issuer),
                ("Shop:Auth:Oidc:ClientId", FakeProvider.ServerClient),
                ("Shop:Auth:Oidc:ClientSecret", FakeProvider.ServerSecret))
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(accounts))));
        using var client = factory.CreateClient();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        var token = provider.AccessToken(NewSubject());
        var subject = NewSubject();

        // Hesap yazılamıyor (veritabanı arızası). Bu, token'ın veya girişin reddi (401 / 400) değildir: istemci
        // oturumunu kapatmamalı, hata günlüğe düşmelidir. İşlenmeyen hata olarak sürer (500).
        accounts.Fail = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: token)).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await ServerSignInAsync(browser, provider, subject, NewEmail())).StatusCode);

        // Arıza geçince aynı token ve aynı kişi hesabını açar: yarım kalmış bir kayıt yoktur.
        accounts.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: token)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await ServerSignInAsync(browser, provider, subject, NewEmail())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(browser, HttpMethod.Get, "/auth/me")).StatusCode);
    }

    [Fact]
    public async Task AccountOpenedWithoutAnAddress_IsReportedWhenAVerifiedAddressIsRequired()
    {
        using var provider = new FakeProvider();
        var logs = new CapturedLogs();
        await using var factory = CreateFactory(provider, ("Shop:Auth:RequireVerifiedEmail", "true"))
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(logs)));
        using var client = factory.CreateClient();
        var subject = NewSubject();

        // Erişim token'ı adresi taşımıyor: hesap açılır ama kimlik uçlarının dışında hiçbir şey yapamaz. Açılış
        // denetimi bunu göremez; işletmeci günlükteki uyarıdan öğrenir (hesap başına bir kez).
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, "/orders", token: provider.AccessToken(subject))).StatusCode);
        Assert.Single(logs.Warnings("ExternalSignIn"), message => message.Contains("RequireVerifiedEmail", StringComparison.Ordinal));

        // Doğrulanmış adresle gelen hesap için uyarı yazılmaz; adres sonradan gelince ilk hesap da açılır.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/orders", token: provider.AccessToken(NewSubject(), NewEmail(), emailVerified: true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/orders", token: provider.AccessToken(subject, NewEmail(), emailVerified: true))).StatusCode);
        Assert.Single(logs.Warnings("ExternalSignIn"), message => message.Contains("RequireVerifiedEmail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PasswordEndpoints_NeverGiveAnExternalAccountAPassword()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        var email = NewEmail();
        var token = provider.AccessToken(NewSubject(), email, emailVerified: true);

        // Sağlayıcının doğruladığı gerçek bir adresi olan dış hesap.
        var account = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: token));
        Assert.Equal((email, true), (account.Email, account.EmailVerified));

        // "Parolamı unuttum" bu hesabı, hiç yokmuş gibi yanıtlar ve kod göndermez: adresin posta kutusunu bir kez
        // okuyabilen biri hesaba kalıcı bir parola koyup sağlayıcının denetimlerini aşamaz.
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client, HttpMethod.Post, "/auth/password/forgot", new { email })).StatusCode);
        await Task.Delay(500, CancellationToken);
        Assert.Equal(0, fixture.Mail.CountFor(email, AccountCodePurpose.PasswordReset));
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(client, HttpMethod.Post, "/auth/password/reset", new { email, code = "ABCDEFGH", newPassword = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Post, "/auth/login", new { email, password = Password })).StatusCode);

        // Oturumu açıkken de parola koyamaz ve bizim token'larımızı veren doğrulama ucunu kullanamaz.
        foreach (var (url, body) in new (string, object?)[]
        {
            ("/auth/password/change", new { currentPassword = "anything at all", newPassword = Password }),
            ("/auth/email/verify", new { code = "ABCDEFGH" }),
            ("/auth/email/resend", null),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, url, body, token)).StatusCode);
        }

        Assert.Equal(0, fixture.Mail.CountFor(email, AccountCodePurpose.EmailVerification));
    }

    [Fact]
    public async Task TrustTurnedOffLater_TakesTheRoleAwayFromAccountsOpenedWhileItWasOn()
    {
        var staff = NewEmail();
        var subject = NewSubject();
        var product = new { name = $"Kettle {Guid.NewGuid():N}", price = 10 };

        using (var provider = new FakeProvider())
        {
            await using var trusting = CreateFactory(provider, ("Shop:Auth:Staff:0", staff));
            using var client = trusting.CreateClient();
            Assert.Equal(
                HttpStatusCode.Created,
                (await SendAsync(client, HttpMethod.Post, "/products", product, provider.AccessToken(subject, staff, emailVerified: true))).StatusCode);

            var trusted = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(subject, staff, emailVerified: true)));
            Assert.Equal((true, true), (trusted.EmailVerified, trusted.IsStaff));
        }

        // Aynı sağlayıcı, aynı kişi; ama adresi doğruladığına artık güvenilmiyor. (Güvenilen başka bir sağlayıcı da
        // ayarlı: aşağıdaki kural "herhangi bir sağlayıcıya güveniliyor mu"ya değil, adresi yazan sağlayıcıya bakar.)
        using var later = new FakeProvider();
        await using var factory = CreateFactory(
            later,
            ("Shop:Auth:Staff:0", staff),
            ("Shop:Auth:Jwt:TrustEmailVerified", "false"),
            ("Shop:Auth:Oidc:Authority", "https://another.idp.test"),
            ("Shop:Auth:Oidc:ClientId", "another-server"),
            ("Shop:Auth:Oidc:ClientSecret", "another-secret"));
        using var cautious = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SendAsync(cautious, HttpMethod.Post, "/products", product with { name = $"Kettle {Guid.NewGuid():N}" }, later.AccessToken(subject, staff, emailVerified: true))).StatusCode);

        // Hesap ucu da isteğin gerçekten taşıdığı yetkiyi söyler; kayıtta duran eski doğrulamayı değil. Arayüzler
        // yazamayacakları bir ürün için "kaydet" düğmesi göstermesin.
        var account = await ReadAsync<AccountInfo>(await SendAsync(cautious, HttpMethod.Get, "/auth/me", token: later.AccessToken(subject, staff, emailVerified: true)));
        Assert.Equal((false, false), (account.EmailVerified, account.IsStaff));

        // Artık güvenilmeyen sağlayıcının eskiden yazdığı adres, adresin sahibini rolünden de etmez: parola ile kaydolup
        // adresini kodla doğrulayan hesap personeldir. ("Adresin yetkilisi sağlayıcıdır" kuralı yalnızca şu an
        // güvenilen sağlayıcının hesapları için işler.)
        var owner = await ReadAsync<AccessTokenResponse>(await SendAsync(cautious, HttpMethod.Post, "/auth/register", new { email = staff, password = Password }));
        var verified = await ReadAsync<AccessTokenResponse>(await SendAsync(
            cautious, HttpMethod.Post, "/auth/email/verify", new { code = fixture.Mail.CodeFor(staff, AccountCodePurpose.EmailVerification) }, owner.AccessToken));
        Assert.True((await ReadAsync<AccountInfo>(await SendAsync(cautious, HttpMethod.Get, "/auth/me", token: verified.AccessToken))).IsStaff);
        Assert.Equal(
            HttpStatusCode.Created,
            (await SendAsync(cautious, HttpMethod.Post, "/products", product with { name = $"Kettle {Guid.NewGuid():N}" }, verified.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task StaffThroughProviderOnly_KeepsTheRoleFromEveryPasswordAccount()
    {
        using var provider = new FakeProvider();
        var staff = NewEmail();
        await using var factory = CreateFactory(provider, ("Shop:Auth:Staff:0", staff), ("Shop:Auth:StaffThroughProviderOnly", "true"));
        using var client = factory.CreateClient();
        var product = new { name = $"Kettle {Guid.NewGuid():N}", price = 10 };

        // Personel sağlayıcıyla henüz hiç girmedi. Posta kutusunu bir kez okuyabilen biri adresle parola hesabı açıp
        // doğrular; ayar açıkken bu, rol getirmez (kapalıyken getirir: parola hesabında rolün dayanağı o koddur).
        var registered = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email = staff, password = Password }));
        var verified = await ReadAsync<AccessTokenResponse>(await SendAsync(
            client, HttpMethod.Post, "/auth/email/verify", new { code = fixture.Mail.CodeFor(staff, AccountCodePurpose.EmailVerification) }, registered.AccessToken));
        var account = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: verified.AccessToken));
        Assert.Equal((true, false), (account.EmailVerified, account.IsStaff));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Post, "/products", product, verified.AccessToken)).StatusCode);

        // Sağlayıcıyla gelen personel rolünü alır.
        var token = provider.AccessToken(NewSubject(), staff, emailVerified: true);
        Assert.True((await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: token))).IsStaff);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client, HttpMethod.Post, "/products", product, token)).StatusCode);
    }

    [Fact]
    public async Task Config_TellsTheClientsWhichSignInsAreOffered()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(provider);

        var configured = await ReadAsync<AuthConfig>(await SendAsync(factory.CreateClient(), HttpMethod.Get, "/auth/config"));
        Assert.Equal(new AuthConfig(true, new ExternalProviderConfig(FakeProvider.Issuer, "shop-panel", "openid profile email"), ServerSignIn: false), configured);

        // Varsayılan ayarla yalnızca parola girişi vardır; sağlayıcının token'ı bizim token'ımız sayılıp reddedilir.
        using var plain = fixture.CreateAuthenticatedAppClient();
        Assert.Equal(new AuthConfig(true, null, ServerSignIn: false), await ReadAsync<AuthConfig>(await SendAsync(plain, HttpMethod.Get, "/auth/config")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(plain, HttpMethod.Get, "/auth/me", token: provider.AccessToken(NewSubject()))).StatusCode);

        // Yönlendirmeli giriş ucu yoktur: istek sağlayıcıya yönlendirilmez (eşleşmeyen yol da giriş ister).
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(plain, HttpMethod.Get, "/auth/oidc/sign-in")).StatusCode);
    }

    [Fact]
    public void InsecureOrIncompleteSettings_StopTheApplication()
    {
        var invalid = new (string Key, string Value)[][]
        {
            [("Shop:Auth:Jwt:Authority", "https://idp.test")],
            [("Shop:Auth:Jwt:Authority", "http://idp.test"), ("Shop:Auth:Jwt:Audience", "shop-api")],
            [("Shop:Auth:Jwt:Authority", "idp.test"), ("Shop:Auth:Jwt:Audience", "shop-api")],
            [("Shop:Auth:Oidc:Authority", "https://idp.test"), ("Shop:Auth:Oidc:ClientId", "shop-server")],

            // API'nin audience'ı bir istemcinin kimliği olamaz: kimlik token'ı da erişim token'ı sayılırdı.
            [("Shop:Auth:Jwt:Authority", "https://idp.test"), ("Shop:Auth:Jwt:Audience", "shop-app"), ("Shop:Auth:Jwt:ClientId", "shop-app")],

            // Sondaki eğik çizgi audience'ı bir istemcinin kimliğinden ayırmaz (doğrulayıcı onu yok sayar).
            [("Shop:Auth:Jwt:Authority", "https://idp.test"), ("Shop:Auth:Jwt:Audience", "shop-app/"), ("Shop:Auth:Jwt:ClientId", "shop-app")],

            // Aynı sağlayıcının başka bir yazımı (büyük harf, sunucu adının sonunda nokta) da aynı sağlayıcıdır.
            [
                ("Shop:Auth:Jwt:Authority", "https://idp.test"), ("Shop:Auth:Jwt:Audience", "shop-api"), ("Shop:Auth:Jwt:TrustEmailVerified", "true"),
                ("Shop:Auth:Oidc:Authority", "https://IDP.test.:443/"), ("Shop:Auth:Oidc:ClientId", "shop-server"), ("Shop:Auth:Oidc:ClientSecret", "secret"),
            ],

            // Öteki bölümün istemcisi de bir istemcidir, sağlayıcının adresi başka yazılmış olsa bile.
            [
                ("Shop:Auth:Jwt:Authority", "https://idp.test"), ("Shop:Auth:Jwt:Audience", "shop-server"),
                ("Shop:Auth:Oidc:Authority", "https://login.idp.test"), ("Shop:Auth:Oidc:ClientId", "shop-server"), ("Shop:Auth:Oidc:ClientSecret", "secret"),
            ],

            // Çerez oturumunun ömrü sınırlıdır.
            [
                ("Shop:Auth:Oidc:Authority", "https://idp.test"), ("Shop:Auth:Oidc:ClientId", "shop-server"), ("Shop:Auth:Oidc:ClientSecret", "secret"),
                ("Shop:Auth:Oidc:SessionMinutes", "0"),
            ],

            // Personel yalnızca sağlayıcıyla gelebilecekse adreslerine güvenilen bir sağlayıcı olmalıdır.
            [("Shop:Auth:StaffThroughProviderOnly", "true")],
            [("Shop:Auth:Jwt:Authority", "https://idp.test"), ("Shop:Auth:Jwt:Audience", "shop-api"), ("Shop:Auth:StaffThroughProviderOnly", "true")],

            // Doğrulanmış adres zorunluyken adresine güvenilmeyen sağlayıcının hesapları hiçbir şey yapamazdı.
            [("Shop:Auth:Jwt:Authority", "https://idp.test"), ("Shop:Auth:Jwt:Audience", "shop-api"), ("Shop:Auth:RequireVerifiedEmail", "true")],

            // Aynı sağlayıcıya iki yoldan farklı güvenilemez.
            [
                ("Shop:Auth:Jwt:Authority", "https://idp.test"), ("Shop:Auth:Jwt:Audience", "shop-api"), ("Shop:Auth:Jwt:TrustEmailVerified", "true"),
                ("Shop:Auth:Oidc:Authority", "https://idp.test/"), ("Shop:Auth:Oidc:ClientId", "shop-server"), ("Shop:Auth:Oidc:ClientSecret", "secret"),
            ],
        };

        foreach (var settings in invalid)
        {
            using var factory = fixture.CreateAuthenticatedFactory(settings);
            var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
            Assert.StartsWith("Shop:Auth:", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ServerSignIn_RedirectsToTheProviderAndOpensACookieSession()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(
            provider,
            ("Shop:Auth:Oidc:Authority", FakeProvider.Issuer),
            ("Shop:Auth:Oidc:ClientId", FakeProvider.ServerClient),
            ("Shop:Auth:Oidc:ClientSecret", FakeProvider.ServerSecret));

        // Oturum çerezi yalnızca HTTPS'te gönderilir; tarayıcı gibi çerez tutan ve yönlendirmeyi izlemeyen bir istemci.
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        Assert.True((await ReadAsync<AuthConfig>(await SendAsync(browser, HttpMethod.Get, "/auth/config"))).ServerSignIn);

        // 1. Giriş isteği tarayıcıyı sağlayıcıya yönlendirir (Authorization Code + PKCE).
        var challenge = await SendAsync(browser, HttpMethod.Get, "/auth/oidc/sign-in?returnUrl=/auth/me");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var authorize = challenge.Headers.Location!;
        var query = HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal($"{FakeProvider.Issuer}/authorize", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal((FakeProvider.ServerClient, "code", "S256", "https://localhost/auth/oidc/callback"), (query["client_id"], query["response_type"], query["code_challenge_method"], query["redirect_uri"]));

        // 2. Sağlayıcı kullanıcıyı kodla geri gönderir; backend kodu token'la değiştirir ve oturum çerezini yazar.
        var email = NewEmail();
        var code = provider.Authorize(query["nonce"]!, query["code_challenge"]!, NewSubject(), email);
        using var callback = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["state"] = query["state"]! });
        var signedIn = await browser.PostAsync("/auth/oidc/callback", callback, CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/auth/me", signedIn.Headers.Location!.OriginalString);

        // 3. Çerez artık kimliktir.
        var account = await ReadAsync<AccountInfo>(await SendAsync(browser, HttpMethod.Get, "/auth/me"));
        Assert.Equal((email, true), (account.Email, account.EmailVerified));

        // Çerez kendiliğinden gönderildiği için yazan istek, başka bir sitenin ekleyemeyeceği başlığı taşımalıdır.
        var forged = await SendAsync(browser, HttpMethod.Post, "/orders", new { customerName = "Ada" }, idempotencyKey: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        var written = await SendAsync(browser, HttpMethod.Post, "/orders", new { customerName = "Ada" }, idempotencyKey: Guid.NewGuid().ToString(), ownRequest: true);
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);

        // 4. Çıkış ucu çerezi sildirir ve oturumu sunucuda da kapatır: çıkıştan önce kopyalanmış bir çerez de artık
        // kimlik değildir (ve sunulduğunda tarayıcıdan sildirilir).
        var copied = string.Join("; ", signedIn.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';')[0])
            .Where(value => value.StartsWith(ExternalSignIn.CookieName, StringComparison.Ordinal) && !value.EndsWith('=')));
        using var thief = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });

        async Task<HttpResponseMessage> ReplayAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
            request.Headers.Add("Cookie", copied);
            return await thief.SendAsync(request, CancellationToken);
        }

        Assert.Equal(HttpStatusCode.OK, (await ReplayAsync()).StatusCode);

        var signedOut = await SendAsync(browser, HttpMethod.Post, "/auth/oidc/sign-out", ownRequest: true);
        Assert.True(signedOut.IsSuccessStatusCode);
        Assert.Contains(signedOut.Headers.GetValues("Set-Cookie"), value => value.StartsWith($"{ExternalSignIn.CookieName}=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(browser, HttpMethod.Get, "/auth/me")).StatusCode);

        var replayed = await ReplayAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Contains(replayed.Headers.GetValues("Set-Cookie"), value => value.StartsWith($"{ExternalSignIn.CookieName}=;", StringComparison.Ordinal));

        // 5. "Her yerden çık" da çerezin oturumunu kapatır: çerez sekiz saat boyunca geçerli kalmaz.
        Assert.Equal(HttpStatusCode.Redirect, (await ServerSignInAsync(browser, provider, NewSubject(), NewEmail())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(browser, HttpMethod.Get, "/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(browser, HttpMethod.Post, "/auth/logout-all", ownRequest: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(browser, HttpMethod.Get, "/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ServerSignIn_ProtectsSignOutAndAnswersABrokenCallbackWithoutFailing()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(
            provider,
            ("Shop:Auth:Oidc:Authority", FakeProvider.Issuer),
            ("Shop:Auth:Oidc:ClientId", FakeProvider.ServerClient),
            ("Shop:Auth:Oidc:ClientSecret", FakeProvider.ServerSecret));

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Sahte veya bozuk bir dönüş isteği işlenmeyen bir hata (500) değil, 400'dür.
        using (var junk = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "nothing", ["state"] = "not-a-state" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/auth/oidc/callback", junk, CancellationToken)).StatusCode);
        }

        var challenge = await SendAsync(browser, HttpMethod.Get, "/auth/oidc/sign-in");
        var query = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        var code = provider.Authorize(query["nonce"]!, query["code_challenge"]!, NewSubject(), NewEmail());
        using var callback = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["state"] = query["state"]! });
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/auth/oidc/callback", callback, CancellationToken)).StatusCode);

        // Çıkış da oturumu değiştirir: çerezle gelen istek, başka bir sitenin ekleyemeyeceği başlığı taşımadan
        // (giriş istemeyen bu uçta da) oturumu kapatamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(browser, HttpMethod.Post, "/auth/oidc/sign-out")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(browser, HttpMethod.Get, "/auth/me")).StatusCode);

        // Token'larımızın çıkış ucu başlığı istemez (çerez orada kimlik sayılmaz) ve çerezin oturumuna dokunamaz.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(browser, HttpMethod.Post, "/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(browser, HttpMethod.Get, "/auth/me")).StatusCode);

        // Çerezi taşımayan çıkış isteği (başka bir siteden gönderilen form: çerez `SameSite=Lax`) tarayıcıya çerezi
        // sildirmez; yoksa başka bir site kullanıcıyı oturumdan düşürebilirdi.
        using var stranger = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        var signedOut = await SendAsync(stranger, HttpMethod.Post, "/auth/oidc/sign-out", ownRequest: true);
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
        Assert.False(signedOut.Headers.Contains("Set-Cookie"));

        // Sağlayıcının ön kanal çıkış adresi kapalıdır: çerezli de çerezsiz de oturuma dokunmaz, çerezi sildirmez.
        foreach (var caller in new[] { browser, stranger })
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
            {
                var remote = await SendAsync(caller, method, "/signout-oidc");
                Assert.Equal(HttpStatusCode.NotFound, remote.StatusCode);
                Assert.False(remote.Headers.Contains("Set-Cookie"));
            }
        }

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(browser, HttpMethod.Get, "/auth/me")).StatusCode);
    }

    [Fact]
    public async Task SessionCookie_DoesNotStandInTheWayOfTheOpenEndpoints()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(
            provider,
            ("Shop:Auth:Oidc:Authority", FakeProvider.Issuer),
            ("Shop:Auth:Oidc:ClientId", FakeProvider.ServerClient),
            ("Shop:Auth:Oidc:ClientSecret", FakeProvider.ServerSecret));

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var challenge = await SendAsync(browser, HttpMethod.Get, "/auth/oidc/sign-in");
        var query = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        var external = NewEmail();
        var code = provider.Authorize(query["nonce"]!, query["code_challenge"]!, NewSubject(), external);
        using var callback = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["state"] = query["state"]! });
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/auth/oidc/callback", callback, CancellationToken)).StatusCode);

        // Tarayıcıda oturum çerezi dururken parola ile kayıt ve giriş, başlığı taşımadan da çalışır: bu uçlar giriş
        // istemez ve çerez orada kimlik sayılmaz. (Başlığı her yerde isteyen ilk düzeltme parola girişini kapatıyordu.)
        var email = NewEmail();
        var registered = await ReadAsync<AccessTokenResponse>(await SendAsync(browser, HttpMethod.Post, "/auth/register", new { email, password = Password }));
        var signedIn = await ReadAsync<AccessTokenResponse>(await SendAsync(browser, HttpMethod.Post, "/auth/login", new { email, password = Password }));
        await ReadAsync<AccessTokenResponse>(await SendAsync(browser, HttpMethod.Post, "/auth/refresh", new { refreshToken = registered.RefreshToken }));

        // Taşıyıcı token çerezin önüne geçer; token'sız istek yine çerezin hesabıdır.
        Assert.Equal(email, (await ReadAsync<AccountInfo>(await SendAsync(browser, HttpMethod.Get, "/auth/me", token: signedIn.AccessToken))).Email);
        Assert.Equal(external, (await ReadAsync<AccountInfo>(await SendAsync(browser, HttpMethod.Get, "/auth/me"))).Email);

        // Çerezle gelen hesap sağlayıcınındır: adresini bizim kodumuzla doğrulayamaz (başlığı taşısa da).
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(browser, HttpMethod.Post, "/auth/email/verify", new { code = "ABCD-EFGH" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(browser, HttpMethod.Post, "/auth/email/verify", new { code = "ABCD-EFGH" }, ownRequest: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(browser, HttpMethod.Post, "/auth/email/resend", ownRequest: true)).StatusCode);
    }

    [Fact]
    public async Task ServerSignIn_ReturnsOnlyToAPathOfThisApplication()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(
            provider,
            ("Shop:Auth:Oidc:Authority", FakeProvider.Issuer),
            ("Shop:Auth:Oidc:ClientId", FakeProvider.ServerClient),
            ("Shop:Auth:Oidc:ClientSecret", FakeProvider.ServerSecret));

        foreach (var target in new[] { "https://evil.test/", "//evil.test", "/\\evil.test", "evil" })
        {
            using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
                HandleCookies = true,
            });

            var challenge = await SendAsync(browser, HttpMethod.Get, $"/auth/oidc/sign-in?returnUrl={Uri.EscapeDataString(target)}");
            var query = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
            var code = provider.Authorize(query["nonce"]!, query["code_challenge"]!, $"user-{Guid.NewGuid():N}", NewEmail());
            using var callback = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["state"] = query["state"]! });

            // Dışarıyı gösteren dönüş adresi (açık yönlendirme) köke çevrilir.
            var signedIn = await browser.PostAsync("/auth/oidc/callback", callback, CancellationToken);
            Assert.Equal("/", signedIn.Headers.Location!.OriginalString);
        }
    }

    private WebApplicationFactory<ShopMessages> CreateFactory(FakeProvider provider, params (string Key, string Value)[] settings) =>
        fixture
            .CreateAuthenticatedFactory(
            [
                ("Shop:Auth:RequestsPerMinute", "100000"),
                ("Shop:Auth:Jwt:Authority", FakeProvider.Issuer),
                ("Shop:Auth:Jwt:Audience", FakeProvider.ApiAudience),
                ("Shop:Auth:Jwt:ClientId", "shop-panel"),

                // Testlerin çoğu, adresleri gerçekten doğrulayan (kuruluşun kendi) sağlayıcısını varsayar.
                ("Shop:Auth:Jwt:TrustEmailVerified", "true"),
                ("Shop:Auth:Oidc:TrustEmailVerified", "true"),
                .. settings,
            ])
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                // Sağlayıcıya giden istekler (keşif belgesi, anahtarlar, token ucu) testin içindeki sağlayıcıya gider.
                services.Configure<JwtBearerOptions>(ExternalSignIn.JwtScheme, options => options.BackchannelHttpHandler = provider);
                services.Configure<OpenIdConnectOptions>(ExternalSignIn.OidcScheme, options => options.BackchannelHttpHandler = provider);
            }));

    /// <summary>Yönlendirmeli girişi baştan sona yapar; dönen, sağlayıcıdan dönüş isteğinin yanıtıdır.</summary>
    private static async Task<HttpResponseMessage> ServerSignInAsync(HttpClient browser, FakeProvider provider, string subject, string email)
    {
        var challenge = await SendAsync(browser, HttpMethod.Get, "/auth/oidc/sign-in");
        var query = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        var code = provider.Authorize(query["nonce"]!, query["code_challenge"]!, subject, email);
        using var callback = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["state"] = query["state"]! });
        return await browser.PostAsync("/auth/oidc/callback", callback, CancellationToken);
    }

    /// <summary>Sıfırlama e-postası yanıt beklenmeden gönderilir; adrese gidene kadar beklenir.</summary>
    private async Task<string> ResetCodeAsync(string email)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);

        while (fixture.Mail.CountFor(email, AccountCodePurpose.PasswordReset) < 1)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The reset code was not mailed.");
            await Task.Delay(25, CancellationToken);
        }

        return fixture.Mail.CodeFor(email, AccountCodePurpose.PasswordReset)!;
    }

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static string NewSubject() => $"subject-{Guid.NewGuid():N}";

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        object? body = null,
        string? token = null,
        string? idempotencyKey = null,
        bool ownRequest = false)
    {
        using var request = new HttpRequestMessage(method, url);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (ownRequest)
        {
            request.Headers.Add(ExternalSignIn.RequestHeader, "1");
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

    /// <summary>İstenince yeni hesabın kaydedilmesini bozan araya girici (veritabanı arızası).</summary>
    private sealed class FailingAccounts : SaveChangesInterceptor
    {
        public volatile bool Fail;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Fail && eventData.Context!.ChangeTracker.Entries<ShopAccount>().Any(entry => entry.State == EntityState.Added)
                ? throw new DbUpdateException("The account could not be written (simulated outage).")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>Uygulamanın günlüğe yazdıklarını toplar.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

        public IEnumerable<string> Warnings(string category) =>
            _entries.Where(entry => entry.Level == LogLevel.Warning && entry.Category.EndsWith(category, StringComparison.Ordinal)).Select(entry => entry.Message);

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class Sink(string category, ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }

    /// <summary>Testin içindeki kimlik sağlayıcı: keşif belgesi, imza anahtarı ve token ucu.</summary>
    private sealed class FakeProvider : HttpMessageHandler
    {
        public const string Issuer = "https://idp.test";
        public const string ApiAudience = "shop-api";
        public const string ServerClient = "shop-server";
        public const string ServerSecret = "server-secret-for-tests";

        private const string KeyId = "test-key-1";

        private readonly RSA _key = RSA.Create(2048);
        private readonly Dictionary<string, (string Nonce, string Challenge, string Subject, string Email)> _codes = [];

        public string AccessToken(
            string? subject,
            string? email = null,
            bool emailVerified = false,
            string? audience = null,
            string? issuer = null,
            TimeSpan? expiresIn = null,
            RSA? key = null,
            TimeSpan? issuedIn = null,
            bool iat = true,
            bool nbf = true) =>
            Sign(subject, email, emailVerified, audience ?? ApiAudience, issuer ?? Issuer, expiresIn ?? TimeSpan.FromMinutes(20), key ?? _key, nonce: null, issuedIn ?? TimeSpan.Zero, iat, nbf);

        /// <summary>Kullanıcının sağlayıcıda giriş yaptığını ve backend'e döneceği kodu aldığını varsayar.</summary>
        public string Authorize(string nonce, string challenge, string subject, string email)
        {
            var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            _codes[code] = (nonce, challenge, subject, email);
            return code;
        }

        /// <summary>İmzası atılmış, "imza yok" diyen token: başlık değişir, imza boşalır.</summary>
        public static string Unsigned(string token) =>
            $"{Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""")}.{token.Split('.')[1]}.";

        /// <summary>İçeriği imzadan sonra değiştirilmiş token.</summary>
        public static string Tampered(string token, string subject, string replacement)
        {
            var parts = token.Split('.');
            var payload = Base64UrlEncoder.Decode(parts[1]).Replace(subject, replacement, StringComparison.Ordinal);
            return $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/.well-known/openid-configuration":
                    return Json(new
                    {
                        issuer = Issuer,
                        authorization_endpoint = $"{Issuer}/authorize",
                        token_endpoint = $"{Issuer}/token",
                        jwks_uri = $"{Issuer}/jwks",
                        response_types_supported = new[] { "code" },
                        subject_types_supported = new[] { "public" },
                        id_token_signing_alg_values_supported = new[] { "RS256" },
                    });

                case "/jwks":
                    var parameters = _key.ExportParameters(includePrivateParameters: false);
                    return Json(new
                    {
                        keys = new[]
                        {
                            new
                            {
                                kty = "RSA",
                                use = "sig",
                                alg = "RS256",
                                kid = KeyId,
                                n = Base64UrlEncoder.Encode(parameters.Modulus),
                                e = Base64UrlEncoder.Encode(parameters.Exponent),
                            },
                        },
                    });

                case "/token":
                    var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));

                    // Kod bir kez kullanılır; istemcinin sırrı ve PKCE doğrulayıcısı sınanır.
                    if (form["client_id"] != ServerClient
                        || form["client_secret"] != ServerSecret
                        || form["code"] is not { } code
                        || !_codes.Remove(code, out var granted)
                        || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"] ?? ""))) != granted.Challenge)
                    {
                        return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
                    }

                    return Json(new
                    {
                        token_type = "Bearer",
                        access_token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                        expires_in = 600,
                        id_token = Sign(granted.Subject, granted.Email, emailVerified: true, ServerClient, Issuer, TimeSpan.FromMinutes(10), _key, granted.Nonce, TimeSpan.Zero, iat: true, nbf: true),
                    });

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _key.Dispose();
            }

            base.Dispose(disposing);
        }

        private static string Sign(
            string? subject, string? email, bool emailVerified, string audience, string issuer, TimeSpan expiresIn, RSA key, string? nonce, TimeSpan issuedIn, bool iat, bool nbf)
        {
            var claims = new Dictionary<string, object>();

            if (subject is not null)
            {
                claims["sub"] = subject;
            }

            if (email is not null)
            {
                claims["email"] = email;
                claims["email_verified"] = emailVerified;
            }

            if (nonce is not null)
            {
                claims["nonce"] = nonce;
            }

            // Token şimdi verilmiştir (saati ileride bir sağlayıcıda: biraz sonra); süresi dolmuş bir örnek istenirse
            // geçmişte verilmiş olur. `iat` / `nbf` kapalıysa token o alanı taşımaz.
            var now = DateTime.UtcNow;
            var expires = now + expiresIn;
            var issued = expires > now ? now + issuedIn : expires.AddMinutes(-20);

            return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Claims = claims,
                IssuedAt = iat ? issued : null,
                NotBefore = nbf ? issued : null,
                Expires = expires,
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
            });
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}
