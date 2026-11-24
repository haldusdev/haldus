using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

/// <summary>Bir kayıt üzerinde çalışan ama durumunu değiştirmeyen aksiyonlar.</summary>
[Collection(ShopCollection.Name)]
public sealed class EntityActionEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActionWithALambda_ChangesTheRecordAndReturnsIt()
    {
        var coupon = await ReadAsync<CouponResponse>(
            await SendAsync(HttpMethod.Post, "/coupons", new { code = $"ONCE-{Guid.NewGuid():N}", percent = 10, maxRedemptions = 1 }));

        var redeemed = await ReadAsync<CouponResponse>(await SendAsync(HttpMethod.Post, $"/coupons/{coupon.Id}/redeem"));
        var persisted = await ReadAsync<CouponResponse>(await SendAsync(HttpMethod.Get, $"/coupons/{coupon.Id}"));

        Assert.Equal(0, coupon.TimesRedeemed);
        Assert.Equal(1, redeemed.TimesRedeemed);
        Assert.Equal(1, persisted.TimesRedeemed);
    }

    [Fact]
    public async Task ActionThatRejects_SavesNothingAndExplainsWhyInTheRequestsLanguage()
    {
        var coupon = await ReadAsync<CouponResponse>(
            await SendAsync(HttpMethod.Post, "/coupons", new { code = $"ONCE-{Guid.NewGuid():N}", percent = 10, maxRedemptions = 1 }));
        await SendAsync(HttpMethod.Post, $"/coupons/{coupon.Id}/redeem");

        var english = await SendAsync(HttpMethod.Post, $"/coupons/{coupon.Id}/redeem");
        var turkish = await SendAsync(HttpMethod.Post, $"/coupons/{coupon.Id}/redeem", language: "tr");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, english.StatusCode);
        Assert.Equal(
            "This coupon has no redemptions left.",
            (await english.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Detail);
        Assert.Equal(
            "Bu kuponun kullanım hakkı doldu.",
            (await turkish.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Detail);

        var unchanged = await ReadAsync<CouponResponse>(await SendAsync(HttpMethod.Get, $"/coupons/{coupon.Id}"));
        Assert.Equal(1, unchanged.TimesRedeemed);
    }

    [Fact]
    public async Task ActionWhoseRequestAppliesItself_IsValidatedAndChecksTheVersion()
    {
        var created = await SendAsync(HttpMethod.Post, "/customers", new { name = "Ada", loyaltyPoints = 10 });
        var customer = await ReadAsync<CustomerResponse>(created);
        var tag = created.Headers.ETag!.Tag;
        var url = $"/customers/{customer.Id}/adjust-loyalty";

        // Sürüm kontrollü kaynakta aksiyon da, kaydı değiştiren her uç gibi, If-Match ister.
        var missing = await SendAsync(HttpMethod.Post, url, new { delta = 5 });
        var invalid = await SendAsync(HttpMethod.Post, url, new { delta = 5000 }, ifMatch: tag);
        var adjusted = await SendAsync(HttpMethod.Post, url, new { delta = 5 }, ifMatch: tag);
        var stale = await SendAsync(HttpMethod.Post, url, new { delta = 5 }, ifMatch: tag);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(15, (await ReadAsync<CustomerResponse>(adjusted)).LoyaltyPoints);
        Assert.NotEqual(tag, adjusted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task Action_IsNotReachableByAnotherTenantOrForAMissingRecord()
    {
        var created = await SendAsync(HttpMethod.Post, "/customers", new { name = "Ada" });
        var customer = await ReadAsync<CustomerResponse>(created);
        var tag = created.Headers.ETag!.Tag;

        var intruder = await SendAsync(
            HttpMethod.Post, $"/customers/{customer.Id}/adjust-loyalty", new { delta = 1 }, ifMatch: tag, tenant: Guid.NewGuid());
        var missing = await SendAsync(HttpMethod.Post, $"/coupons/{Guid.NewGuid()}/redeem");

        Assert.Equal(HttpStatusCode.NotFound, intruder.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ActionWithItsOwnResult_ReturnsItAndStillSavesTheRecord()
    {
        var product = await ReadAsync<ProductResponse>(
            await SendAsync(HttpMethod.Post, "/products", new { name = $"Repriced {Guid.NewGuid():N}", price = 200 }));

        var response = await SendAsync(HttpMethod.Post, $"/products/{product.Id}/reprice", new { percent = 10 });
        var invalid = await SendAsync(HttpMethod.Post, $"/products/{product.Id}/reprice", new { percent = 500 });
        var missing = await SendAsync(HttpMethod.Post, $"/products/{Guid.NewGuid()}/reprice", new { percent = 10 });

        // Yanıt ürünün kendisi değil, aksiyonun sonucudur; handler sınıfı modülün içinden bulunur.
        var result = await ReadAsync<RepriceResult>(response);
        Assert.Equal(new RepriceResult(200m, 220m), result);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var persisted = await ReadAsync<ProductResponse>(await SendAsync(HttpMethod.Get, $"/products/{product.Id}"));
        Assert.Equal(220m, persisted.Price);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string? ifMatch = null, Guid? tenant = null, string? language = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, (tenant ?? _tenant).ToString());

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
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
