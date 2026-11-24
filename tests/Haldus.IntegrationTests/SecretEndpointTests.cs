using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

/// <summary>Sırlar: yalnızca bir kez gösterilen sır, döndürme ve token ile oturumsuz erişim.</summary>
[Collection(ShopCollection.Name)]
public sealed partial class SecretEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [GeneratedRegex("^sk_[A-Za-z0-9]{43}$")]
    private static partial Regex ApiSecret();

    [GeneratedRegex("^inv_[A-Za-z0-9]{43}$")]
    private static partial Regex InvitationToken();

    // ── Yalnızca bir kez gösterilen sır ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecretOnce_IsShownWhenTheRecordIsCreatedAndNeverAgain()
    {
        var created = await SendAsync(HttpMethod.Post, "/api-keys", new { name = "CI" });
        var key = await ReadAsync<ApiKeyResponse>(created);

        Assert.Matches(ApiSecret(), key.Secret!);
        Assert.Equal(key.Secret![^4..], key.SecretHint);

        // Sonraki hiçbir yanıt sırrı (ya da özetini) taşımaz; yalnızca ipucu görünür.
        foreach (var url in new[] { $"/api-keys/{key.Id}", "/api-keys" })
        {
            var body = await (await SendAsync(HttpMethod.Get, url)).Content.ReadAsStringAsync(CancellationToken);

            Assert.DoesNotContain("\"secret\"", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hash", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(key.Secret!, body, StringComparison.Ordinal);
            Assert.Contains(key.SecretHint, body, StringComparison.Ordinal);
        }

        // Veritabanında sırrın kendisi değil özeti durur.
        await fixture.WithDbContextAsync(async context =>
        {
            var stored = await context.ApiKeys.IgnoreQueryFilters().SingleAsync(item => item.Id == key.Id, CancellationToken);

            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Secret!))), stored.SecretHash);
            Assert.NotEqual(key.Secret, stored.SecretHash);
        });
    }

    [Fact]
    public async Task SecretOnce_CannotBeChosenByTheClient()
    {
        var chosen = "sk_" + new string('A', 43);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(chosen)));

        var key = await ReadAsync<ApiKeyResponse>(await SendAsync(
            HttpMethod.Post, "/api-keys", new { name = "Sneaky", secret = chosen, secretHash = hash, secretHint = "AAAA" }));

        // İstek gövdesindeki sır alanları yok sayılır; sır her zaman sunucuda üretilir.
        Assert.NotEqual(chosen, key.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmIAsync(chosen)).StatusCode);
    }

    [Fact]
    public async Task SecretOnce_APresentedSecretIdentifiesItsRecord()
    {
        var key = await ReadAsync<ApiKeyResponse>(await SendAsync(HttpMethod.Post, "/api-keys", new { name = "Deploy" }));

        var known = await WhoAmIAsync(key.Secret);
        var wrong = await WhoAmIAsync("sk_" + new string('x', 43));
        var missing = await WhoAmIAsync(null);

        // Arama kiracıdan bağımsızdır; kiracıyı bulunan kayıt söyler.
        var identity = await ReadAsync<WhoAmI>(known);
        Assert.Equal((key.Id, "Deploy", _tenant), (identity.Id, identity.Name, identity.TenantId));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
    }

    // ── Döndürme ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rotatable_IssuesANewSecretAndKeepsTheOldOneForTheOverlap()
    {
        var key = await ReadAsync<ApiKeyResponse>(await SendAsync(HttpMethod.Post, "/api-keys", new { name = "Rolling" }));

        var rotated = await ReadAsync<ApiKeyResponse>(await SendAsync(HttpMethod.Post, $"/api-keys/{key.Id}/rotate"));

        Assert.Matches(ApiSecret(), rotated.Secret!);
        Assert.NotEqual(key.Secret, rotated.Secret);
        Assert.Equal(rotated.Secret![^4..], rotated.SecretHint);

        // Örtüşme süresince iki sır da geçerlidir; süre dolunca eskisi reddedilir.
        Assert.Equal(HttpStatusCode.OK, (await WhoAmIAsync(rotated.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WhoAmIAsync(key.Secret)).StatusCode);

        await fixture.WithDbContextAsync(context => context.ApiKeys
            .IgnoreQueryFilters()
            .Where(item => item.Id == key.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PreviousSecretExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmIAsync(key.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WhoAmIAsync(rotated.Secret)).StatusCode);
    }

    [Fact]
    public async Task Rotatable_IsAWriteOfTheOwningTenant()
    {
        var key = await ReadAsync<ApiKeyResponse>(await SendAsync(HttpMethod.Post, "/api-keys", new { name = "Mine" }));

        var foreign = await SendAsync(HttpMethod.Post, $"/api-keys/{key.Id}/rotate", tenant: Guid.NewGuid());
        var missing = await SendAsync(HttpMethod.Post, $"/api-keys/{Guid.NewGuid()}/rotate");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WhoAmIAsync(key.Secret)).StatusCode);
    }

    [Fact]
    public async Task RecoverableSecret_CanBeUsedByTheServerButIsNeverReturned()
    {
        var endpoint = await ReadAsync<WebhookEndpointResponse>(
            await SendAsync(HttpMethod.Post, "/webhook-endpoints", new { url = "https://example.test/hooks" }));

        Assert.StartsWith("whsec_", endpoint.Secret);

        // Sunucu sırrı açıp gönderiyi imzalar; istemci aynı imzayı elindeki sırla hesaplayarak doğrular.
        var signed = await ReadAsync<PayloadSignature>(
            await SendAsync(HttpMethod.Post, $"/webhook-endpoints/{endpoint.Id}/sign-payload", new { payload = "order.placed" }));
        Assert.Equal(Sign(endpoint.Secret!, "order.placed"), signed.Signature);

        // Veritabanındaki şifreli değer sırrın kendisi değildir.
        await fixture.WithDbContextAsync(async context =>
        {
            var stored = await context.WebhookEndpoints.IgnoreQueryFilters().SingleAsync(item => item.Id == endpoint.Id, CancellationToken);

            Assert.NotEmpty(stored.SecretCipher);
            Assert.DoesNotContain(endpoint.Secret!, stored.SecretCipher, StringComparison.Ordinal);
        });

        // Örtüşme bildirilmedi: döndürünce eski sır hemen ölür ve imzalar yeni sırla atılır.
        var rotated = await ReadAsync<WebhookEndpointResponse>(await SendAsync(HttpMethod.Post, $"/webhook-endpoints/{endpoint.Id}/rotate"));
        var resigned = await ReadAsync<PayloadSignature>(
            await SendAsync(HttpMethod.Post, $"/webhook-endpoints/{endpoint.Id}/sign-payload", new { payload = "order.placed" }));

        Assert.NotEqual(endpoint.Secret, rotated.Secret);
        Assert.Equal(Sign(rotated.Secret!, "order.placed"), resigned.Signature);
        Assert.NotEqual(signed.Signature, resigned.Signature);
    }

    // ── Token ile erişim ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TokenAccess_LetsSomeoneWithoutASessionReadTheRecord()
    {
        var created = await ReadAsync<InvitationResponse>(await SendAsync(HttpMethod.Post, "/invitations", new { email = "ada@example.test" }));
        Assert.Matches(InvitationToken(), created.Token!);

        // Token başlıkta taşınır; isteğin kiracısı ve kullanıcısı yoktur.
        var response = await SendAsync(HttpMethod.Get, "/invitations/by-token", tenant: null, token: created.Token);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        var invitation = JsonSerializer.Deserialize<InvitationResponse>(body, JsonSerializerOptions.Web)!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((created.Id, "ada@example.test", InvitationStatus.Pending), (invitation.Id, invitation.Email, invitation.Status));

        // Token yalnızca oluşturma yanıtında görünür.
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);

        // Başka bir kiracının başlığı sonucu değiştirmez: kaydı token tanıtır.
        var elsewhere = await SendAsync(HttpMethod.Get, "/invitations/by-token", tenant: Guid.NewGuid(), token: created.Token);
        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
    }

    [Fact]
    public async Task TokenAccess_AnUnknownOrMissingTokenFindsNothing()
    {
        await ReadAsync<InvitationResponse>(await SendAsync(HttpMethod.Post, "/invitations", new { email = "ada@example.test" }));

        var wrong = await SendAsync(HttpMethod.Get, "/invitations/by-token", tenant: null, token: "inv_" + new string('x', 43));
        var missing = await SendAsync(HttpMethod.Get, "/invitations/by-token", tenant: null);
        var acceptWrong = await SendAsync(HttpMethod.Post, "/invitations/by-token/accept", tenant: null, token: "inv_" + new string('x', 43));

        // İkisi aynı yanıtı alır; yanıt bir token'ın var olup olmadığını ele vermez.
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, acceptWrong.StatusCode);

        // Token'sız uçlar eskisi gibi kiracı ister.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/invitations", tenant: null)).StatusCode);
    }

    [Fact]
    public async Task TokenAccess_RunsOnlyTheListedTransitionsAndUnderTheirRules()
    {
        var created = await ReadAsync<InvitationResponse>(await SendAsync(HttpMethod.Post, "/invitations", new { email = "ada@example.test" }));

        var accepted = await SendAsync(HttpMethod.Post, "/invitations/by-token/accept", tenant: null, token: created.Token);
        var again = await SendAsync(HttpMethod.Post, "/invitations/by-token/accept", tenant: null, token: created.Token);
        var revoke = await SendAsync(HttpMethod.Post, "/invitations/by-token/revoke", tenant: null, token: created.Token);

        Assert.Equal(InvitationStatus.Accepted, (await ReadAsync<InvitationResponse>(accepted)).Status);

        // Geçişin kendi kuralı token ile gelene de uygulanır: kabul edilmiş davet yeniden kabul edilemez.
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // Sayılmayan bir geçişin token adresi yoktur.
        Assert.Contains(revoke.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });

        // Değişiklik, kaydın kendi kiracısında görünür.
        var seen = await ReadAsync<InvitationResponse>(await SendAsync(HttpMethod.Get, $"/invitations/{created.Id}"));
        Assert.Equal(InvitationStatus.Accepted, seen.Status);
    }

    [Fact]
    public async Task TokenAccess_AnExpiredRecordCannotBeReachedByItsToken()
    {
        var created = await ReadAsync<InvitationResponse>(await SendAsync(HttpMethod.Post, "/invitations", new { email = "late@example.test" }));

        await fixture.WithDbContextAsync(context => context.Invitations
            .IgnoreQueryFilters()
            .Where(item => item.Id == created.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken));

        var read = await SendAsync(HttpMethod.Get, "/invitations/by-token", tenant: null, token: created.Token);
        var accept = await SendAsync(HttpMethod.Post, "/invitations/by-token/accept", tenant: null, token: created.Token);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
    }

    private static string Sign(string secret, string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    private async Task<HttpResponseMessage> WhoAmIAsync(string? secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");

        if (secret is not null)
        {
            request.Headers.Add(IntegrationEndpoints.ApiKeyHeader, secret);
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null) =>
        SendAsync(method, url, _tenant, token: null, body);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid? tenant, string? token = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);

        if (tenant is { } tenantId)
        {
            request.Headers.Add(DemoIdentity.TenantHeader, tenantId.ToString());
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
