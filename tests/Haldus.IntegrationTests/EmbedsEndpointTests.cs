using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop.Features.Platform;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed class EmbedsEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmbeddedValue_RoundTripsAsANestedObject()
    {
        var created = await CreatePlanAsync("Pro", new Money(99.5m, "EUR"));

        var read = await _client.GetFromJsonAsync<PlanResponse>($"/plans/{created.Id}", CancellationToken);
        var list = await _client.GetFromJsonAsync<PagedResult<PlanResponse>>("/plans?pageSize=100", CancellationToken);

        Assert.Equal(new Money(99.5m, "EUR"), created.Price);
        Assert.Equal(created, read);
        Assert.Contains(list!.Items, item => item.Id == created.Id && item.Price == new Money(99.5m, "EUR"));
    }

    [Fact]
    public async Task EmbeddedValue_IsRequiredWhenItsPropertyIsNotNullable()
    {
        var response = await _client.PostAsJsonAsync("/plans", new { name = "No price" }, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(["The Price field is required."], problem.Errors["price"]);
    }

    [Fact]
    public async Task EmbeddedValue_ItsOwnRulesRunAndAreAddressedByPath()
    {
        var post = await _client.PostAsJsonAsync(
            "/plans", new { name = "Bad", price = new { amount = -1, currency = "EURO" } }, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        var problem = await post.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(["price.amount", "price.currency"], problem.Errors.Keys.Order());
        Assert.Equal(["The Amount field must be between 0 and 1000000000."], problem.Errors["price.amount"]);
        Assert.Equal(["The Currency field must be between 3 and 3 characters long."], problem.Errors["price.currency"]);
    }

    [Fact]
    public async Task EmbeddedValue_RulesAlsoRunOnPutAndPatch()
    {
        var plan = await CreatePlanAsync("Basic", new Money(10m, "TRY"));
        var invalid = new { amount = 5, currency = "X" };

        var put = await _client.PutAsJsonAsync($"/plans/{plan.Id}", new { name = "Basic", price = invalid }, CancellationToken);
        var patch = await _client.PatchAsJsonAsync($"/plans/{plan.Id}", new { price = invalid }, CancellationToken);
        var cleared = await _client.PatchAsJsonAsync($"/plans/{plan.Id}", new { price = (object?)null }, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, cleared.StatusCode);

        var unchanged = await _client.GetFromJsonAsync<PlanResponse>($"/plans/{plan.Id}", CancellationToken);
        Assert.Equal(new Money(10m, "TRY"), unchanged?.Price);
    }

    [Fact]
    public async Task EmbeddedValue_IsReplacedAsAWholeAndLeftAloneWhenNotSent()
    {
        var plan = await CreatePlanAsync("Basic", new Money(10m, "TRY"));

        var renamed = await ReadAsync<PlanResponse>(
            await _client.PatchAsJsonAsync($"/plans/{plan.Id}", new { name = "Starter" }, CancellationToken));
        var repriced = await ReadAsync<PlanResponse>(await _client.PatchAsJsonAsync(
            $"/plans/{plan.Id}", new { price = new Money(15m, "USD") }, CancellationToken));
        var replaced = await ReadAsync<PlanResponse>(await _client.PutAsJsonAsync(
            $"/plans/{plan.Id}", new UpdatePlanRequest { Name = "Final", Price = new Money(20m, "EUR") }, CancellationToken));

        Assert.Equal(new Money(10m, "TRY"), renamed.Price);
        Assert.Equal("Starter", repriced.Name);
        Assert.Equal(new Money(15m, "USD"), repriced.Price);
        Assert.Equal(new Money(20m, "EUR"), replaced.Price);

        var persisted = await _client.GetFromJsonAsync<PlanResponse>($"/plans/{plan.Id}", CancellationToken);
        Assert.Equal(replaced, persisted);
    }

    private async Task<PlanResponse> CreatePlanAsync(string name, Money price) =>
        await ReadAsync<PlanResponse>(await _client.PostAsJsonAsync(
            "/plans", new CreatePlanRequest { Name = name, Price = price }, CancellationToken));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken)}");

        return (await response.Content.ReadFromJsonAsync<T>(CancellationToken))!;
    }
}
