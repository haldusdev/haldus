using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop.Features.Engagement;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

/// <summary>Tip–örnek (<c>TypedBy</c>) ve rol (<c>RoleOf</c>) ilişkileri.</summary>
[Collection(ShopCollection.Name)]
public sealed class ModelingEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ── Tip–örnek ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TypedBy_FillsBlankFieldsFromTheType()
    {
        var model = await ReadAsync<DeviceModelResponse>(await SendAsync(HttpMethod.Post, "/device-models", new { name = "X200", warrantyMonths = 24, manual = "x200.pdf" }));

        // Boş bırakılan alanlar modelden gelir; yazılan alan olduğu gibi kalır.
        var plain = await ReadAsync<DeviceResponse>(await SendAsync(HttpMethod.Post, "/devices", new { serialNumber = "SN-1", deviceModelId = model.Id }));
        var custom = await ReadAsync<DeviceResponse>(await SendAsync(HttpMethod.Post, "/devices", new { serialNumber = "SN-2", deviceModelId = model.Id, warrantyMonths = 12 }));

        Assert.Equal((24, "x200.pdf", model.Id), (plain.WarrantyMonths, plain.Manual, plain.DeviceModelId));
        Assert.Equal((12, "x200.pdf"), (custom.WarrantyMonths, custom.Manual));

        // Alanı boşaltmak onu yeniden modele bağlar.
        var reset = await ReadAsync<DeviceResponse>(await SendAsync(HttpMethod.Patch, $"/devices/{custom.Id}", new { warrantyMonths = (int?)null }));
        Assert.Equal(24, reset.WarrantyMonths);

        // Değer yazılırken devralınır: modelin varsayılanı sonradan değişse de var olan cihazlar kendi değerini korur.
        await ReadAsync<DeviceModelResponse>(await SendAsync(HttpMethod.Patch, $"/device-models/{model.Id}", new { warrantyMonths = 36 }));
        Assert.Equal(24, (await ReadAsync<DeviceResponse>(await SendAsync(HttpMethod.Get, $"/devices/{plain.Id}"))).WarrantyMonths);

        var later = await ReadAsync<DeviceResponse>(await SendAsync(HttpMethod.Post, "/devices", new { serialNumber = "SN-3", deviceModelId = model.Id }));
        Assert.Equal(36, later.WarrantyMonths);
    }

    [Fact]
    public async Task TypedBy_FixesTheTypeOfARecord()
    {
        var model = await ReadAsync<DeviceModelResponse>(await SendAsync(HttpMethod.Post, "/device-models", new { name = "A1", warrantyMonths = 6 }));
        var other = await ReadAsync<DeviceModelResponse>(await SendAsync(HttpMethod.Post, "/device-models", new { name = "B2", warrantyMonths = 60 }));

        // Tip zorunludur ve var olmalıdır.
        Assert.Equal(["deviceModelId"], await ErrorsAsync(await SendAsync(HttpMethod.Post, "/devices", new { serialNumber = "SN-9", deviceModelId = Guid.NewGuid() })));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/devices", new { serialNumber = "SN-9" })).StatusCode);

        var device = await ReadAsync<DeviceResponse>(await SendAsync(HttpMethod.Post, "/devices", new { serialNumber = "SN-9", deviceModelId = model.Id }));

        // Tip sonradan değişmez: güncelleme isteğinde yer almaz, gönderilse de yok sayılır.
        var updated = await ReadAsync<DeviceResponse>(await SendAsync(HttpMethod.Put, $"/devices/{device.Id}", new { serialNumber = "SN-9b", deviceModelId = other.Id }));
        Assert.Equal((model.Id, "SN-9b", 6), (updated.DeviceModelId, updated.SerialNumber, updated.WarrantyMonths));

        // Kayıtları olan tip silinemez.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Delete, $"/device-models/{model.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/device-models/{other.Id}")).StatusCode);
    }

    // ── Rol ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoleOf_GivesAPlayerAtMostOneRoleRecord()
    {
        var person = await ReadAsync<PersonResponse>(await SendAsync(HttpMethod.Post, "/persons", new { fullName = "Ada Lovelace" }));
        var stranger = await ReadAsync<PersonResponse>(await SendAsync(HttpMethod.Post, "/persons", new { fullName = "Nobody" }));

        // Kişi henüz çalışan değil.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/staff-members/by-person-id/{person.Id}")).StatusCode);

        var staff = await ReadAsync<StaffMemberResponse>(await SendAsync(HttpMethod.Post, "/staff-members", new { personId = person.Id, department = "R&D" }));
        Assert.Equal(person.Id, staff.PersonId);
        Assert.StartsWith("EMP-", staff.Number);

        // Rol, oyuncunun kimliğiyle de okunur.
        var byPerson = await ReadAsync<StaffMemberResponse>(await SendAsync(HttpMethod.Get, $"/staff-members/by-person-id/{person.Id}"));
        Assert.Equal(staff.Id, byPerson.Id);

        // Bir kişi aynı rolü ikinci kez oynayamaz.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/staff-members", new { personId = person.Id })).StatusCode);

        // Oyuncu var olmalıdır ve sonradan değişmez.
        Assert.Equal(["personId"], await ErrorsAsync(await SendAsync(HttpMethod.Post, "/staff-members", new { personId = Guid.NewGuid() })));
        var moved = await ReadAsync<StaffMemberResponse>(await SendAsync(HttpMethod.Put, $"/staff-members/{staff.Id}", new { department = "Ops", personId = stranger.Id }));
        Assert.Equal((person.Id, "Ops"), (moved.PersonId, moved.Department));

        // Oyuncu silinince rolü de silinir.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/persons/{person.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/staff-members/{staff.Id}")).StatusCode);
    }

    // ── Genelleme ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubtypeOf_SharesTheTableKeyAndTraitsOfTheBase()
    {
        var plate = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var car = await ReadAsync<CarResponse>(await SendAsync(HttpMethod.Post, "/cars", new { plate = $"C-{plate}", seats = 5 }));
        var truck = await ReadAsync<TruckResponse>(await SendAsync(HttpMethod.Post, "/trucks", new { plate = $"T-{plate}", payloadTons = 12.5m }));
        var plain = await ReadAsync<VehicleResponse>(await SendAsync(HttpMethod.Post, "/vehicles", new { plate = $"V-{plate}" }));

        // Kaydın türü yanıtta görünür ve üst tipin trait'leri alt tipte de işler.
        Assert.Equal(("car", 5), (car.Kind, car.Seats));
        Assert.Equal(("truck", 12.5m), (truck.Kind, truck.PayloadTons));
        Assert.Equal("vehicle", plain.Kind);
        Assert.NotEqual(default, car.CreatedAt);

        // Alt tipin kaydı üst tipin adresinden de okunur: aynı kimlik, ortak alanlar.
        var asVehicle = await ReadAsync<VehicleResponse>(await SendAsync(HttpMethod.Get, $"/vehicles/{car.Id}"));
        Assert.Equal((car.Id, $"C-{plate}", "car"), (asVehicle.Id, asVehicle.Plate, asVehicle.Kind));

        // Alt tipin uçları yalnızca kendi türünü görür.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/cars/{truck.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/trucks/{plain.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Patch, $"/cars/{truck.Id}", new { seats = 2 })).StatusCode);

        var cars = await ReadAsync<PagedResult<CarResponse>>(await SendAsync(HttpMethod.Get, "/cars?pageSize=100"));
        Assert.Contains(cars.Items, item => item.Id == car.Id);
        Assert.All(cars.Items, item => Assert.Equal("car", item.Kind));

        // Üst tipin listesi bütün türleri döndürür ve türe göre daraltılır.
        var all = await ReadAsync<PagedResult<VehicleResponse>>(await SendAsync(HttpMethod.Get, "/vehicles?pageSize=100"));
        var onlyTrucks = await ReadAsync<PagedResult<VehicleResponse>>(await SendAsync(HttpMethod.Get, "/vehicles?kind=truck&pageSize=100"));
        Assert.Superset(new HashSet<Guid> { car.Id, truck.Id, plain.Id }, all.Items.Select(item => item.Id).ToHashSet());
        Assert.Contains(onlyTrucks.Items, item => item.Id == truck.Id);
        Assert.DoesNotContain(onlyTrucks.Items, item => item.Id == car.Id);

        // Ortak alan iki adresten de yazılır; kendi alanı yalnızca alt tipin adresinden.
        await ReadAsync<VehicleResponse>(await SendAsync(HttpMethod.Patch, $"/vehicles/{car.Id}", new { plate = $"C2-{plate}" }));
        var edited = await ReadAsync<CarResponse>(await SendAsync(HttpMethod.Patch, $"/cars/{car.Id}", new { seats = 7 }));
        Assert.Equal(($"C2-{plate}", 7, "car"), (edited.Plate, edited.Seats, edited.Kind));
        Assert.NotNull(edited.UpdatedAt);

        // Tür istekle yazılamaz ve değişmez.
        var forged = await ReadAsync<CarResponse>(await SendAsync(HttpMethod.Put, $"/cars/{car.Id}", new { plate = $"C3-{plate}", seats = 4, kind = "truck" }));
        Assert.Equal("car", forged.Kind);

        // Silme üst tipin kuralını izler: kayıt yumuşak silinir ve geri yüklenir.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/cars/{car.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/vehicles/{car.Id}")).StatusCode);
        var restored = await ReadAsync<CarResponse>(await SendAsync(HttpMethod.Post, $"/cars/{car.Id}/restore"));
        Assert.Equal((7 - 3, null), (restored.Seats, restored.DeletedAt));

        // Alt tipin kendi alanına göre sıralanır.
        await ReadAsync<TruckResponse>(await SendAsync(HttpMethod.Post, "/trucks", new { plate = $"T2-{plate}", payloadTons = 99.9m }));
        var heaviest = await ReadAsync<PagedResult<TruckResponse>>(await SendAsync(HttpMethod.Get, "/trucks?sort=-payloadTons&pageSize=1"));
        Assert.Equal(99.9m, Assert.Single(heaviest.Items).PayloadTons);

        // Alt tipin kuralları kendi alanına uygulanır.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/cars", new { plate = "BAD", seats = 40 })).StatusCode);
    }

    private static async Task<IEnumerable<string>> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!.Errors.Keys;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);

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
