using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Platform;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed class OperationEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Query_BindsCamelCaseParametersAndRunsTheHandlerClass()
    {
        var code = $"Q{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await _client.PutAsJsonAsync($"/countries/{code}", new PutCountryRequest { Name = "Known" }, CancellationToken);

        var known = await _client.GetFromJsonAsync<ShippingQuote>(
            $"/shipping-quote?country={code}&weight=2&express=true", CancellationToken);
        var unknown = await _client.GetFromJsonAsync<ShippingQuote>(
            "/shipping-quote?country=NOWHERE&weight=2", CancellationToken);

        // Bilinen ülke: (10 + 2 * 2) * 2 = 28; bilinmeyen: 10 + 2 * 5 = 20.
        Assert.Equal(new ShippingQuote(28m, CountryKnown: true), known);
        Assert.Equal(new ShippingQuote(20m, CountryKnown: false), unknown);
    }

    [Fact]
    public async Task Query_OmittedParameterKeepsTheDefaultOfTheRequestClass()
    {
        var quote = await _client.GetFromJsonAsync<ShippingQuote>("/shipping-quote?country=NOWHERE", CancellationToken);

        // Weight gönderilmedi; sınıftaki varsayılanı 1: 10 + 1 * 5 = 15.
        Assert.Equal(15m, quote?.Price);
    }

    [Fact]
    public async Task Query_ValidatesTheRequestWithLocalizedMessages()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/shipping-quote?weight=5000");
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("tr"));

        var response = await _client.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(["Country alanı zorunludur."], problem.Errors["country"]);
        Assert.Equal(["Weight alanı 0,01 ile 1000 arasında olmalıdır."], problem.Errors["weight"]);
    }

    [Fact]
    public async Task Query_WithLambda_BindsScalarsAndArrays()
    {
        var echoed = await _client.GetFromJsonAsync<EchoResponse>("/echo?message=hi&numbers=1&numbers=2&numbers=3", CancellationToken);
        var empty = await _client.GetFromJsonAsync<EchoResponse>("/echo", CancellationToken);
        var invalid = await _client.GetAsync($"/echo?message={new string('x', 51)}", CancellationToken);

        Assert.Equal(new EchoResponse("hi", 6), echoed);
        Assert.Equal(new EchoResponse("pong", 0), empty);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Action_RunsTheHandlerClassAndReturnsItsResult()
    {
        var prefix = $"d{Guid.NewGuid():N}";
        var first = await CreateProductAsync($"{prefix} A", 200m);
        await CreateProductAsync($"{prefix} B", 100m);
        var other = await CreateProductAsync($"other {prefix}", 100m);

        var response = await _client.PostAsJsonAsync(
            "/apply-discount", new ApplyDiscountRequest { NamePrefix = prefix, Percent = 25 }, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ApplyDiscountResult(2), await response.Content.ReadFromJsonAsync<ApplyDiscountResult>(CancellationToken));

        var discounted = await _client.GetFromJsonAsync<ProductResponse>($"/products/{first.Id}", CancellationToken);
        var untouched = await _client.GetFromJsonAsync<ProductResponse>($"/products/{other.Id}", CancellationToken);
        Assert.Equal(150m, discounted?.Price);
        Assert.Equal(100m, untouched?.Price);
    }

    [Theory]
    [InlineData("ab", 10, "namePrefix")]
    [InlineData("abc", 0, "percent")]
    [InlineData("abc", 91, "percent")]
    public async Task Action_ValidatesTheBody(string prefix, int percent, string expectedField)
    {
        var response = await _client.PostAsJsonAsync(
            "/apply-discount", new ApplyDiscountRequest { NamePrefix = prefix, Percent = percent }, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal([expectedField], problem.Errors.Keys);
    }

    [Fact]
    public async Task Action_WithLambdaAndNoResult_ReturnsNoContent()
    {
        await _client.PutAsJsonAsync(
            "/maintenance-notice", new UpdateMaintenanceNoticeRequest { Enabled = true, Message = "Soon" }, CancellationToken);

        var response = await _client.PostAsJsonAsync("/clear-maintenance-notice", new ClearMaintenanceNotice(), CancellationToken);
        var notice = await _client.GetFromJsonAsync<MaintenanceNoticeResponse>("/maintenance-notice", CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(notice?.Enabled);
        Assert.Equal("Soon", notice?.Message);
    }

    [Fact]
    public async Task ActionAndQuery_UseTheirOwnHttpMethod()
    {
        var getAction = await _client.GetAsync("/apply-discount", CancellationToken);
        var postQuery = await _client.PostAsJsonAsync("/shipping-quote", new { country = "TR" }, CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, getAction.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postQuery.StatusCode);
    }

    private async Task<ProductResponse> CreateProductAsync(string name, decimal price)
    {
        var response = await _client.PostAsJsonAsync(
            "/products", new CreateProductRequest { Name = name, Price = price }, CancellationToken);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ProductResponse>(CancellationToken))!;
    }
}
