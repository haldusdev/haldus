using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed class RelationEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task References_AcceptsAnExistingTargetAndExposesTheForeignKey()
    {
        var category = await CreateCategoryAsync("Peripherals");

        var product = await ReadAsync<ProductResponse>(await _client.PostAsJsonAsync(
            "/products", new CreateProductRequest { Name = "Keyboard", Price = 10m, CategoryId = category.Id }, CancellationToken));
        var withoutCategory = await ReadAsync<ProductResponse>(await _client.PostAsJsonAsync(
            "/products", new CreateProductRequest { Name = "Loose", Price = 10m }, CancellationToken));

        Assert.Equal(category.Id, product.CategoryId);
        Assert.Null(withoutCategory.CategoryId);
    }

    [Fact]
    public async Task References_RejectsAMissingTargetOnEveryWrite()
    {
        var product = await ReadAsync<ProductResponse>(await _client.PostAsJsonAsync(
            "/products", new CreateProductRequest { Name = "Keyboard", Price = 10m }, CancellationToken));
        const int missing = int.MaxValue;

        var post = await _client.PostAsJsonAsync(
            "/products", new CreateProductRequest { Name = "X", Price = 1m, CategoryId = missing }, CancellationToken);
        var put = await _client.PutAsJsonAsync(
            $"/products/{product.Id}", new UpdateProductRequest { Name = "X", Price = 1m, CategoryId = missing }, CancellationToken);
        var patch = await _client.PatchAsJsonAsync($"/products/{product.Id}", new { categoryId = missing }, CancellationToken);

        foreach (var response in new[] { post, put, patch })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
            Assert.NotNull(problem);
            Assert.Equal([$"Category '{missing}' was not found."], problem.Errors["categoryId"]);
        }

        var unchanged = await _client.GetFromJsonAsync<ProductResponse>($"/products/{product.Id}", CancellationToken);
        Assert.Null(unchanged?.CategoryId);
    }

    [Fact]
    public async Task References_PatchCanSetAndClearAnOptionalReference()
    {
        var category = await CreateCategoryAsync("Audio");
        var product = await ReadAsync<ProductResponse>(await _client.PostAsJsonAsync(
            "/products", new CreateProductRequest { Name = "Speaker", Price = 10m }, CancellationToken));

        var set = await ReadAsync<ProductResponse>(
            await _client.PatchAsJsonAsync($"/products/{product.Id}", new { categoryId = category.Id }, CancellationToken));
        var untouched = await ReadAsync<ProductResponse>(
            await _client.PatchAsJsonAsync($"/products/{product.Id}", new { name = "Speaker 2" }, CancellationToken));
        var cleared = await ReadAsync<ProductResponse>(
            await _client.PatchAsJsonAsync($"/products/{product.Id}", new { categoryId = (int?)null }, CancellationToken));

        Assert.Equal(category.Id, set.CategoryId);
        Assert.Equal(category.Id, untouched.CategoryId);
        Assert.Null(cleared.CategoryId);
    }

    [Fact]
    public async Task References_WithRestrict_ReportsDeletionOfATargetInUseAsConflict()
    {
        var category = await CreateCategoryAsync("In use");
        var product = await ReadAsync<ProductResponse>(await _client.PostAsJsonAsync(
            "/products", new CreateProductRequest { Name = "Holder", Price = 10m, CategoryId = category.Id }, CancellationToken));

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/categories/{category.Id}");
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("tr"));
        var blocked = await _client.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var problem = await blocked.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken);
        Assert.Equal("Kayıt kullanımda", problem?.Title);

        await _client.DeleteAsync($"/products/{product.Id}", CancellationToken);
        var allowed = await _client.DeleteAsync($"/categories/{category.Id}", CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task References_GeneratedForeignKeyIsSearchable()
    {
        var category = await CreateCategoryAsync("Searchable");
        var product = await ReadAsync<ProductResponse>(await _client.PostAsJsonAsync(
            "/products", new CreateProductRequest { Name = "Findable", Price = 10m, CategoryId = category.Id }, CancellationToken));

        var page = await ReadAsync<PagedResult<ProductResponse>>(await _client.PostAsJsonAsync(
            "/products/search", new { where = new { categoryId = new { eq = category.Id } } }, CancellationToken));

        Assert.Equal([product.Id], page.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Hierarchical_BuildsATreeThatCanBeWalkedLevelByLevel()
    {
        var root = await CreateCategoryAsync("Root");
        var child = await CreateCategoryAsync("Child", root.Id);
        var grandchild = await CreateCategoryAsync("Grandchild", child.Id);

        var children = await _client.GetFromJsonAsync<PagedResult<CategoryResponse>>(
            $"/categories?parentId={root.Id}", CancellationToken);
        var roots = await _client.GetFromJsonAsync<PagedResult<CategoryResponse>>(
            "/categories?roots=true&pageSize=100", CancellationToken);

        Assert.Equal(root.Id, child.ParentId);
        Assert.Equal([child.Id], children!.Items.Select(item => item.Id));
        Assert.Contains(roots!.Items, item => item.Id == root.Id);
        Assert.DoesNotContain(roots.Items, item => item.Id == child.Id || item.Id == grandchild.Id);
    }

    [Fact]
    public async Task Hierarchical_RejectsMovingANodeUnderItselfOrItsDescendants()
    {
        var root = await CreateCategoryAsync("Root");
        var child = await CreateCategoryAsync("Child", root.Id);
        var grandchild = await CreateCategoryAsync("Grandchild", child.Id);
        var other = await CreateCategoryAsync("Other");

        var underItself = await _client.PatchAsJsonAsync($"/categories/{root.Id}", new { parentId = root.Id }, CancellationToken);
        var underDescendant = await _client.PutAsJsonAsync(
            $"/categories/{root.Id}", new UpdateCategoryRequest { Name = "Root", ParentId = grandchild.Id }, CancellationToken);
        var underMissing = await _client.PatchAsJsonAsync($"/categories/{root.Id}", new { parentId = int.MaxValue }, CancellationToken);
        var underOther = await _client.PatchAsJsonAsync($"/categories/{child.Id}", new { parentId = other.Id }, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, underItself.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, underDescendant.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, underMissing.StatusCode);

        var problem = await underDescendant.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(
            ["A record cannot be placed under itself or under one of its own descendants."],
            problem.Errors["parentId"]);

        // Geçerli bir taşıma çalışır ve ağacın geri kalanı onunla birlikte gelir.
        Assert.Equal(other.Id, (await ReadAsync<CategoryResponse>(underOther)).ParentId);
    }

    [Fact]
    public async Task Hierarchical_NodeWithChildrenCannotBeDeleted()
    {
        var root = await CreateCategoryAsync("Root");
        var child = await CreateCategoryAsync("Child", root.Id);

        var blocked = await _client.DeleteAsync($"/categories/{root.Id}", CancellationToken);
        await _client.DeleteAsync($"/categories/{child.Id}", CancellationToken);
        var allowed = await _client.DeleteAsync($"/categories/{root.Id}", CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task Under_PlacesTheResourceBelowItsParentAndTakesTheParentFromTheRoute()
    {
        var tenant = Guid.NewGuid();
        var customer = await CreateCustomerAsync(tenant, "Ada");
        var other = await CreateCustomerAsync(tenant, "Grace");

        var created = await SendAsync(
            HttpMethod.Post, $"/customers/{customer.Id}/addresses", tenant,
            new CreateAddressRequest { Line = "1 Main St", City = "Izmir" });
        var address = await ReadAsync<AddressResponse>(created);
        await SendAsync(
            HttpMethod.Post, $"/customers/{other.Id}/addresses", tenant,
            new CreateAddressRequest { Line = "2 Side St", City = "Ankara" });

        var list = await ReadAsync<PagedResult<AddressResponse>>(
            await SendAsync(HttpMethod.Get, $"/customers/{customer.Id}/addresses", tenant));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/customers/{customer.Id}/addresses/{address.Id}", created.Headers.Location?.OriginalString);
        Assert.Equal(customer.Id, address.CustomerId);
        Assert.Equal([address.Id], list.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Under_ARecordIsNotReachableThroughAnotherParent()
    {
        var tenant = Guid.NewGuid();
        var owner = await CreateCustomerAsync(tenant, "Owner");
        var stranger = await CreateCustomerAsync(tenant, "Stranger");
        var address = await ReadAsync<AddressResponse>(await SendAsync(
            HttpMethod.Post, $"/customers/{owner.Id}/addresses", tenant,
            new CreateAddressRequest { Line = "1 Main St", City = "Izmir" }));

        var read = await SendAsync(HttpMethod.Get, $"/customers/{stranger.Id}/addresses/{address.Id}", tenant);
        var update = await SendAsync(
            HttpMethod.Put, $"/customers/{stranger.Id}/addresses/{address.Id}", tenant,
            new UpdateAddressRequest { Line = "Hijacked", City = "X" });
        var delete = await SendAsync(HttpMethod.Delete, $"/customers/{stranger.Id}/addresses/{address.Id}", tenant);
        var stillThere = await ReadAsync<AddressResponse>(
            await SendAsync(HttpMethod.Get, $"/customers/{owner.Id}/addresses/{address.Id}", tenant));

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("1 Main St", stillThere.Line);
    }

    [Fact]
    public async Task Under_AnotherTenantCannotReachRecordsBelowAParentItCannotSee()
    {
        // Adres kendisi kiracıya bağlı değildir; koruma, ebeveynin (müşterinin) görünürlüğünden gelir.
        var tenant = Guid.NewGuid();
        var intruder = Guid.NewGuid();
        var customer = await CreateCustomerAsync(tenant, "Ada");
        var address = await ReadAsync<AddressResponse>(await SendAsync(
            HttpMethod.Post, $"/customers/{customer.Id}/addresses", tenant,
            new CreateAddressRequest { Line = "1 Main St", City = "Izmir" }));
        var url = $"/customers/{customer.Id}/addresses";

        var list = await ReadAsync<PagedResult<AddressResponse>>(await SendAsync(HttpMethod.Get, url, intruder));
        var read = await SendAsync(HttpMethod.Get, $"{url}/{address.Id}", intruder);
        var create = await SendAsync(HttpMethod.Post, url, intruder, new CreateAddressRequest { Line = "Planted", City = "X" });
        var update = await SendAsync(
            HttpMethod.Put, $"{url}/{address.Id}", intruder, new UpdateAddressRequest { Line = "Hijacked", City = "X" });
        var patch = await SendAsync(HttpMethod.Patch, $"{url}/{address.Id}", intruder, new { line = "Hijacked" });
        var delete = await SendAsync(HttpMethod.Delete, $"{url}/{address.Id}", intruder);
        var withoutTenant = await _client.GetFromJsonAsync<PagedResult<AddressResponse>>(url, CancellationToken);

        Assert.Empty(list.Items);
        Assert.Equal(0, list.TotalCount);
        Assert.Empty(withoutTenant!.Items);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var intact = await ReadAsync<PagedResult<AddressResponse>>(await SendAsync(HttpMethod.Get, url, tenant));
        Assert.Equal("1 Main St", Assert.Single(intact.Items).Line);
    }

    [Fact]
    public async Task Under_RecordsOfASoftDeletedParentAreHiddenAndCascadeWhenItIsPurged()
    {
        var tenant = Guid.NewGuid();
        var customer = await CreateCustomerAsync(tenant, "Ada");
        var url = $"/customers/{customer.Id}/addresses";
        var address = await ReadAsync<AddressResponse>(
            await SendAsync(HttpMethod.Post, url, tenant, new CreateAddressRequest { Line = "1 Main St", City = "Izmir" }));

        await SendAsync(HttpMethod.Delete, $"/customers/{customer.Id}", tenant, ifMatch: "*");
        var hidden = await SendAsync(HttpMethod.Get, $"{url}/{address.Id}", tenant);

        // Sayım filtreler olmadan yapılır; aksi halde ebeveyni görünmeyen adresler zaten sayılmazdı.
        var beforePurge = await CountAddressRowsAsync(customer.Id);
        await SendAsync(HttpMethod.Post, $"/customers/{customer.Id}/purge", tenant, ifMatch: "*");
        var afterPurge = await CountAddressRowsAsync(customer.Id);

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(1, beforePurge);
        Assert.Equal(0, afterPurge);
    }

    [Fact]
    public async Task Under_VisibilityCarriesThroughToResourcesNestedBelowTheChild()
    {
        var tenant = Guid.NewGuid();
        var intruder = Guid.NewGuid();
        var customer = await CreateCustomerAsync(tenant, "Ada");
        var address = await ReadAsync<AddressResponse>(await SendAsync(
            HttpMethod.Post, $"/customers/{customer.Id}/addresses", tenant,
            new CreateAddressRequest { Line = "1 Main St", City = "Izmir" }));
        var url = $"/addresses/{address.Id}/address-notes";

        var note = await ReadAsync<AddressNoteResponse>(
            await SendAsync(HttpMethod.Post, url, tenant, new CreateAddressNoteRequest { Text = "Ring twice" }));

        // Not ne kiracıya ne müşteriye doğrudan bağlıdır; yine de yalnızca adresin sahibi kiracı ulaşabilir.
        var planted = await SendAsync(HttpMethod.Post, url, intruder, new CreateAddressNoteRequest { Text = "Planted" });
        var list = await ReadAsync<PagedResult<AddressNoteResponse>>(await SendAsync(HttpMethod.Get, url, intruder));
        var read = await SendAsync(HttpMethod.Get, $"{url}/{note.Id}", intruder);
        var delete = await SendAsync(HttpMethod.Delete, $"{url}/{note.Id}", intruder);

        Assert.Equal(HttpStatusCode.NotFound, planted.StatusCode);
        Assert.Empty(list.Items);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var own = await ReadAsync<PagedResult<AddressNoteResponse>>(await SendAsync(HttpMethod.Get, url, tenant));
        Assert.Equal("Ring twice", Assert.Single(own.Items).Text);

        // Müşteri silinince zincirin altındaki her şey gizlenir.
        await SendAsync(HttpMethod.Delete, $"/customers/{customer.Id}", tenant, ifMatch: "*");
        var afterDelete = await ReadAsync<PagedResult<AddressNoteResponse>>(await SendAsync(HttpMethod.Get, url, tenant));
        Assert.Empty(afterDelete.Items);
    }

    [Fact]
    public async Task References_CannotPointAtARecordWhoseParentIsNotVisible()
    {
        var tenant = Guid.NewGuid();
        var intruder = Guid.NewGuid();
        var customer = await CreateCustomerAsync(tenant, "Ada");
        var address = await ReadAsync<AddressResponse>(await SendAsync(
            HttpMethod.Post, $"/customers/{customer.Id}/addresses", tenant,
            new CreateAddressRequest { Line = "1 Main St", City = "Izmir" }));

        var own = await SendAsync(
            HttpMethod.Post, "/deliveries", tenant, new CreateDeliveryRequest { Carrier = "Fast", AddressId = address.Id });
        var foreign = await SendAsync(
            HttpMethod.Post, "/deliveries", intruder, new CreateDeliveryRequest { Carrier = "Sneaky", AddressId = address.Id });
        var delivery = await ReadAsync<DeliveryResponse>(own);
        var redirected = await SendAsync(
            HttpMethod.Patch, $"/deliveries/{delivery.Id}", intruder, new { addressId = address.Id });

        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, redirected.StatusCode);

        var problem = await foreign.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(["addressId"], problem.Errors.Keys);
    }

    private async Task<int> CountAddressRowsAsync(Guid customerId)
    {
        var count = -1;
        await fixture.WithDbContextAsync(async db =>
            count = await db.Addresses.IgnoreQueryFilters().CountAsync(item => item.CustomerId == customerId, CancellationToken));

        return count;
    }

    [Fact]
    public async Task Under_CreateBelowAMissingParent_ReturnsNotFound()
    {
        var response = await SendAsync(
            HttpMethod.Post, $"/customers/{Guid.NewGuid()}/addresses", Guid.NewGuid(),
            new CreateAddressRequest { Line = "Nowhere", City = "X" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<CategoryResponse> CreateCategoryAsync(string name, int? parentId = null) =>
        await ReadAsync<CategoryResponse>(await _client.PostAsJsonAsync(
            "/categories", new CreateCategoryRequest { Name = $"{name} {Guid.NewGuid():N}", ParentId = parentId }, CancellationToken));

    private async Task<CustomerResponse> CreateCustomerAsync(Guid tenant, string name) =>
        await ReadAsync<CustomerResponse>(
            await SendAsync(HttpMethod.Post, "/customers", tenant, new CreateCustomerRequest { Name = name }));

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
