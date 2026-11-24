using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed class FilterAndSearchTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    // Ürünler tüm testlerce paylaşılır; her test kendi kayıtlarını bu benzersiz önekle ayırır.
    private readonly string _token = $"t{Guid.NewGuid():N}";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Filter_BindsCamelCaseQueryParametersAndAppliesThem()
    {
        await CreateProductsAsync(("Keyboard", 100m, null), ("Mouse", 40m, null), ("Monitor", 900m, null));

        var page = await _client.GetFromJsonAsync<PagedResult<ProductResponse>>(
            $"/products?search={_token}&minPrice=50&maxPrice=500", CancellationToken);

        Assert.Equal([$"{_token} Keyboard"], page!.Items.Select(item => item.Name));
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Filter_SearchIsAppliedAsWrittenInTheFilterClass()
    {
        await CreateProductsAsync(("Keyboard", 100m, null));

        // ProductFilter büyük/küçük harf ayrımı yapmadan arar.
        var page = await _client.GetFromJsonAsync<PagedResult<ProductResponse>>(
            $"/products?search={_token.ToUpperInvariant()}%20KEYBOARD", CancellationToken);

        Assert.Single(page!.Items);
    }

    [Fact]
    public async Task Filter_OmittedParametersKeepTheFilterDefaults()
    {
        await CreateProductsAsync(("Described", 10m, "has text"), ("Bare", 10m, null));

        var all = await _client.GetFromJsonAsync<PagedResult<ProductResponse>>(
            $"/products?search={_token}", CancellationToken);
        var described = await _client.GetFromJsonAsync<PagedResult<ProductResponse>>(
            $"/products?search={_token}&onlyDescribed=true", CancellationToken);

        Assert.Equal(2, all!.TotalCount);
        Assert.Equal([$"{_token} Described"], described!.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task Search_CombinesFieldConditionsWithAnd()
    {
        await CreateProductsAsync(("Keyboard", 100m, null), ("Mouse", 40m, null), ("Monitor", 900m, null));

        var page = await SearchProductsAsync(new
        {
            where = new { name = new { startsWith = _token }, price = new { gte = 50, lt = 900 } },
        });

        Assert.Equal([$"{_token} Keyboard"], page.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task Search_SupportsOrAndNotNesting()
    {
        await CreateProductsAsync(("Keyboard", 100m, null), ("Mouse", 40m, null), ("Monitor", 900m, null));

        var page = await SearchProductsAsync(new
        {
            where = new
            {
                name = new { startsWith = _token },
                or = new object[]
                {
                    new { price = new { lt = 50 } },
                    new { price = new { gt = 500 } },
                },
                not = new { name = new { endsWith = "Monitor" } },
            },
        });

        Assert.Equal([$"{_token} Mouse"], page.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task Search_StringOperatorsHonourIgnoreCase()
    {
        await CreateProductsAsync(("Keyboard", 100m, null));

        var caseSensitive = await SearchProductsAsync(new
        {
            where = new { name = new { contains = $"{_token} KEYBOARD" } },
        });
        var caseInsensitive = await SearchProductsAsync(new
        {
            where = new { name = new { contains = $"{_token} KEYBOARD", ignoreCase = true } },
        });

        Assert.Empty(caseSensitive.Items);
        Assert.Single(caseInsensitive.Items);
    }

    [Fact]
    public async Task Search_SupportsInListsAndNullChecks()
    {
        await CreateProductsAsync(("A", 10m, "text"), ("B", 20m, null), ("C", 30m, null));

        var byList = await SearchProductsAsync(new
        {
            where = new { name = new { @in = new[] { $"{_token} A", $"{_token} C", "missing" } } },
        });
        var withoutDescription = await SearchProductsAsync(new
        {
            where = new { name = new { startsWith = _token }, description = new { isNull = true } },
        });
        var emptyList = await SearchProductsAsync(new
        {
            where = new { name = new { startsWith = _token }, price = new { @in = Array.Empty<decimal>() } },
        });

        Assert.Equal(2, byList.TotalCount);
        Assert.Equal(2, withoutDescription.TotalCount);
        Assert.Empty(emptyList.Items);
    }

    [Fact]
    public async Task Search_SortsByRequestedFieldsAndPages()
    {
        await CreateProductsAsync(("B", 20m, null), ("A", 20m, null), ("C", 5m, null));

        var page = await SearchProductsAsync(new
        {
            where = new { name = new { startsWith = _token } },
            sort = new object[]
            {
                new { field = "price", descending = true },
                new { field = "name" },
            },
            page = 1,
            pageSize = 2,
        });

        Assert.Equal([$"{_token} A", $"{_token} B"], page.Items.Select(item => item.Name));
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.PageSize);
    }

    [Fact]
    public async Task Search_TreatsWildcardsAndQuotesAsLiteralText()
    {
        await CreateProductsAsync(("100% cotton", 10m, null), ("100x cotton", 10m, null));

        var wildcard = await SearchProductsAsync(new
        {
            where = new { name = new { contains = $"{_token} 100%" } },
        });
        var injection = await SearchProductsAsync(new
        {
            where = new { name = new { eq = "'; DROP TABLE \"Products\"; --" } },
        });
        var stillThere = await SearchProductsAsync(new { where = new { name = new { startsWith = _token } } });

        Assert.Equal([$"{_token} 100% cotton"], wildcard.Items.Select(item => item.Name));
        Assert.Empty(injection.Items);
        Assert.Equal(2, stillThere.TotalCount);
    }

    [Theory]
    [InlineData("""{ "where": { "secret": { "eq": "x" } } }""")]
    [InlineData("""{ "where": { "name": { "regex": "x" } } }""")]
    [InlineData("""{ "sort": [{ "field": "secret" }] }""")]
    [InlineData("""{ "sort": [{ "descending": true }] }""")]
    [InlineData("""{ "unknown": 1 }""")]
    public async Task Search_RejectsUnknownFieldsAndOperators(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/products/search", content, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_RejectsNestingBeyondTheBudget()
    {
        object where = new { name = new { eq = "x" } };
        for (var depth = 0; depth < SearchBudget.MaxDepth; depth++)
        {
            where = new { not = where };
        }

        var response = await _client.PostAsJsonAsync("/products/search", new { where }, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken);
        Assert.Equal("Search is too complex", problem?.Title);
    }

    [Fact]
    public async Task Search_AllowsNestingUpToTheBudget()
    {
        object where = new { name = new { eq = "x" } };
        for (var depth = 0; depth < SearchBudget.MaxDepth - 1; depth++)
        {
            where = new { not = where };
        }

        var response = await _client.PostAsJsonAsync("/products/search", new { where }, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Search_RejectsOversizedListsConditionCountsAndSorts()
    {
        var tooManyValues = await _client.PostAsJsonAsync(
            "/products/search",
            new { where = new { price = new { @in = Enumerable.Range(0, SearchBudget.MaxListValues + 1) } } },
            CancellationToken);
        var tooManyConditions = await _client.PostAsJsonAsync(
            "/products/search",
            new
            {
                where = new
                {
                    or = Enumerable.Range(0, SearchBudget.MaxConditions + 1).Select(value => new { price = new { eq = value } }),
                },
            },
            CancellationToken);
        var tooManySorts = await _client.PostAsJsonAsync(
            "/products/search",
            new { sort = Enumerable.Repeat(new { field = "price" }, SearchBudget.MaxSortFields + 1) },
            CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, tooManyValues.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyConditions.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooManySorts.StatusCode);
    }

    [Fact]
    public async Task Search_StaysInsideTheTenantAndHonoursSoftDelete()
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var kept = await CreateCustomerAsync(tenant, "Kept", loyaltyPoints: 5);
        var deleted = await CreateCustomerAsync(tenant, "Deleted", loyaltyPoints: 5);
        await CreateCustomerAsync(otherTenant, "Foreign", loyaltyPoints: 5);
        await SendAsync(HttpMethod.Delete, $"/customers/{deleted.Id}", tenant, ifMatch: "*");

        var body = new { where = new { loyaltyPoints = new { eq = 5 } } };
        var visible = await SearchCustomersAsync(tenant, body);
        var withDeleted = await SearchCustomersAsync(
            tenant, new { where = new { loyaltyPoints = new { eq = 5 } }, includeDeleted = true });

        Assert.Equal([kept.Id], visible.Items.Select(item => item.Id));
        Assert.Equal(2, withDeleted.TotalCount);
    }

    [Fact]
    public async Task Search_WorksOnIdsTraitFieldsAndNullableRanges()
    {
        var tenant = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var first = await CreateCustomerAsync(tenant, "First");
        var second = await CreateCustomerAsync(tenant, "Second");
        await SendAsync(
            HttpMethod.Put, $"/customers/{second.Id}", tenant,
            body: new UpdateCustomerRequest { Name = "Second, edited" }, ifMatch: "*");

        var byId = await SearchCustomersAsync(tenant, new { where = new { id = new { @in = new[] { first.Id } } } });
        var createdRecently = await SearchCustomersAsync(tenant, new { where = new { createdAt = new { gte = before } } });
        var neverUpdated = await SearchCustomersAsync(tenant, new { where = new { updatedAt = new { isNull = true } } });
        var updatedRecently = await SearchCustomersAsync(tenant, new { where = new { updatedAt = new { gte = before } } });

        Assert.Equal([first.Id], byId.Items.Select(item => item.Id));
        Assert.Equal(2, createdRecently.TotalCount);
        Assert.Equal([first.Id], neverUpdated.Items.Select(item => item.Id));
        Assert.Equal([second.Id], updatedRecently.Items.Select(item => item.Id));
    }

    private async Task CreateProductsAsync(params (string Name, decimal Price, string? Description)[] products)
    {
        foreach (var (name, price, description) in products)
        {
            var response = await _client.PostAsJsonAsync(
                "/products",
                new CreateProductRequest { Name = $"{_token} {name}", Price = price, Description = description },
                CancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<PagedResult<ProductResponse>> SearchProductsAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/products/search", body, CancellationToken);
        return await ReadAsync<PagedResult<ProductResponse>>(response);
    }

    private async Task<PagedResult<CustomerResponse>> SearchCustomersAsync(Guid tenant, object body) =>
        await ReadAsync<PagedResult<CustomerResponse>>(await SendAsync(HttpMethod.Post, "/customers/search", tenant, body: body));

    private async Task<CustomerResponse> CreateCustomerAsync(Guid tenant, string name, int loyaltyPoints = 0) =>
        await ReadAsync<CustomerResponse>(await SendAsync(
            HttpMethod.Post, "/customers", tenant,
            body: new CreateCustomerRequest { Name = name, LoyaltyPoints = loyaltyPoints }));

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
