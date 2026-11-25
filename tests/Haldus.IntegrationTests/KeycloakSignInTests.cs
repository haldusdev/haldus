using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Haldus.IntegrationTests;

/// <summary>
/// Kimlik sağlayıcıyla giriş, gerçek bir sağlayıcıya (kapsayıcıda Keycloak) karşı: keşif belgesi, imza anahtarları,
/// token ucu ve giriş sayfası sağlayıcının kendisinindir. <see cref="ExternalSignInTests"/> aynı kuralları testin
/// içindeki bir sağlayıcıyla, ayrıntısıyla sınar; buradaki testler gerçek bir sağlayıcıyla birlikte çalışmayı gösterir.
/// </summary>
[Collection(ShopCollection.Name)]
public sealed partial class KeycloakSignInTests(ShopFixture fixture, KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AccessTokenOfTheProvider_IsAcceptedAndBecomesALocalAccount()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var tokens = await keycloak.PasswordGrantAsync(KeycloakFixture.AppClient);
        var account = await ReadAsync<AccountInfo>(await SendAsync(client, "/auth/me", tokens.AccessToken));

        // Sağlayıcının doğruladığı adres hesaba yazılır.
        Assert.Equal((KeycloakFixture.Email, true), (account.Email, account.EmailVerified));

        // Sağlayıcının yenilediği token aynı hesabı tanıtır.
        var renewed = await keycloak.RefreshAsync(KeycloakFixture.AppClient, tokens.RefreshToken);
        Assert.NotEqual(tokens.AccessToken, renewed.AccessToken);
        Assert.Equal(account.Id, (await ReadAsync<AccountInfo>(await SendAsync(client, "/auth/me", renewed.AccessToken))).Id);

        // Aynı sağlayıcının başka bir API için verdiği token (audience bu API değil) kabul edilmez.
        var foreign = await keycloak.PasswordGrantAsync(KeycloakFixture.OtherClient);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, "/auth/me", foreign.AccessToken)).StatusCode);

        // İmzası bozulan token kabul edilmez.
        var broken = tokens.AccessToken[..^3] + (tokens.AccessToken.EndsWith("AAA", StringComparison.Ordinal) ? "BBB" : "AAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, "/auth/me", broken)).StatusCode);
    }

    [Fact]
    public async Task ServerSignIn_GoesThroughTheLoginPageOfTheProvider()
    {
        await using var factory = CreateFactory(
            ("Shop:Auth:Oidc:Authority", keycloak.Issuer),
            ("Shop:Auth:Oidc:ClientId", KeycloakFixture.ServerClient),
            ("Shop:Auth:Oidc:ClientSecret", keycloak.ServerSecret),
            ("Shop:Auth:Oidc:RequireHttpsMetadata", "false"),
            ("Shop:Auth:Oidc:TrustEmailVerified", "true"));

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // 1. Backend tarayıcıyı sağlayıcının giriş sayfasına yönlendirir.
        var challenge = await browser.GetAsync("/auth/oidc/sign-in?returnUrl=/auth/me", CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.StartsWith(keycloak.Issuer, challenge.Headers.Location!.AbsoluteUri, StringComparison.Ordinal);

        // 2. Kullanıcı sağlayıcının sayfasında giriş yapar; sağlayıcı kodu backend'e gönderecek formu döndürür.
        var returned = await keycloak.SignInAsync(challenge.Headers.Location);
        Assert.Equal("https://localhost/auth/oidc/callback", returned.Action);

        // 3. Backend kodu sağlayıcının token ucunda değiştirir ve oturum çerezini yazar.
        using var form = new FormUrlEncodedContent(returned.Fields);
        var signedIn = await browser.PostAsync("/auth/oidc/callback", form, CancellationToken);
        Assert.True(signedIn.StatusCode == HttpStatusCode.Redirect, $"{(int)signedIn.StatusCode}: {await signedIn.Content.ReadAsStringAsync(CancellationToken)}");
        Assert.Equal("/auth/me", signedIn.Headers.Location!.OriginalString);

        var account = await ReadAsync<AccountInfo>(await browser.GetAsync("/auth/me", CancellationToken));
        Assert.Equal((KeycloakFixture.Email, true), (account.Email, account.EmailVerified));

        // Aynı kişi, hangi yoldan gelirse gelsin (çerez veya token), aynı hesaptır.
        using var api = factory.CreateClient();
        var tokens = await keycloak.PasswordGrantAsync(KeycloakFixture.AppClient);
        Assert.Equal(account.Id, (await ReadAsync<AccountInfo>(await SendAsync(api, "/auth/me", tokens.AccessToken))).Id);
    }

    private WebApplicationFactory<ShopMessages> CreateFactory(params (string Key, string Value)[] settings) =>
        fixture.CreateAuthenticatedFactory(
        [
            ("Shop:Auth:RequestsPerMinute", "100000"),
            ("Shop:Auth:Jwt:Authority", keycloak.Issuer),
            ("Shop:Auth:Jwt:Audience", KeycloakFixture.ApiAudience),
            ("Shop:Auth:Jwt:ClientId", KeycloakFixture.AppClient),

            // Kapsayıcıdaki sağlayıcı düz HTTP ile çalışır; yalnızca geliştirme ortamında kabul edilir.
            ("Shop:Auth:Jwt:RequireHttpsMetadata", "false"),

            // Bu sağlayıcı testin kendi kurduğu sağlayıcıdır; adresi doğruladığına güvenilir.
            ("Shop:Auth:Jwt:TrustEmailVerified", "true"),
            .. settings,
        ]);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

/// <summary>
/// Gerçek bir Keycloak. Alan (realm), kullanıcı ve istemciler açılışta içe aktarılır: gizli anahtarı olmayan bir
/// uygulama istemcisi (token'ına bu API'nin audience'ı eklenir), başka bir API'nin istemcisi ve backend'in kendi
/// istemcisi. Parolalar ve istemci sırrı her çalıştırmada rastgele üretilir.
/// </summary>
public sealed partial class KeycloakFixture : IAsyncLifetime
{
    public const string ApiAudience = "shop-api";
    public const string AppClient = "shop-app";
    public const string OtherClient = "other-app";
    public const string ServerClient = "shop-server";
    public const string Email = "ada.keycloak@example.com";

    private const string Realm = "shop";
    private const string UserName = "ada";

    // quay.io/keycloak/keycloak:26.4
    private static readonly string Image = Environment.GetEnvironmentVariable("HALDUS_TEST_KEYCLOAK_IMAGE")
        ?? "quay.io/keycloak/keycloak@sha256:9409c59bdfb65dbffa20b11e6f18b8abb9281d480c7ca402f51ed3d5977e6007";

    private readonly IContainer _container;
    private readonly HttpClient _http = new();
    private readonly string _password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    public KeycloakFixture()
    {
        var realm = JsonSerializer.Serialize(new
        {
            realm = Realm,
            enabled = true,
            users = new[]
            {
                new
                {
                    username = UserName,
                    enabled = true,
                    email = Email,
                    emailVerified = true,
                    firstName = "Ada",
                    lastName = "Lovelace",
                    credentials = new[] { new { type = "password", value = _password, temporary = false } },
                },
            },
            clients = new object[]
            {
                new
                {
                    clientId = AppClient,
                    publicClient = true,
                    standardFlowEnabled = true,
                    directAccessGrantsEnabled = true,
                    redirectUris = new[] { "*" },
                    webOrigins = new[] { "*" },

                    // Erişim token'ı kimin için verildiğini söylemelidir; API yalnızca kendisi için verileni kabul eder.
                    protocolMappers = new[]
                    {
                        new
                        {
                            name = "shop-api-audience",
                            protocol = "openid-connect",
                            protocolMapper = "oidc-audience-mapper",
                            config = new Dictionary<string, string>
                            {
                                ["included.custom.audience"] = ApiAudience,
                                ["access.token.claim"] = "true",
                                ["id.token.claim"] = "false",
                            },
                        },
                    },
                },
                new { clientId = OtherClient, publicClient = true, directAccessGrantsEnabled = true },
                new
                {
                    clientId = ServerClient,
                    publicClient = false,
                    secret = ServerSecret,
                    standardFlowEnabled = true,
                    redirectUris = new[] { "https://localhost/auth/oidc/callback" },
                },
            },
        });

        _container = new ContainerBuilder(Image)
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)))
            .WithResourceMapping(Encoding.UTF8.GetBytes(realm), FilePath.Of($"/opt/keycloak/data/import/{Realm}-realm.json"))
            .WithCommand("start-dev", "--import-realm")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(8080)
                .ForPath($"/realms/{Realm}/.well-known/openid-configuration")))
            .Build();
    }

    public string ServerSecret { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    /// <summary>Sağlayıcının adresi (issuer); token'lardaki <c>iss</c> ile aynıdır.</summary>
    public string Issuer => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}/realms/{Realm}";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>Kullanıcının parolasıyla token alır (yalnızca test için; istemciler yönlendirmeli akışı kullanır).</summary>
    public Task<ProviderTokens> PasswordGrantAsync(string client) => TokenAsync(new()
    {
        ["grant_type"] = "password",
        ["client_id"] = client,
        ["username"] = UserName,
        ["password"] = _password,
        ["scope"] = "openid profile email",
    });

    public Task<ProviderTokens> RefreshAsync(string client, string refreshToken) => TokenAsync(new()
    {
        ["grant_type"] = "refresh_token",
        ["client_id"] = client,
        ["refresh_token"] = refreshToken,
    });

    /// <summary>
    /// Bir tarayıcının yapacağını yapar: giriş sayfasını açar, kullanıcı adı ve parolayı gönderir ve sağlayıcının
    /// kodu uygulamaya göndermek için döndürdüğü formu (<c>response_mode=form_post</c>) okur.
    /// </summary>
    public async Task<(string Action, Dictionary<string, string> Fields)> SignInAsync(Uri authorize)
    {
        // Sağlayıcı kapsayıcıda düz HTTP ile çalışır ama çerezlerini "Secure" işaretler; .NET'in çerez kabı onları
        // HTTP'de geri göndermez. Çerezleri ve yönlendirmeleri bu yüzden test kendisi taşır.
        using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        using var browser = new HttpClient(handler);
        var cookies = new Dictionary<string, string>();

        async Task<HttpResponseMessage> NavigateAsync(HttpMethod method, Uri address, Dictionary<string, string>? form = null)
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(method, address);

                if (cookies.Count > 0)
                {
                    request.Headers.Add("Cookie", string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
                }

                if (form is not null)
                {
                    request.Content = new FormUrlEncodedContent(form);
                }

                var response = await browser.SendAsync(request, CancellationToken);

                if (response.Headers.TryGetValues("Set-Cookie", out var set))
                {
                    foreach (var pair in set.Select(header => header.Split(';')[0].Split('=', 2)))
                    {
                        cookies[pair[0].Trim()] = pair.Length > 1 ? pair[1] : "";
                    }
                }

                if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } next || hop == 10)
                {
                    return response;
                }

                // Yönlendirme GET ile izlenir.
                address = new Uri(address, next);
                method = HttpMethod.Get;
                form = null;
                response.Dispose();
            }
        }

        using var opened = await NavigateAsync(HttpMethod.Get, authorize);
        var page = await opened.Content.ReadAsStringAsync(CancellationToken);
        var login = LoginForm().Match(page);
        Assert.True(login.Success, $"The provider did not show a login form ({(int)opened.StatusCode}) for {authorize}:\n{Text(page)}");

        using var response = await NavigateAsync(
            HttpMethod.Post,
            new Uri(WebUtility.HtmlDecode(login.Groups["action"].Value)),
            new Dictionary<string, string> { ["username"] = UserName, ["password"] = _password, ["credentialId"] = "" });
        var body = await response.Content.ReadAsStringAsync(CancellationToken);

        var post = PostForm().Match(body);
        Assert.True(post.Success, $"The provider did not return the form that carries the code ({(int)response.StatusCode}):\n{Text(body)}");

        var fields = HiddenInput().Matches(body).ToDictionary(
            input => WebUtility.HtmlDecode(input.Groups["name"].Value),
            input => WebUtility.HtmlDecode(input.Groups["value"].Value));

        return (WebUtility.HtmlDecode(post.Groups["action"].Value), fields);
    }

    // Hata sayfasının okunur kısmı: etiketler atılır.
    private static string Text(string html)
    {
        var text = Tags().Replace(html, " ");
        text = Spaces().Replace(text, " ").Trim();
        return text[..Math.Min(text.Length, 500)];
    }

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _container.DisposeAsync();
    }

    private async Task<ProviderTokens> TokenAsync(Dictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync($"{Issuer}/protocol/openid-connect/token", content, CancellationToken);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        return new ProviderTokens(
            document.RootElement.GetProperty("access_token").GetString()!,
            document.RootElement.GetProperty("refresh_token").GetString()!);
    }

    [GeneratedRegex("""<form[^>]*id="kc-form-login"[^>]*action="(?<action>[^"]+)"[^>]*>|<form[^>]*action="(?<action>[^"]+)"[^>]*id="kc-form-login"[^>]*>""")]
    private static partial Regex LoginForm();

    [GeneratedRegex("""<form[^>]*method="post"[^>]*action="(?<action>[^"]+)"[^>]*>|<form[^>]*action="(?<action>[^"]+)"[^>]*method="post"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex PostForm();

    [GeneratedRegex("""<input[^>]*type="hidden"[^>]*name="(?<name>[^"]+)"[^>]*value="(?<value>[^"]*)"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInput();

    [GeneratedRegex("""<(script|style)[\s\S]*?</\1>|<[^>]+>""", RegexOptions.IgnoreCase)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public sealed record ProviderTokens(string AccessToken, string RefreshToken);
}
