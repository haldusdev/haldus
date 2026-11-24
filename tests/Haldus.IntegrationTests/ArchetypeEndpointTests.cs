using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Platform;
using Haldus.Sample.Shop.Features.Stock;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed class ArchetypeEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AppendOnly_CanBeCreatedAndReadButNotChanged()
    {
        var created = await ReadAsync<StockMovementResponse>(await _client.PostAsJsonAsync(
            "/stock-movements",
            new CreateStockMovementRequest { ProductId = Guid.NewGuid(), Quantity = 5, Reason = "Initial" },
            CancellationToken));

        var read = await _client.GetAsync($"/stock-movements/{created.Id}", CancellationToken);
        var put = await _client.PutAsJsonAsync($"/stock-movements/{created.Id}", new { quantity = 9 }, CancellationToken);
        var patch = await _client.PatchAsJsonAsync($"/stock-movements/{created.Id}", new { quantity = 9 }, CancellationToken);
        var delete = await _client.DeleteAsync($"/stock-movements/{created.Id}", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, patch.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.NotEqual(default, created.CreatedAt);
    }

    [Fact]
    public async Task ReadOnly_ExposesSystemWrittenRecordsAndRejectsClientWrites()
    {
        var level = new StockLevel { Id = Guid.CreateVersion7(), ProductId = Guid.NewGuid(), Quantity = 42 };
        await fixture.WithDbContextAsync(async db =>
        {
            db.StockLevels.Add(level);
            await db.SaveChangesAsync(CancellationToken);
        });

        var read = await _client.GetFromJsonAsync<StockLevelResponse>($"/stock-levels/{level.Id}", CancellationToken);
        var list = await _client.GetAsync("/stock-levels", CancellationToken);
        var post = await _client.PostAsJsonAsync("/stock-levels", new { quantity = 1 }, CancellationToken);
        var delete = await _client.DeleteAsync($"/stock-levels/{level.Id}", CancellationToken);

        Assert.Equal(42, read?.Quantity);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    }

    [Fact]
    public async Task Store_PutCreatesThenReplacesByClientKey()
    {
        var key = $"flag-{Guid.NewGuid():N}";

        var created = await _client.PutAsJsonAsync(
            $"/feature-flags/{key}", new PutFeatureFlagRequest { Enabled = true, Description = "first" }, CancellationToken);
        var replaced = await _client.PutAsJsonAsync(
            $"/feature-flags/{key}", new PutFeatureFlagRequest { Enabled = false }, CancellationToken);
        var read = await _client.GetFromJsonAsync<FeatureFlagResponse>($"/feature-flags/{key}", CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/feature-flags/{key}", created.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(new FeatureFlagResponse { Key = key, Enabled = false, Description = null }, read);
    }

    [Fact]
    public async Task Store_SupportsListAndDeleteButNotPost()
    {
        var key = $"flag-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/feature-flags/{key}", new PutFeatureFlagRequest { Enabled = true }, CancellationToken);

        var list = await _client.GetFromJsonAsync<PagedResult<FeatureFlagResponse>>("/feature-flags?pageSize=100", CancellationToken);
        var post = await _client.PostAsJsonAsync("/feature-flags", new { enabled = true }, CancellationToken);
        var deleted = await _client.DeleteAsync($"/feature-flags/{key}", CancellationToken);
        var gone = await _client.GetAsync($"/feature-flags/{key}", CancellationToken);

        Assert.Contains(list!.Items, item => item.Key == key);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Store_ConcurrentPutsOfANewKeyAllSucceed()
    {
        var key = $"flag-{Guid.NewGuid():N}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => _client.PutAsJsonAsync(
            $"/feature-flags/{key}", new PutFeatureFlagRequest { Enabled = index % 2 == 0 }, CancellationToken)));

        Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}"));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
    }

    [Fact]
    public async Task Store_ValidatesTheBody()
    {
        var response = await _client.PutAsJsonAsync(
            $"/feature-flags/flag-{Guid.NewGuid():N}", new { description = new string('x', 501) }, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Store_WithMultiTenant_KeepsTheSameKeySeparatePerTenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var first = await SendAsync(HttpMethod.Put, "/preferences/theme", tenantA, new PutPreferenceRequest { Value = "dark" });
        var second = await SendAsync(HttpMethod.Put, "/preferences/theme", tenantB, new PutPreferenceRequest { Value = "light" });
        var readA = await ReadAsync<PreferenceResponse>(await SendAsync(HttpMethod.Get, "/preferences/theme", tenantA));
        var readB = await ReadAsync<PreferenceResponse>(await SendAsync(HttpMethod.Get, "/preferences/theme", tenantB));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("dark", readA.Value);
        Assert.Equal("light", readB.Value);
    }

    [Fact]
    public async Task Lookup_ReturnsTheWholeListOrderedByCode()
    {
        var prefix = $"Z{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await _client.PutAsJsonAsync($"/countries/{prefix}B", new PutCountryRequest { Name = "Second" }, CancellationToken);
        await _client.PutAsJsonAsync($"/countries/{prefix}A", new PutCountryRequest { Name = "First" }, CancellationToken);

        var list = await _client.GetFromJsonAsync<List<CountryResponse>>("/countries", CancellationToken);
        var mine = list!.Where(country => country.Code.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        Assert.Equal([$"{prefix}A", $"{prefix}B"], mine.Select(country => country.Code));
        Assert.Equal(["First", "Second"], mine.Select(country => country.Name));
    }

    [Fact]
    public async Task Lookup_SupportsReadUpsertAndDeleteByCode()
    {
        var code = $"C{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        var created = await _client.PutAsJsonAsync($"/countries/{code}", new PutCountryRequest { Name = "Created" }, CancellationToken);
        var renamed = await _client.PutAsJsonAsync($"/countries/{code}", new PutCountryRequest { Name = "Renamed" }, CancellationToken);
        var read = await _client.GetFromJsonAsync<CountryResponse>($"/countries/{code}", CancellationToken);
        var deleted = await _client.DeleteAsync($"/countries/{code}", CancellationToken);
        var gone = await _client.GetAsync($"/countries/{code}", CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(new CountryResponse { Code = code, Name = "Renamed" }, read);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Lookup_RequiresAName(string name)
    {
        var response = await _client.PutAsJsonAsync($"/countries/X{Guid.NewGuid():N}", new { name }, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(["The Name field is required."], problem.Errors["name"]);
    }

    [Fact]
    public async Task Singleton_ReadsDefaultsBeforeTheFirstWriteAndRejectsCreateAndDelete()
    {
        var tenant = Guid.NewGuid();

        var defaults = await ReadAsync<StoreSettingsResponse>(await SendAsync(HttpMethod.Get, "/store-settings", tenant));
        var post = await SendAsync(HttpMethod.Post, "/store-settings", tenant, new { storeName = "x" });
        var delete = await SendAsync(HttpMethod.Delete, "/store-settings", tenant);

        Assert.Equal("", defaults.StoreName);
        Assert.Equal("TRY", defaults.Currency);
        Assert.Equal(20m, defaults.TaxRate);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    }

    [Fact]
    public async Task Singleton_WithMultiTenant_KeepsOneRecordPerTenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var written = await ReadAsync<StoreSettingsResponse>(await SendAsync(
            HttpMethod.Put, "/store-settings", tenantA,
            new UpdateStoreSettingsRequest { StoreName = "A Store", Currency = "EUR", TaxRate = 8m }));
        var rewritten = await ReadAsync<StoreSettingsResponse>(await SendAsync(
            HttpMethod.Put, "/store-settings", tenantA,
            new UpdateStoreSettingsRequest { StoreName = "A Store 2", Currency = "EUR", TaxRate = 8m }));
        var readA = await ReadAsync<StoreSettingsResponse>(await SendAsync(HttpMethod.Get, "/store-settings", tenantA));
        var readB = await ReadAsync<StoreSettingsResponse>(await SendAsync(HttpMethod.Get, "/store-settings", tenantB));

        Assert.Equal("A Store", written.StoreName);
        Assert.Null(written.UpdatedAt);
        Assert.NotNull(rewritten.UpdatedAt);
        Assert.Equal(written.CreatedAt, rewritten.CreatedAt);
        Assert.Equal("A Store 2", readA.StoreName);
        Assert.Equal("EUR", readA.Currency);
        Assert.Equal("", readB.StoreName);
        Assert.Equal("TRY", readB.Currency);
    }

    [Fact]
    public async Task Singleton_PatchOnAnUnwrittenRecordStartsFromTheDefaults()
    {
        var tenant = Guid.NewGuid();

        var patched = await ReadAsync<StoreSettingsResponse>(
            await SendAsync(HttpMethod.Patch, "/store-settings", tenant, new { storeName = "Patched" }));

        Assert.Equal("Patched", patched.StoreName);
        Assert.Equal("TRY", patched.Currency);
        Assert.Equal(20m, patched.TaxRate);
    }

    [Fact]
    public async Task Singleton_ValidatesWritesAndRequiresATenant()
    {
        var invalid = await SendAsync(
            HttpMethod.Put, "/store-settings", Guid.NewGuid(),
            new UpdateStoreSettingsRequest { StoreName = "S", Currency = "EURO", TaxRate = 8m });
        var withoutTenant = await _client.GetAsync("/store-settings", CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withoutTenant.StatusCode);
    }

    [Fact]
    public async Task Singleton_ConcurrentFirstWritesAllSucceedAndLeaveOneRecord()
    {
        var tenant = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => SendAsync(
            HttpMethod.Put, "/store-settings", tenant,
            new UpdateStoreSettingsRequest { StoreName = $"Writer {index}", Currency = "USD", TaxRate = 1m })));

        Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}"));

        var count = 0;
        await fixture.WithDbContextAsync(async db =>
        {
            // Sayım kiracı filtresi dışında yapılır: tabloda bu kiracıya ait tam bir satır olmalı.
            count = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.StoreSettings),
                settings => settings.TenantId == tenant,
                CancellationToken);
        });

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Singleton_WithoutTenant_IsOneRecordForTheWholeApplication()
    {
        var first = await ReadAsync<MaintenanceNoticeResponse>(await _client.PutAsJsonAsync(
            "/maintenance-notice", new UpdateMaintenanceNoticeRequest { Enabled = true, Message = "Tonight" }, CancellationToken));
        var second = await ReadAsync<MaintenanceNoticeResponse>(await _client.PatchAsJsonAsync(
            "/maintenance-notice", new { message = "Tomorrow" }, CancellationToken));
        var read = await _client.GetFromJsonAsync<MaintenanceNoticeResponse>("/maintenance-notice", CancellationToken);

        Assert.True(first.Enabled);
        Assert.Equal(new MaintenanceNoticeResponse { Enabled = true, Message = "Tomorrow" }, second);
        Assert.Equal(second, read);

        var count = 0;
        await fixture.WithDbContextAsync(async db =>
            count = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
                db.MaintenanceNotice, CancellationToken));

        Assert.Equal(1, count);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid tenant, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, tenant.ToString());

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
