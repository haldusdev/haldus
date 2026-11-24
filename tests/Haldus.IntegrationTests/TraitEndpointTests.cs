using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed class TraitEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MultiTenant_WithoutTenant_ReturnsLocalizedForbidden()
    {
        var response = await SendAsync(HttpMethod.Get, "/customers", tenant: null, language: "tr");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken);
        Assert.Equal("Kiracı gerekli", problem?.Title);
    }

    [Fact]
    public async Task MultiTenant_HidesRecordsOfOtherTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (customer, _) = await CreateCustomerAsync(tenantA, "Ada");

        var seenByOwner = await SendAsync(HttpMethod.Get, $"/customers/{customer.Id}", tenantA);
        var seenByOther = await SendAsync(HttpMethod.Get, $"/customers/{customer.Id}", tenantB);
        var listOfOther = await ReadAsync<PagedResult<CustomerResponse>>(
            await SendAsync(HttpMethod.Get, "/customers?pageSize=100", tenantB));

        Assert.Equal(HttpStatusCode.OK, seenByOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, seenByOther.StatusCode);
        Assert.Empty(listOfOther.Items);
    }

    [Fact]
    public async Task MultiTenant_OtherTenantCannotModifyOrDelete()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (customer, etag) = await CreateCustomerAsync(tenantA, "Ada");

        var update = await SendAsync(
            HttpMethod.Put, $"/customers/{customer.Id}", tenantB,
            body: new UpdateCustomerRequest { Name = "Hijacked" }, ifMatch: etag);
        var delete = await SendAsync(HttpMethod.Delete, $"/customers/{customer.Id}", tenantB, ifMatch: "*");

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task Auditable_StampsCreationAndModification()
    {
        var tenant = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var (created, etag) = await CreateCustomerAsync(tenant, "Ada", user: "alice");

        Assert.Equal("alice", created.CreatedBy);
        Assert.InRange(created.CreatedAt, before, DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Null(created.UpdatedAt);
        Assert.Null(created.UpdatedBy);

        var updated = await ReadAsync<CustomerResponse>(await SendAsync(
            HttpMethod.Put, $"/customers/{created.Id}", tenant, user: "bob",
            body: new UpdateCustomerRequest { Name = "Ada L." }, ifMatch: etag));

        Assert.Equal("alice", updated.CreatedBy);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.Equal("bob", updated.UpdatedBy);
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task ConcurrencyChecked_WithoutIfMatch_ReturnsPreconditionRequired()
    {
        var tenant = Guid.NewGuid();
        var (customer, _) = await CreateCustomerAsync(tenant, "Ada");

        var response = await SendAsync(
            HttpMethod.Put, $"/customers/{customer.Id}", tenant, body: new UpdateCustomerRequest { Name = "X" });

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrencyChecked_WithStaleVersion_ReturnsPreconditionFailed()
    {
        var tenant = Guid.NewGuid();
        var (customer, firstETag) = await CreateCustomerAsync(tenant, "Ada");

        var first = await SendAsync(
            HttpMethod.Put, $"/customers/{customer.Id}", tenant,
            body: new UpdateCustomerRequest { Name = "First writer" }, ifMatch: firstETag);
        var second = await SendAsync(
            HttpMethod.Put, $"/customers/{customer.Id}", tenant,
            body: new UpdateCustomerRequest { Name = "Second writer" }, ifMatch: firstETag);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotEqual(firstETag, first.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, second.StatusCode);

        var current = await ReadAsync<CustomerResponse>(await SendAsync(HttpMethod.Get, $"/customers/{customer.Id}", tenant));
        Assert.Equal("First writer", current.Name);
    }

    [Theory]
    [InlineData("*", HttpStatusCode.OK)]
    [InlineData("not-a-version", HttpStatusCode.PreconditionFailed)]
    public async Task ConcurrencyChecked_HandlesWildcardAndMalformedIfMatch(string ifMatch, HttpStatusCode expected)
    {
        var tenant = Guid.NewGuid();
        var (customer, _) = await CreateCustomerAsync(tenant, "Ada");

        var response = await SendAsync(
            HttpMethod.Put, $"/customers/{customer.Id}", tenant,
            body: new UpdateCustomerRequest { Name = "Renamed" }, ifMatch: ifMatch);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrencyChecked_GetReturnsTheCurrentETag()
    {
        var tenant = Guid.NewGuid();
        var (customer, createdETag) = await CreateCustomerAsync(tenant, "Ada");

        var response = await SendAsync(HttpMethod.Get, $"/customers/{customer.Id}", tenant);

        Assert.Equal(createdETag, response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Patch_ChangesOnlyTheFieldsThatWereSent()
    {
        var tenant = Guid.NewGuid();
        var (customer, etag) = await CreateCustomerAsync(tenant, "Ada", email: "ada@example.com", loyaltyPoints: 7);

        var patched = await ReadAsync<CustomerResponse>(await SendAsync(
            HttpMethod.Patch, $"/customers/{customer.Id}", tenant, body: new { loyaltyPoints = 12 }, ifMatch: etag));

        Assert.Equal("Ada", patched.Name);
        Assert.Equal("ada@example.com", patched.Email);
        Assert.Equal(12, patched.LoyaltyPoints);
    }

    [Fact]
    public async Task Patch_WithExplicitNull_ClearsNullableFieldAndRejectsNonNullableOne()
    {
        var tenant = Guid.NewGuid();
        var (customer, etag) = await CreateCustomerAsync(tenant, "Ada", email: "ada@example.com");

        var rejected = await SendAsync(
            HttpMethod.Patch, $"/customers/{customer.Id}", tenant, body: new { name = (string?)null }, ifMatch: etag);
        var cleared = await SendAsync(
            HttpMethod.Patch, $"/customers/{customer.Id}", tenant, body: new { email = (string?)null }, ifMatch: etag);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(["The Name field is required."], problem.Errors["name"]);

        var customerAfter = await ReadAsync<CustomerResponse>(cleared);
        Assert.Equal("Ada", customerAfter.Name);
        Assert.Null(customerAfter.Email);
    }

    [Fact]
    public async Task Patch_ValidatesOnlyTheFieldsThatWereSent()
    {
        var tenant = Guid.NewGuid();
        var (customer, etag) = await CreateCustomerAsync(tenant, "Ada");

        var response = await SendAsync(
            HttpMethod.Patch, $"/customers/{customer.Id}", tenant, body: new { email = "not-an-email" }, ifMatch: etag);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(["email"], problem.Errors.Keys);
    }

    [Fact]
    public async Task SoftDeletable_DeleteHidesTheRecordAndRestoreBringsItBack()
    {
        var tenant = Guid.NewGuid();
        var (customer, etag) = await CreateCustomerAsync(tenant, "Ada");

        var deleted = await SendAsync(HttpMethod.Delete, $"/customers/{customer.Id}", tenant, ifMatch: etag);
        var hidden = await SendAsync(HttpMethod.Get, $"/customers/{customer.Id}", tenant);
        var defaultList = await ReadAsync<PagedResult<CustomerResponse>>(
            await SendAsync(HttpMethod.Get, "/customers", tenant));
        var fullList = await ReadAsync<PagedResult<CustomerResponse>>(
            await SendAsync(HttpMethod.Get, "/customers?includeDeleted=true", tenant));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Empty(defaultList.Items);
        Assert.NotNull(Assert.Single(fullList.Items).DeletedAt);

        var restored = await ReadAsync<CustomerResponse>(
            await SendAsync(HttpMethod.Post, $"/customers/{customer.Id}/restore", tenant));
        var visible = await SendAsync(HttpMethod.Get, $"/customers/{customer.Id}", tenant);

        Assert.Null(restored.DeletedAt);
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
    }

    [Fact]
    public async Task SoftDeletable_DeleteWithStaleVersion_ReturnsPreconditionFailedAndKeepsTheRecord()
    {
        var tenant = Guid.NewGuid();
        var (customer, staleETag) = await CreateCustomerAsync(tenant, "Ada");
        await SendAsync(
            HttpMethod.Put, $"/customers/{customer.Id}", tenant,
            body: new UpdateCustomerRequest { Name = "Changed" }, ifMatch: staleETag);

        var deleted = await SendAsync(HttpMethod.Delete, $"/customers/{customer.Id}", tenant, ifMatch: staleETag);
        var stillThere = await SendAsync(HttpMethod.Get, $"/customers/{customer.Id}", tenant);

        Assert.Equal(HttpStatusCode.PreconditionFailed, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }

    [Fact]
    public async Task SoftDeletable_RestoreOfLiveRecord_ReturnsNotFound()
    {
        var tenant = Guid.NewGuid();
        var (customer, _) = await CreateCustomerAsync(tenant, "Ada");

        var response = await SendAsync(HttpMethod.Post, $"/customers/{customer.Id}/restore", tenant);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SoftDeletable_PurgeRemovesTheRecordPermanentlyWithinTheTenant()
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var (customer, etag) = await CreateCustomerAsync(tenant, "Ada");
        await SendAsync(HttpMethod.Delete, $"/customers/{customer.Id}", tenant, ifMatch: etag);

        var purgeByOther = await SendAsync(HttpMethod.Post, $"/customers/{customer.Id}/purge", otherTenant);
        var purge = await SendAsync(HttpMethod.Post, $"/customers/{customer.Id}/purge", tenant);
        var fullList = await ReadAsync<PagedResult<CustomerResponse>>(
            await SendAsync(HttpMethod.Get, "/customers?includeDeleted=true", tenant));

        Assert.Equal(HttpStatusCode.NotFound, purgeByOther.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, purge.StatusCode);
        Assert.Empty(fullList.Items);
    }

    [Fact]
    public async Task Archivable_HidesFromListsButKeepsTheRecordReadable()
    {
        var brand = await ReadAsync<BrandResponse>(
            await _client.PostAsJsonAsync("/brands", new CreateBrandRequest { Name = $"Brand {Guid.NewGuid():N}" }, CancellationToken));

        var archived = await ReadAsync<BrandResponse>(
            await _client.PostAsync($"/brands/{brand.Id}/archive", content: null, CancellationToken));
        var defaultList = await _client.GetFromJsonAsync<PagedResult<BrandResponse>>("/brands?pageSize=100", CancellationToken);
        var fullList = await _client.GetFromJsonAsync<PagedResult<BrandResponse>>(
            "/brands?pageSize=100&includeArchived=true", CancellationToken);
        var single = await _client.GetAsync($"/brands/{brand.Id}", CancellationToken);

        Assert.NotNull(archived.ArchivedAt);
        Assert.DoesNotContain(defaultList!.Items, item => item.Id == brand.Id);
        Assert.Contains(fullList!.Items, item => item.Id == brand.Id);
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);

        // Arşivleme yanıtındaki zaman, sonradan okunan değerle birebir aynı olmalı.
        Assert.Equal(archived, await single.Content.ReadFromJsonAsync<BrandResponse>(CancellationToken));

        var unarchived = await ReadAsync<BrandResponse>(
            await _client.PostAsync($"/brands/{brand.Id}/unarchive", content: null, CancellationToken));
        Assert.Null(unarchived.ArchivedAt);
    }

    [Fact]
    public async Task Without_RemovesTheEndpoint()
    {
        var brand = await ReadAsync<BrandResponse>(
            await _client.PostAsJsonAsync("/brands", new CreateBrandRequest { Name = "Undeletable" }, CancellationToken));

        var response = await _client.DeleteAsync($"/brands/{brand.Id}", CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private async Task<(CustomerResponse Customer, string ETag)> CreateCustomerAsync(
        Guid tenant, string name, string? user = null, string? email = null, int loyaltyPoints = 0)
    {
        var response = await SendAsync(
            HttpMethod.Post, "/customers", tenant, user,
            body: new CreateCustomerRequest { Name = name, Email = email, LoyaltyPoints = loyaltyPoints });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);

        return (await ReadAsync<CustomerResponse>(response), response.Headers.ETag.Tag);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        Guid? tenant,
        string? user = null,
        object? body = null,
        string? ifMatch = null,
        string? language = null)
    {
        using var request = new HttpRequestMessage(method, url);

        if (tenant is not null)
        {
            request.Headers.Add(DemoIdentity.TenantHeader, tenant.Value.ToString());
        }

        if (user is not null)
        {
            request.Headers.Add(DemoIdentity.UserHeader, user);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (language is not null)
        {
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(language));
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
