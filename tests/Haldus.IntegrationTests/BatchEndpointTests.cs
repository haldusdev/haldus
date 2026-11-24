using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed class BatchEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();

    /// <summary>Ürünler ve etiketler geneldir; her test kendi kayıtlarını bu önekle ayırır.</summary>
    private readonly string _prefix = $"b{Guid.NewGuid():N}"[..12];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BatchCreate_SavesEveryItemAndReturnsThemInOrder()
    {
        var response = await SendAsync(HttpMethod.Post, "/products/batch-create", new
        {
            items = new object[]
            {
                new { name = $"{_prefix}-a", price = 10 },
                new { name = $"{_prefix}-b", price = 20 },
                new { name = $"{_prefix}-c", price = 30 },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadAsync<BatchResult<ProductResponse>>(response);

        Assert.Equal([$"{_prefix}-a", $"{_prefix}-b", $"{_prefix}-c"], result.Items.Select(product => product.Name));
        Assert.All(result.Items, product => Assert.NotEqual(Guid.Empty, product.Id));
        Assert.Equal(3, await CountProductsAsync());
    }

    [Fact]
    public async Task BatchCreate_Atomic_SavesNothingWhenAnItemIsInvalid()
    {
        var response = await SendAsync(HttpMethod.Post, "/products/batch-create", new
        {
            items = new object[]
            {
                new { name = $"{_prefix}-a", price = 10 },
                new { name = $"{_prefix}-b", price = -1 },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await ReadJsonAsync(response);

        // Hata, hangi öğenin hangi alanına ait olduğunu söyler.
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("items[1].price", out _));
        Assert.Equal(0, await CountProductsAsync());
    }

    [Fact]
    public async Task BatchCreate_Atomic_RollsBackEarlierItemsWhenALaterOneBreaksARule()
    {
        // İlk öğe geçerlidir ve kaydedilir; üçüncüsü var olmayan bir kategoriye bağlanmak ister.
        var response = await SendAsync(HttpMethod.Post, "/products/batch-create", new
        {
            items = new object[]
            {
                new { name = $"{_prefix}-a", price = 10 },
                new { name = $"{_prefix}-b", price = 20 },
                new { name = $"{_prefix}-c", price = 30, categoryId = 999_999 },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await ReadJsonAsync(response);

        Assert.Equal(2, problem.RootElement.GetProperty("index").GetInt32());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("items[2].categoryId", out _));
        Assert.Equal(0, await CountProductsAsync());
    }

    [Fact]
    public async Task BatchUpdate_ReplacesEveryItemOrNone()
    {
        var created = await CreateProductsAsync("a", "b");

        var failed = await SendAsync(HttpMethod.Post, "/products/batch-update", new
        {
            items = new object[]
            {
                new { id = created[0].Id, name = $"{_prefix}-changed", price = 99 },
                new { id = Guid.NewGuid(), name = $"{_prefix}-ghost", price = 1 },
            },
        });

        Assert.Equal(HttpStatusCode.NotFound, failed.StatusCode);
        using (var problem = await ReadJsonAsync(failed))
        {
            Assert.Equal(1, problem.RootElement.GetProperty("index").GetInt32());
        }

        // İlk öğenin değişikliği de geri alındı.
        var untouched = await ReadAsync<ProductResponse>(await SendAsync(HttpMethod.Get, $"/products/{created[0].Id}"));
        Assert.Equal($"{_prefix}-a", untouched.Name);

        var succeeded = await ReadAsync<BatchResult<ProductResponse>>(await SendAsync(HttpMethod.Post, "/products/batch-update", new
        {
            items = new object[]
            {
                new { id = created[1].Id, name = $"{_prefix}-b2", price = 21 },
                new { id = created[0].Id, name = $"{_prefix}-a2", price = 11 },
            },
        }));

        Assert.Equal([$"{_prefix}-b2", $"{_prefix}-a2"], succeeded.Items.Select(product => product.Name));
        Assert.Equal(11m, (await ReadAsync<ProductResponse>(await SendAsync(HttpMethod.Get, $"/products/{created[0].Id}"))).Price);
    }

    [Fact]
    public async Task BatchDelete_RemovesEveryItemOrNone()
    {
        var created = await CreateProductsAsync("a", "b", "c");

        var failed = await SendAsync(
            HttpMethod.Post, "/products/batch-delete", new { ids = new[] { created[0].Id, Guid.NewGuid() } });

        Assert.Equal(HttpStatusCode.NotFound, failed.StatusCode);
        Assert.Equal(3, await CountProductsAsync());

        var succeeded = await SendAsync(
            HttpMethod.Post, "/products/batch-delete", new { ids = new[] { created[0].Id, created[1].Id, created[0].Id } });

        Assert.Equal(HttpStatusCode.NoContent, succeeded.StatusCode);
        Assert.Equal(1, await CountProductsAsync());
    }

    [Fact]
    public async Task BatchGet_ReturnsWhatItFindsAndListsTheRest()
    {
        var created = await CreateProductsAsync("a", "b");
        var missing = Guid.NewGuid();

        var result = await ReadAsync<BatchGetResult<ProductResponse, Guid>>(await SendAsync(
            HttpMethod.Post, "/products/batch-get", new { ids = new[] { created[1].Id, missing, created[0].Id } }));

        Assert.Equal(created.Select(product => product.Id).Order(), result.Items.Select(product => product.Id).Order());
        Assert.Equal([missing], result.NotFound);
    }

    [Fact]
    public async Task Batch_RejectsMoreItemsThanAllowed()
    {
        var response = await SendAsync(HttpMethod.Post, "/tags/batch-create", new
        {
            items = Enumerable.Range(0, 6).Select(index => new { name = $"{_prefix}-{index}" }),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await ReadJsonAsync(response);
        Assert.Equal("A batch request can contain at most 5 items.", problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(0, await CountTagsAsync());
    }

    [Fact]
    public async Task BatchCreate_Partial_SavesTheValidItemsAndReportsEachOne()
    {
        var response = await SendAsync(HttpMethod.Post, "/tags/batch-create", new
        {
            items = new object?[]
            {
                new { name = $"{_prefix}-a" },
                new { name = "" },
                null,
                new { name = $"{_prefix}-b" },
            },
        });

        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
        var result = await ReadAsync<BatchResults<TagResponse>>(response);

        Assert.Equal([0, 1, 2, 3], result.Results.Select(item => item.Index));
        Assert.Equal([201, 400, 400, 201], result.Results.Select(item => item.Status));
        Assert.Equal($"{_prefix}-a", result.Results[0].Item!.Name);
        Assert.Null(result.Results[0].Problem);
        Assert.Null(result.Results[1].Item);

        // Reddedilen öğenin hatası, tek başına gönderilseydi dönecek hatayla aynıdır.
        using var raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken));
        Assert.True(raw.RootElement.GetProperty("results")[1].GetProperty("problem").GetProperty("errors").TryGetProperty("name", out _));

        Assert.Equal(2, await CountTagsAsync());
    }

    [Fact]
    public async Task BatchUpdateAndDelete_Partial_ApplyWhatTheyCan()
    {
        var created = (await ReadAsync<BatchResults<TagResponse>>(await SendAsync(HttpMethod.Post, "/tags/batch-create", new
        {
            items = new object[] { new { name = $"{_prefix}-a" }, new { name = $"{_prefix}-b" } },
        }))).Results.Select(item => item.Item!).ToList();

        var updated = await ReadAsync<BatchResults<TagResponse>>(await SendAsync(HttpMethod.Post, "/tags/batch-update", new
        {
            items = new object[]
            {
                new { id = Guid.NewGuid(), name = $"{_prefix}-ghost" },
                new { id = created[0].Id, name = $"{_prefix}-a2" },
            },
        }));

        Assert.Equal([404, 200], updated.Results.Select(item => item.Status));
        Assert.Equal($"{_prefix}-a2", updated.Results[1].Item!.Name);

        var deleted = await SendAsync(
            HttpMethod.Post, "/tags/batch-delete", new { ids = new[] { created[1].Id, Guid.NewGuid() } });

        Assert.Equal(HttpStatusCode.MultiStatus, deleted.StatusCode);
        var results = await ReadAsync<BatchResults<Guid>>(deleted);
        Assert.Equal([204, 404], results.Results.Select(item => item.Status));
        Assert.Equal(created[1].Id, results.Results[0].Item);
        Assert.Equal(1, await CountTagsAsync());
    }

    [Fact]
    public async Task Batch_UnderAParent_TakesTheParentFromTheRouteAndKeepsItsVisibility()
    {
        var customer = await ReadAsync<CustomerResponse>(
            await SendAsync(HttpMethod.Post, "/customers", new { name = "Ada" }));
        var url = $"/customers/{customer.Id}/addresses";

        var created = await ReadAsync<BatchResult<AddressResponse>>(await SendAsync(HttpMethod.Post, $"{url}/batch-create", new
        {
            items = new object[] { new { line = "1 Main St", city = "Ankara" }, new { line = "2 Side St", city = "Izmir" } },
        }));

        Assert.All(created.Items, address => Assert.Equal(customer.Id, address.CustomerId));

        // Başka bir kiracı ne bu müşterinin altına yazabilir ne de adreslerini toplu okuyabilir.
        var intruder = Guid.NewGuid();
        var planted = await SendAsync(
            HttpMethod.Post, $"{url}/batch-create", new { items = new object[] { new { line = "X", city = "Y" } } }, tenant: intruder);
        var read = await ReadAsync<BatchGetResult<AddressResponse, Guid>>(await SendAsync(
            HttpMethod.Post, $"{url}/batch-get", new { ids = created.Items.Select(address => address.Id) }, tenant: intruder));
        var removed = await SendAsync(
            HttpMethod.Post, $"{url}/batch-delete", new { ids = created.Items.Select(address => address.Id) }, tenant: intruder);

        Assert.Equal(HttpStatusCode.NotFound, planted.StatusCode);
        Assert.Empty(read.Items);
        Assert.Equal(2, read.NotFound.Count);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);

        var mine = await ReadAsync<PagedResult<AddressResponse>>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal(2, mine.Items.Count);
    }

    private async Task<List<ProductResponse>> CreateProductsAsync(params string[] names)
    {
        var result = await ReadAsync<BatchResult<ProductResponse>>(await SendAsync(HttpMethod.Post, "/products/batch-create", new
        {
            items = names.Select(name => new { name = $"{_prefix}-{name}", price = 10 }),
        }));

        return [.. result.Items];
    }

    private async Task<int> CountProductsAsync()
    {
        var count = -1;
        await fixture.WithDbContextAsync(async db =>
            count = await db.Products.CountAsync(product => product.Name.StartsWith(_prefix), CancellationToken));

        return count;
    }

    private async Task<int> CountTagsAsync()
    {
        var count = -1;
        await fixture.WithDbContextAsync(async db =>
            count = await db.Tags.CountAsync(tag => tag.Name.StartsWith(_prefix), CancellationToken));

        return count;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, Guid? tenant = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, (tenant ?? _tenant).ToString());

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken)}");

        return (await response.Content.ReadFromJsonAsync<T>(CancellationToken))!;
    }
}
