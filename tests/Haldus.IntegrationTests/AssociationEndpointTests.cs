using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed class AssociationEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Link_CanBeCreatedCheckedListedFromBothSidesAndRemoved()
    {
        var product = await CreateProductAsync("Keyboard");
        var tag = await CreateTagAsync("wireless");
        var pair = $"/products/{product.Id}/tags/{tag.Id}";

        var before = await _client.GetAsync(pair, CancellationToken);
        var linked = await _client.PutAsync(pair, content: null, CancellationToken);
        var after = await _client.GetAsync(pair, CancellationToken);
        var tags = await _client.GetFromJsonAsync<PagedResult<TagResponse>>($"/products/{product.Id}/tags", CancellationToken);
        var products = await _client.GetFromJsonAsync<PagedResult<ProductResponse>>($"/tags/{tag.Id}/products", CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, after.StatusCode);
        Assert.Equal([tag], tags!.Items);
        Assert.Equal([product.Id], products!.Items.Select(item => item.Id));

        var removed = await _client.DeleteAsync(pair, CancellationToken);
        var removedAgain = await _client.DeleteAsync(pair, CancellationToken);
        var empty = await _client.GetFromJsonAsync<PagedResult<TagResponse>>($"/products/{product.Id}/tags", CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removedAgain.StatusCode);
        Assert.Empty(empty!.Items);
    }

    [Fact]
    public async Task Link_IsIdempotentEvenUnderConcurrentRequests()
    {
        var product = await CreateProductAsync("Mouse");
        var tag = await CreateTagAsync("ergonomic");
        var pair = $"/products/{product.Id}/tags/{tag.Id}";

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => _client.PutAsync(pair, content: null, CancellationToken)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));

        var rows = -1;
        await fixture.WithDbContextAsync(async db =>
            rows = await db.ProductTags.CountAsync(link => link.ProductId == product.Id, CancellationToken));

        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Link_RequiresBothEndsToExist()
    {
        var product = await CreateProductAsync("Monitor");
        var tag = await CreateTagAsync("4k");

        var missingTag = await _client.PutAsync($"/products/{product.Id}/tags/{Guid.NewGuid()}", content: null, CancellationToken);
        var missingProduct = await _client.PutAsync($"/products/{Guid.NewGuid()}/tags/{tag.Id}", content: null, CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, missingTag.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingProduct.StatusCode);

        var problem = await missingTag.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken);
        Assert.StartsWith("Tag with identifier", problem?.Detail);
    }

    [Fact]
    public async Task Link_DisappearsWhenEitherEndIsDeleted()
    {
        var product = await CreateProductAsync("Cable");
        var kept = await CreateTagAsync("usb");
        var doomed = await CreateTagAsync("temporary");
        await _client.PutAsync($"/products/{product.Id}/tags/{kept.Id}", content: null, CancellationToken);
        await _client.PutAsync($"/products/{product.Id}/tags/{doomed.Id}", content: null, CancellationToken);

        await _client.DeleteAsync($"/tags/{doomed.Id}", CancellationToken);
        var tags = await _client.GetFromJsonAsync<PagedResult<TagResponse>>($"/products/{product.Id}/tags", CancellationToken);

        Assert.Equal([kept], tags!.Items);
    }

    [Fact]
    public async Task Link_ToATenantScopedRecordIsInvisibleToOtherTenants()
    {
        var tenant = Guid.NewGuid();
        var intruder = Guid.NewGuid();
        var tag = await CreateTagAsync("vip");
        var customer = await ReadAsync<CustomerResponse>(
            await SendAsync(HttpMethod.Post, "/customers", tenant, new CreateCustomerRequest { Name = "Ada" }));
        var pair = $"/customers/{customer.Id}/tags/{tag.Id}";
        await SendAsync(HttpMethod.Put, pair, tenant);

        var exists = await SendAsync(HttpMethod.Get, pair, intruder);
        var link = await SendAsync(HttpMethod.Put, pair, intruder);
        var unlink = await SendAsync(HttpMethod.Delete, pair, intruder);
        var tagsOfCustomer = await ReadAsync<PagedResult<TagResponse>>(
            await SendAsync(HttpMethod.Get, $"/customers/{customer.Id}/tags", intruder));
        var customersOfTag = await ReadAsync<PagedResult<CustomerResponse>>(
            await SendAsync(HttpMethod.Get, $"/tags/{tag.Id}/customers", intruder));

        Assert.Equal(HttpStatusCode.NotFound, exists.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, link.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unlink.StatusCode);
        Assert.Empty(tagsOfCustomer.Items);
        Assert.Empty(customersOfTag.Items);

        // Bağ yerinde duruyor ve sahibi kiracı onu iki yönden de görüyor.
        var ownTags = await ReadAsync<PagedResult<TagResponse>>(
            await SendAsync(HttpMethod.Get, $"/customers/{customer.Id}/tags", tenant));
        var ownCustomers = await ReadAsync<PagedResult<CustomerResponse>>(
            await SendAsync(HttpMethod.Get, $"/tags/{tag.Id}/customers", tenant));

        Assert.Equal([tag], ownTags.Items);
        Assert.Equal([customer.Id], ownCustomers.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Link_AGlobalEndCannotBeDeletedWhileATenantOwnedRecordIsLinkedToIt()
    {
        // Etiket geneldir, müşteri bir kiracıya aittir. Etiketi silmek bağı da silseydi, etiketi silen
        // kişi başka bir kiracının verisini yok etmiş olurdu.
        var tenant = Guid.NewGuid();
        var tag = await CreateTagAsync("shared");
        var customer = await ReadAsync<CustomerResponse>(
            await SendAsync(HttpMethod.Post, "/customers", tenant, new CreateCustomerRequest { Name = "Ada" }));
        var pair = $"/customers/{customer.Id}/tags/{tag.Id}";
        await SendAsync(HttpMethod.Put, pair, tenant);

        var blocked = await _client.DeleteAsync($"/tags/{tag.Id}", CancellationToken);
        var stillLinked = await SendAsync(HttpMethod.Get, pair, tenant);

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, stillLinked.StatusCode);

        await SendAsync(HttpMethod.Delete, pair, tenant);
        var allowed = await _client.DeleteAsync($"/tags/{tag.Id}", CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task Link_IsRemovedWhenTheTenantOwnedEndIsPurged()
    {
        var tenant = Guid.NewGuid();
        var tag = await CreateTagAsync("ephemeral");
        var customer = await ReadAsync<CustomerResponse>(
            await SendAsync(HttpMethod.Post, "/customers", tenant, new CreateCustomerRequest { Name = "Ada" }));
        await SendAsync(HttpMethod.Put, $"/customers/{customer.Id}/tags/{tag.Id}", tenant);

        await SendAsync(HttpMethod.Delete, $"/customers/{customer.Id}", tenant, ifMatch: "*");
        await SendAsync(HttpMethod.Post, $"/customers/{customer.Id}/purge", tenant, ifMatch: "*");

        // Kiracı kendi kaydını kalıcı silince bağı da gider; etiket artık serbesttir.
        var deleted = await _client.DeleteAsync($"/tags/{tag.Id}", CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task Link_ToASoftDeletedRecordIsHiddenUntilItIsRestored()
    {
        var tenant = Guid.NewGuid();
        var tag = await CreateTagAsync("lapsed");
        var customer = await ReadAsync<CustomerResponse>(
            await SendAsync(HttpMethod.Post, "/customers", tenant, new CreateCustomerRequest { Name = "Ada" }));
        var pair = $"/customers/{customer.Id}/tags/{tag.Id}";
        await SendAsync(HttpMethod.Put, pair, tenant);

        await SendAsync(HttpMethod.Delete, $"/customers/{customer.Id}", tenant, ifMatch: "*");
        var whileDeleted = await SendAsync(HttpMethod.Get, pair, tenant);
        await SendAsync(HttpMethod.Post, $"/customers/{customer.Id}/restore", tenant, ifMatch: "*");
        var afterRestore = await SendAsync(HttpMethod.Get, pair, tenant);

        Assert.Equal(HttpStatusCode.NotFound, whileDeleted.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, afterRestore.StatusCode);
    }

    private async Task<ProductResponse> CreateProductAsync(string name) =>
        await ReadAsync<ProductResponse>(await _client.PostAsJsonAsync(
            "/products", new CreateProductRequest { Name = name, Price = 10m }, CancellationToken));

    private async Task<TagResponse> CreateTagAsync(string name) =>
        await ReadAsync<TagResponse>(await _client.PostAsJsonAsync(
            "/tags", new CreateTagRequest { Name = name }, CancellationToken));

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, Guid tenant, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, tenant.ToString());

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
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
