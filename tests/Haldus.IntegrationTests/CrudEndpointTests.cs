using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Haldus.Sample.Shop.Features.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed class CrudEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_ReturnsCreatedWithGeneratedGuidAndLocation()
    {
        var response = await _client.PostAsJsonAsync(
            "/products", new CreateProductRequest { Name = "Keyboard", Price = 1250.50m }, CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(CancellationToken);
        Assert.NotNull(product);
        Assert.NotEqual(Guid.Empty, product.Id);
        Assert.Equal("Keyboard", product.Name);
        Assert.Equal(1250.50m, product.Price);
        Assert.Null(product.Description);
        Assert.Equal($"/products/{product.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Get_ReturnsThePersistedEntity()
    {
        var created = await CreateProductAsync("Mouse", 300m);

        var product = await _client.GetFromJsonAsync<ProductResponse>($"/products/{created.Id}", CancellationToken);

        // Yanıt bir metadata çantası da taşıyor; record eşitliği onu içeriğiyle değil başvurusuyla karşılaştırır.
        Assert.Equivalent(created, product, strict: true);
    }

    [Fact]
    public async Task Update_ReplacesWritableFields()
    {
        var created = await CreateProductAsync("Monitor", 5000m);

        var response = await _client.PutAsJsonAsync(
            $"/products/{created.Id}",
            new UpdateProductRequest { Name = "Monitor 27", Price = 5500m, Description = "QHD" },
            CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reloaded = await _client.GetFromJsonAsync<ProductResponse>($"/products/{created.Id}", CancellationToken);
        Assert.NotNull(reloaded);
        Assert.Equal("Monitor 27", reloaded.Name);
        Assert.Equal(5500m, reloaded.Price);
        Assert.Equal("QHD", reloaded.Description);
    }

    [Fact]
    public async Task Delete_RemovesTheEntity()
    {
        var created = await CreateProductAsync("Cable", 50m);

        var deleted = await _client.DeleteAsync($"/products/{created.Id}", CancellationToken);
        var reloaded = await _client.GetAsync($"/products/{created.Id}", CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reloaded.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsRequestedPageAndTotalCount()
    {
        for (var index = 0; index < 3; index++)
        {
            await CreateProductAsync($"Listed {index}", 10m);
        }

        var page = await _client.GetFromJsonAsync<PagedResult<ProductResponse>>(
            "/products?page=1&pageSize=2", CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(1, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.True(page.TotalCount >= 3);
    }

    [Theory]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("page=2147483647&pageSize=2147483647")]
    [InlineData("page=-5&pageSize=-5")]
    public async Task List_WithOutOfRangePaging_ClampsInsteadOfFailing(string query)
    {
        var response = await _client.GetAsync($"/products?{query}", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PagedResult<ProductResponse>>(CancellationToken);
        Assert.NotNull(page);
        Assert.InRange(page.Page, 1, PageRequest.MaxPage);
        Assert.InRange(page.PageSize, 1, PageRequest.MaxPageSize);
    }

    [Fact]
    public async Task Create_WithDeclaredIntId_UsesDatabaseGeneratedKey()
    {
        var response = await _client.PostAsJsonAsync(
            "/categories", new CreateCategoryRequest { Name = "Peripherals" }, CancellationToken);

        var category = await response.Content.ReadFromJsonAsync<CategoryResponse>(CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(category);
        Assert.True(category.Id > 0);
    }

    [Fact]
    public async Task Create_WithInvalidBody_ReturnsValidationProblemInEnglishByDefault()
    {
        var response = await _client.PostAsJsonAsync(
            "/products", new { name = new string('x', 201), price = -1 }, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("One or more validation errors occurred.", problem.Title);
        Assert.Equal(["The Name field must be at most 200 characters long."], problem.Errors["name"]);
        Assert.Equal(["The Price field must be between 0 and 1000000."], problem.Errors["price"]);
    }

    [Fact]
    public async Task Create_WithMissingRequiredField_ReturnsLocalizedValidationProblem()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/products")
        {
            Content = JsonContent.Create(new { price = 10 }),
        };
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("tr"));

        var response = await _client.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("Bir veya daha fazla doğrulama hatası oluştu.", problem.Title);
        Assert.Equal(["Name alanı zorunludur."], problem.Errors["name"]);
    }

    [Fact]
    public async Task Create_WithStringLengthRule_ReportsBothBounds()
    {
        var response = await _client.PostAsJsonAsync("/categories", new { name = "x" }, CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(problem);
        Assert.Equal(["The Name field must be between 2 and 100 characters long."], problem.Errors["name"]);
    }

    [Fact]
    public async Task Get_WithUnknownId_ReturnsLocalizedNotFoundProblem()
    {
        var id = Guid.CreateVersion7();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/products/{id}");
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("tr"));

        var response = await _client.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("Kayıt bulunamadı", problem.Title);
        Assert.Equal($"'{id}' kimlikli Product kaydı bulunamadı.", problem.Detail);
    }

    [Fact]
    public async Task Update_WithUnknownId_ReturnsNotFound()
    {
        var response = await _client.PutAsJsonAsync(
            $"/products/{Guid.CreateVersion7()}",
            new UpdateProductRequest { Name = "Ghost", Price = 1m },
            CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<ProductResponse> CreateProductAsync(string name, decimal price)
    {
        var response = await _client.PostAsJsonAsync(
            "/products", new CreateProductRequest { Name = name, Price = price }, CancellationToken);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ProductResponse>(CancellationToken))!;
    }
}
