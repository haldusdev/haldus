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
using Microsoft.Extensions.DependencyInjection;
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
    public async Task AddressOfAPasswordAccount_IsNotTakenOverByAnExternalIdentity()
    {
        using var provider = new FakeProvider();
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        var email = NewEmail();

        var session = await ReadAsync<AccessTokenResponse>(await SendAsync(client, HttpMethod.Post, "/auth/register", new { email, password = Password }));
        var local = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: session.AccessToken));

        // Sağlayıcı aynı adresi doğrulamış olsa da var olan hesaba bağlanılmaz; dış kimlik kendi ayrı hesabını alır.
        var intruder = NewSubject();
        var external = await ReadAsync<AccountInfo>(
            await SendAsync(client, HttpMethod.Get, "/auth/me", token: provider.AccessToken(intruder, email, emailVerified: true)));

        Assert.NotEqual(local.Id, external.Id);
        Assert.NotEqual(local.TenantId, external.TenantId);
        Assert.NotEqual(email, external.Email);
        Assert.False(external.EmailVerified);

        // Parola girişi ve kendi token'larımız sağlayıcı açıkken de çalışır.
        Assert.Equal(local.Id, (await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token: session.AccessToken))).Id);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/auth/refresh", new { refreshToken = session.RefreshToken })).StatusCode);

        // Dış hesabın parolası yoktur: parolayla girilemez ve parola değiştirme ucu onu kabul etmez.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Post, "/auth/login", new { email = external.Email, password = Password })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(client, HttpMethod.Post, "/auth/password/change", new { currentPassword = "", newPassword = Password }, provider.AccessToken(intruder))).StatusCode);
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

        // 4. Çıkış çerezi siler.
        Assert.True((await SendAsync(browser, HttpMethod.Post, "/auth/oidc/sign-out")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(browser, HttpMethod.Get, "/auth/me")).StatusCode);
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
                .. settings,
            ])
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                // Sağlayıcıya giden istekler (keşif belgesi, anahtarlar, token ucu) testin içindeki sağlayıcıya gider.
                services.Configure<JwtBearerOptions>(ExternalSignIn.JwtScheme, options => options.BackchannelHttpHandler = provider);
                services.Configure<OpenIdConnectOptions>(ExternalSignIn.OidcScheme, options => options.BackchannelHttpHandler = provider);
            }));

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
            RSA? key = null) =>
            Sign(subject, email, emailVerified, audience ?? ApiAudience, issuer ?? Issuer, expiresIn ?? TimeSpan.FromMinutes(10), key ?? _key, nonce: null);

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
                        id_token = Sign(granted.Subject, granted.Email, emailVerified: true, ServerClient, Issuer, TimeSpan.FromMinutes(10), _key, granted.Nonce),
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

        private static string Sign(string? subject, string? email, bool emailVerified, string audience, string issuer, TimeSpan expiresIn, RSA key, string? nonce)
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

            var expires = DateTime.UtcNow + expiresIn;

            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Claims = claims,
                IssuedAt = expires.AddMinutes(-20),
                NotBefore = expires.AddMinutes(-20),
                Expires = expires,
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
            });
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}
