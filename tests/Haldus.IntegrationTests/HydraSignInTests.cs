using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Haldus.IntegrationTests;

/// <summary>
/// Sağlayıcının JWT erişim token'larının kabulü, ikinci bir gerçek sağlayıcıya (kapsayıcıda Ory Hydra) karşı.
/// <see cref="KeycloakSignInTests"/> bir kullanıcının girişini sınar; burada bir makine istemcisi (client credentials)
/// token alır: kullanıcı, adres ve yenileme token'ı yoktur. Amaç, kabulün tek bir ürüne göre yazılmadığını
/// göstermektir: keşif belgesi, imza anahtarları ve token'ın alanları (<c>iss</c>, <c>aud</c>, <c>iat</c>) başka bir
/// uygulamanın ürettiği biçimdedir.
/// </summary>
[Collection(ShopCollection.Name)]
public sealed class HydraSignInTests(ShopFixture fixture, HydraFixture hydra) : IClassFixture<HydraFixture>
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AccessTokenOfAMachineClient_IsAcceptedAndBecomesALocalAccount()
    {
        await using var factory = fixture.CreateAuthenticatedFactory(
        [
            ("Shop:Auth:RequestsPerMinute", "100000"),
            ("Shop:Auth:Jwt:Authority", hydra.Issuer),
            ("Shop:Auth:Jwt:Audience", HydraFixture.ApiAudience),

            // Kapsayıcıdaki sağlayıcı düz HTTP ile çalışır; yalnızca geliştirme ortamında kabul edilir.
            ("Shop:Auth:Jwt:RequireHttpsMetadata", "false"),
        ]);
        using var client = factory.CreateClient();

        var token = await hydra.ClientCredentialsAsync(HydraFixture.ApiAudience);

        // İstemci (token'ın `sub` alanı) yerel bir hesaba ve kendi kiracısına çevrilir. Adresi yoktur: doğrulanmış
        // sayılmaz ve personel olamaz.
        var account = await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", token));
        Assert.Equal((false, false), (account.EmailVerified, account.IsStaff));
        Assert.EndsWith("@external.invalid", account.Email, StringComparison.Ordinal);

        // Aynı istemcinin yeni token'ı aynı hesaptır.
        await Task.Delay(TimeSpan.FromSeconds(1.2), CancellationToken);
        var again = await hydra.ClientCredentialsAsync(HydraFixture.ApiAudience);
        Assert.NotEqual(token, again);
        Assert.Equal(account.Id, (await ReadAsync<AccountInfo>(await SendAsync(client, HttpMethod.Get, "/auth/me", again))).Id);

        // Haldus kiracıyı bu kimlikten okur: kiracıya ait kaynak yazılır ve okunur; ortak kaynak yazılamaz.
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client, HttpMethod.Post, "/orders", token, new { customerName = "Machine" })).StatusCode);
        var orders = await ReadAsync<JsonElement>(await SendAsync(client, HttpMethod.Get, "/orders", token));
        Assert.Equal(1, orders.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Post, "/products", token, new { name = $"Kettle {Guid.NewGuid():N}", price = 10 })).StatusCode);

        // Aynı sağlayıcının başka bir API için verdiği ya da hiçbir API'yi adlandırmayan token kabul edilmez.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/auth/me", await hydra.ClientCredentialsAsync(HydraFixture.OtherAudience))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/auth/me", await hydra.ClientCredentialsAsync(audience: null))).StatusCode);

        // İmzası bozulan token kabul edilmez.
        var broken = token[..^3] + (token.EndsWith("AAA", StringComparison.Ordinal) ? "BBB" : "AAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/auth/me", broken)).StatusCode);

        // "Her yerden çık" o ana kadar verilmiş token'ları geçersiz kılar (token'ın `iat` alanına dayanır); sağlayıcının
        // sonradan verdiği token geçer.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Post, "/auth/logout-all", again)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/auth/me", again)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/auth/me", token)).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(1.2), CancellationToken);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/auth/me", await hydra.ClientCredentialsAsync(HydraFixture.ApiAudience))).StatusCode);

        // Panel ve mobil için bir istemci ayarlanmadı: giriş ekranlarına sağlayıcı sunulmaz.
        Assert.Null((await ReadAsync<AuthConfig>(await client.GetAsync("/auth/config", CancellationToken))).Provider);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
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

/// <summary>
/// Gerçek bir Ory Hydra: bellekteki veritabanıyla, erişim token'larını JWT olarak verecek biçimde çalışır. İstemci
/// açılışta yönetim API'sinden oluşturulur; sırrı her çalıştırmada rastgele üretilir.
/// </summary>
public sealed class HydraFixture : IAsyncLifetime
{
    public const string ApiAudience = "shop-api";
    public const string OtherAudience = "another-api";

    private const string Client = "shop-machine";

    // oryd/hydra:v2.3.0
    private static readonly string Image = Environment.GetEnvironmentVariable("HALDUS_TEST_HYDRA_IMAGE")
        ?? "oryd/hydra@sha256:b94007e19a1f7f78157e7f4ea340da8a55b5f104a0f1198755c256f38ef32b4b";

    private readonly IContainer _container;
    private readonly HttpClient _http = new();
    private readonly string _secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    // Sağlayıcı kendi adını (issuer) açılışta bilmelidir; bağlantı noktası bu yüzden kapsayıcıdan önce seçilir.
    private readonly int _port = FreePort();

    public HydraFixture() =>
        _container = new ContainerBuilder(Image)
            .WithPortBinding(_port, 4444)
            .WithPortBinding(4445, assignRandomHostPort: true)
            .WithEnvironment("DSN", "memory")
            .WithEnvironment("SECRETS_SYSTEM", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)))
            .WithEnvironment("URLS_SELF_ISSUER", Issuer)
            .WithEnvironment("STRATEGIES_ACCESS_TOKEN", "jwt")
            .WithCommand("serve", "all", "--dev")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(4444)
                .ForPath("/.well-known/openid-configuration")))
            .Build();

    /// <summary>Sağlayıcının adresi (issuer); token'lardaki <c>iss</c> ile aynıdır.</summary>
    public string Issuer => $"http://127.0.0.1:{_port}";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>İstemcinin kendi adına aldığı erişim token'ı; <paramref name="audience"/> verilmezse token hiçbir API'yi adlandırmaz.</summary>
    public async Task<string> ClientCredentialsAsync(string? audience)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = Client,
            ["client_secret"] = _secret,
        };

        if (audience is not null)
        {
            form["audience"] = audience;
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync($"{Issuer}/oauth2/token", content, CancellationToken);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        using var response = await _http.PostAsJsonAsync(
            $"http://{_container.Hostname}:{_container.GetMappedPublicPort(4445)}/admin/clients",
            new
            {
                client_id = Client,
                client_secret = _secret,
                grant_types = new[] { "client_credentials" },
                token_endpoint_auth_method = "client_secret_post",
                audience = new[] { ApiAudience, OtherAudience },
                access_token_strategy = "jwt",
            });

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _container.DisposeAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
