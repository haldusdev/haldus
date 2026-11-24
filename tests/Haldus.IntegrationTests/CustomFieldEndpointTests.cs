using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Supply;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

/// <summary>Kiracının tanımladığı özel alanlar (<c>CustomFields</c>).</summary>
[Collection(ShopCollection.Name)]
public sealed class CustomFieldEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Definitions_AreManagedPerTenant()
    {
        var color = await ReadAsync<CustomFieldDefinition>(await SendAsync(
            HttpMethod.Put, "/machines/custom-fields/color", new { label = "Color", type = "Choice", required = true, options = new[] { "red", "blue" } }));
        Assert.Equal(("color", "Color", CustomFieldType.Choice, true), (color.Key, color.Label, color.Type, color.Required));
        Assert.Equal(["red", "blue"], color.Options);

        await ReadAsync<CustomFieldDefinition>(await SendAsync(HttpMethod.Put, "/machines/custom-fields/weight", new { label = "Weight", type = "Number" }));

        // Aynı anahtara yeniden yazmak tanımı değiştirir.
        var relabeled = await ReadAsync<CustomFieldDefinition>(await SendAsync(HttpMethod.Put, "/machines/custom-fields/weight", new { label = "Weight (kg)", type = "Number" }));
        Assert.Equal("Weight (kg)", relabeled.Label);

        var mine = await ReadAsync<List<CustomFieldDefinition>>(await SendAsync(HttpMethod.Get, "/machines/custom-fields"));
        Assert.Equal(["color", "weight"], mine.Select(definition => definition.Key));

        // Tanımlar kiracıya aittir.
        var theirs = await ReadAsync<List<CustomFieldDefinition>>(await SendAsync(HttpMethod.Get, "/machines/custom-fields", tenant: _otherTenant));
        Assert.Empty(theirs);

        // Geçersiz tanımlar reddedilir.
        var badKey = await SendAsync(HttpMethod.Put, "/machines/custom-fields/1st", new { label = "First", type = "Text" });
        var noOptions = await SendAsync(HttpMethod.Put, "/machines/custom-fields/size", new { label = "Size", type = "Choice" });
        var strayOptions = await SendAsync(HttpMethod.Put, "/machines/custom-fields/size", new { label = "Size", type = "Text", options = new[] { "s" } });
        var noLabel = await SendAsync(HttpMethod.Put, "/machines/custom-fields/size", new { type = "Text" });

        Assert.Equal(["key"], await ErrorsAsync(badKey));
        Assert.Equal(["options"], await ErrorsAsync(noOptions));
        Assert.Equal(["options"], await ErrorsAsync(strayOptions));
        Assert.Equal(HttpStatusCode.BadRequest, noLabel.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/machines/custom-fields/weight")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/machines/custom-fields/weight")).StatusCode);
    }

    [Fact]
    public async Task Values_AreValidatedAgainstTheDefinitions()
    {
        await DefineAsync("color", new { label = "Color", type = "Choice", required = true, options = new[] { "red", "blue" } });
        await DefineAsync("weight", new { label = "Weight", type = "Number" });
        await DefineAsync("installedOn", new { label = "Installed on", type = "Date" });
        await DefineAsync("fragile", new { label = "Fragile", type = "Boolean" });
        await DefineAsync("note", new { label = "Note", type = "Text" });

        // Zorunlu alan eksik, tanımsız anahtar ve türüne uymayan değerler: hepsi alan düzeyinde bildirilir.
        var missing = await SendAsync(HttpMethod.Post, "/machines", new { name = "Drill" });
        var invalid = await SendAsync(HttpMethod.Post, "/machines", new
        {
            name = "Drill",
            customFields = new Dictionary<string, object?>
            {
                ["color"] = "green",
                ["weight"] = "heavy",
                ["installedOn"] = "01.06.2026",
                ["fragile"] = "yes",
                ["note"] = 12,
                ["serial"] = "X-1",
            },
        });

        Assert.Equal(["customFields.color"], await ErrorsAsync(missing));
        Assert.Equal(
            new[] { "customFields.color", "customFields.fragile", "customFields.installedOn", "customFields.note", "customFields.serial", "customFields.weight" },
            (await ErrorsAsync(invalid)).Order());

        var created = await ReadAsync<MachineResponse>(await SendAsync(HttpMethod.Post, "/machines", new
        {
            name = "Drill",
            customFields = new Dictionary<string, object?> { ["color"] = "red", ["weight"] = 2.5, ["installedOn"] = "2026-06-01", ["fragile"] = false, ["note"] = "Keep dry" },
        }));

        Assert.Equal("red", created.CustomFields["color"].GetString());
        Assert.Equal(2.5m, created.CustomFields["weight"].GetDecimal());
        Assert.False(created.CustomFields["fragile"].GetBoolean());
        var url = $"/machines/{created.Id}";

        // PATCH birleştirir: gönderilen değişir, null gönderilen silinir, gönderilmeyen kalır.
        var patched = await ReadAsync<MachineResponse>(await SendAsync(HttpMethod.Patch, url, new
        {
            customFields = new Dictionary<string, object?> { ["weight"] = 3, ["note"] = null },
        }));
        Assert.Equal(new[] { "color", "fragile", "installedOn", "weight" }, patched.CustomFields.Keys.Order());
        Assert.Equal(3, patched.CustomFields["weight"].GetInt32());

        // Zorunlu alan silinemez; çantaya dokunmayan PATCH onu doğrulamaz da.
        Assert.Equal(["customFields.color"], await ErrorsAsync(await SendAsync(HttpMethod.Patch, url, new { customFields = new Dictionary<string, object?> { ["color"] = null } })));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Patch, url, new { name = "Hammer drill" })).StatusCode);

        // PUT çantayı tamamen değiştirir.
        var replaced = await ReadAsync<MachineResponse>(await SendAsync(HttpMethod.Put, url, new
        {
            name = "Hammer drill",
            customFields = new Dictionary<string, object?> { ["color"] = "blue" },
        }));
        Assert.Equal(["color"], replaced.CustomFields.Keys);
        Assert.Equal(["customFields.color"], await ErrorsAsync(await SendAsync(HttpMethod.Put, url, new { name = "Hammer drill" })));

        // Tanımı kaldırılan alanın değeri kaydın bir sonraki yazmasında düşer; yeniden yazılamaz.
        await SendAsync(HttpMethod.Patch, url, new { customFields = new Dictionary<string, object?> { ["note"] = "Temporary" } });
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/machines/custom-fields/note")).StatusCode);

        var afterRemoval = await ReadAsync<MachineResponse>(await SendAsync(HttpMethod.Patch, url, new { customFields = new Dictionary<string, object?> { ["weight"] = 4 } }));
        Assert.Equal(new[] { "color", "weight" }, afterRemoval.CustomFields.Keys.Order());
        Assert.Equal(["customFields.note"], await ErrorsAsync(await SendAsync(HttpMethod.Patch, url, new { customFields = new Dictionary<string, object?> { ["note"] = "Again" } })));

        // Başka bir kiracının tanımları başkadır: burada tanımlı alan orada tanımsızdır, zorunlu da değildir.
        var elsewhere = await SendAsync(HttpMethod.Post, "/machines", new { name = "Saw", customFields = new Dictionary<string, object?> { ["color"] = "red" } }, tenant: _otherTenant);
        Assert.Equal(["customFields.color"], await ErrorsAsync(elsewhere));

        var plain = await ReadAsync<MachineResponse>(await SendAsync(HttpMethod.Post, "/machines", new { name = "Saw" }, tenant: _otherTenant));
        Assert.Empty(plain.CustomFields);

        var turkish = await SendAsync(HttpMethod.Post, "/machines", new { name = "Drill", customFields = new Dictionary<string, object?> { ["color"] = 5 } }, language: "tr");
        var problem = await turkish.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.Equal("Değer geçerli bir choice değil.", Assert.Single(problem!.Errors["customFields.color"]));
    }

    private async Task DefineAsync(string key, object definition) =>
        await ReadAsync<CustomFieldDefinition>(await SendAsync(HttpMethod.Put, $"/machines/custom-fields/{key}", definition));

    private static async Task<IEnumerable<string>> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!.Errors.Keys;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, Guid? tenant = null, string? language = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, (tenant ?? _tenant).ToString());

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