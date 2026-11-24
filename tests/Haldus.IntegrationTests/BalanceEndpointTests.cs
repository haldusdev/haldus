using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Stock;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

/// <summary>Bakiyeler (<c>MapBalance</c>): düzeltme, mutlak yazma, kovalar arası taşıma ve defter.</summary>
[Collection(ShopCollection.Name)]
public sealed class BalanceEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly string _sku = "SKU-" + Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private string Url => $"/inventories/{_sku}";

    [Fact]
    public async Task Adjust_ChangesTheBalanceAndWritesTheLedger()
    {
        // Hiç yazılmamış bir anahtarın kaydı yoktur; ilk düzeltme onu oluşturur.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, Url)).StatusCode);

        var received = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 10m, reason = "Goods receipt" }));
        Assert.Equal((_sku, 10m, 0m, 0m), (received.Key, received.Available, received.Reserved, received.Damaged));

        var damaged = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 2m, bucket = "damaged", reason = "Dropped" }));
        var sold = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = -3.5m }));
        Assert.Equal((6.5m, 0m, 2m), (sold.Available, sold.Reserved, sold.Damaged));
        Assert.Equal(2m, damaged.Damaged);

        var read = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Get, Url));
        Assert.Equal(6.5m, read.Available);
        Assert.NotNull(read.UpdatedAt);

        // Her değişim deftere geçer: en yeni başta, kovası, değişimi ve sonraki bakiyesiyle.
        var entries = await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{Url}/entries"));
        Assert.Equal(3, entries.TotalCount);
        Assert.Equal(
            [("available", -3.5m, 6.5m, null), ("damaged", 2m, 2m, "Dropped"), ("available", 10m, 10m, "Goods receipt")],
            entries.Items.Select(entry => (entry.Bucket, entry.Delta, entry.Balance, entry.Reason)));
        Assert.True(entries.Items[0].Id > entries.Items[1].Id);

        var onlyDamaged = await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{Url}/entries?bucket=damaged"));
        Assert.Equal("Dropped", Assert.Single(onlyDamaged.Items).Reason);

        // Bakiye başka hiçbir yoldan yazılamaz.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(HttpMethod.Put, Url, new { available = 999m })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(HttpMethod.Delete, Url)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(HttpMethod.Post, "/inventories", new { key = "X", available = 5m })).StatusCode);

        // Liste bakiyeye göre sıralanır.
        var listed = await ReadAsync<PagedResult<InventoryResponse>>(await SendAsync(HttpMethod.Get, "/inventories?sort=-available"));
        Assert.Equal(_sku, Assert.Single(listed.Items).Key);
    }

    [Fact]
    public async Task Balance_CannotDropBelowZero()
    {
        await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 5m }));

        var overdrawn = await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = -6m });
        Assert.Equal(HttpStatusCode.Conflict, overdrawn.StatusCode);
        Assert.Equal("Insufficient balance", (await overdrawn.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Title);

        // Reddedilen düzeltme hiçbir şey yazmaz: ne bakiyeyi ne defteri.
        Assert.Equal(5m, (await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Get, Url))).Available);
        Assert.Equal(1, (await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{Url}/entries"))).TotalCount);

        // Hiç yazılmamış bir bakiyeden düşülemez ve reddedilen istek kaydı oluşturmaz.
        var unknown = $"/inventories/NEW-{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{unknown}/adjust", new { delta = -1m })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, unknown)).StatusCode);

        // Tam bakiyeyi düşmek serbesttir.
        var emptied = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = -5m }));
        Assert.Equal(0m, emptied.Available);

        var turkish = await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = -1m }, language: "tr");
        Assert.Equal("Yetersiz bakiye", (await turkish.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Title);
    }

    [Fact]
    public async Task ConcurrentAdjustments_NeverOverdrawTheBalance()
    {
        await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 10m }));

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = -1m })));

        // Kontrol ile yazma arasına başka bir istek giremez: tam on istek geçer, bakiye sıfırda kalır.
        Assert.Equal(10, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(10, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0m, (await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Get, Url))).Available);
        Assert.Equal(11, (await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{Url}/entries"))).TotalCount);
    }

    [Fact]
    public async Task Set_WritesAnAbsoluteValueOnlyIfTheBalanceIsAsExpected()
    {
        await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 38m }));

        // Sayım sırasında bakiye değiştiyse mutlak değer onun üzerine yazılmaz.
        var stale = await SendAsync(HttpMethod.Post, $"{Url}/set", new { quantity = 40m, expected = 37m });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var counted = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/set", new { quantity = 40m, expected = 38m, reason = "Stock count" }));
        Assert.Equal(40m, counted.Available);

        // Beklenen değer verilmezse koşulsuz yazılır; deftere aradaki fark geçer.
        await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/set", new { quantity = 25m, bucket = "reserved" }));
        var unchanged = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/set", new { quantity = 40m }));
        Assert.Equal((40m, 25m), (unchanged.Available, unchanged.Reserved));

        var entries = await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{Url}/entries"));
        Assert.Equal(
            [("reserved", 25m, 25m), ("available", 2m, 40m), ("available", 38m, 38m)],
            entries.Items.Select(entry => (entry.Bucket, entry.Delta, entry.Balance)));

        Assert.Equal(["quantity"], await ErrorsAsync(await SendAsync(HttpMethod.Post, $"{Url}/set", new { quantity = -1m })));
    }

    [Fact]
    public async Task Move_ShiftsAQuantityBetweenBucketsAtomically()
    {
        await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 10m }));

        var reserved = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/move", new { from = "available", to = "reserved", quantity = 4m, reason = "Order 1042" }));
        Assert.Equal((6m, 4m), (reserved.Available, reserved.Reserved));

        // Kaynak kova yetmiyorsa hiçbir kova değişmez.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{Url}/move", new { from = "available", to = "reserved", quantity = 7m })).StatusCode);
        var after = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Get, Url));
        Assert.Equal((6m, 4m), (after.Available, after.Reserved));

        var entries = await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{Url}/entries"));
        Assert.Equal(
            [("reserved", 4m, 4m, "Order 1042"), ("available", -4m, 6m, "Order 1042"), ("available", 10m, 10m, null)],
            entries.Items.Select(entry => (entry.Bucket, entry.Delta, entry.Balance, entry.Reason)));

        Assert.Equal(["from"], await ErrorsAsync(await SendAsync(HttpMethod.Post, $"{Url}/move", new { from = "lost", to = "reserved", quantity = 1m })));
        Assert.Equal(["to"], await ErrorsAsync(await SendAsync(HttpMethod.Post, $"{Url}/move", new { from = "available", to = "available", quantity = 1m })));
        Assert.Equal(["quantity"], await ErrorsAsync(await SendAsync(HttpMethod.Post, $"{Url}/move", new { from = "available", to = "reserved", quantity = 0m })));
    }

    [Fact]
    public async Task Holds_ReserveAQuantityUntilTheyAreCapturedOrReleased()
    {
        await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 10m }));

        // Tutma miktarı eldeki stoktan ayrılmış stoğa taşır ve bir kayıt açar.
        var first = await ReadAsync<BalanceHold>(await SendAsync(HttpMethod.Post, $"{Url}/holds", new { quantity = 4m, expiresInSeconds = 3600, reason = "Order 1" }));
        Assert.Equal((4m, "Active", "Order 1"), (first.Quantity, first.Status, first.Reason));
        Assert.NotNull(first.ExpiresAt);

        var held = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Get, Url));
        Assert.Equal((6m, 4m), (held.Available, held.Reserved));

        // Eldekinden fazlası tutulamaz; reddedilen istek hiçbir şey yazmaz.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{Url}/holds", new { quantity = 7m })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, $"{Url}/holds", new { quantity = 0m })).StatusCode);

        var second = await ReadAsync<BalanceHold>(await SendAsync(HttpMethod.Post, $"{Url}/holds", new { quantity = 2m }));
        Assert.Null(second.ExpiresAt);

        var open = await ReadAsync<List<BalanceHold>>(await SendAsync(HttpMethod.Get, $"{Url}/holds"));
        Assert.Equal([first.Id, second.Id], open.Select(hold => hold.Id));

        // Tahsil: miktar bakiyeden düşer. Kapanan tutma ikinci kez kapatılamaz.
        var captured = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/holds/{first.Id}/capture"));
        Assert.Equal((4m, 2m), (captured.Available, captured.Reserved));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{Url}/holds/{first.Id}/capture")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{Url}/holds/{first.Id}/release")).StatusCode);

        // Bırakma: miktar eldeki stoğa geri döner.
        var released = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/holds/{second.Id}/release"));
        Assert.Equal((6m, 0m), (released.Available, released.Reserved));
        Assert.Empty(await ReadAsync<List<BalanceHold>>(await SendAsync(HttpMethod.Get, $"{Url}/holds")));

        // Olmayan tutma ve başka kiracının tutması bulunamaz.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"{Url}/holds/{Guid.NewGuid()}/capture")).StatusCode);

        var third = await ReadAsync<BalanceHold>(await SendAsync(HttpMethod.Post, $"{Url}/holds", new { quantity = 1m }));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"{Url}/holds/{third.Id}/capture", tenant: _otherTenant)).StatusCode);
        Assert.Empty(await ReadAsync<List<BalanceHold>>(await SendAsync(HttpMethod.Get, $"{Url}/holds", tenant: _otherTenant)));

        // Her adım deftere geçer.
        var entries = await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{Url}/entries?pageSize=50"));
        Assert.Contains(entries.Items, entry => entry.Bucket == "reserved" && entry.Delta == 4m && entry.Reason == "Order 1");
        Assert.Contains(entries.Items, entry => entry.Bucket == "reserved" && entry.Delta == -4m);
        Assert.Contains(entries.Items, entry => entry.Bucket == "available" && entry.Delta == 2m && entry.Reason == "Released");
    }

    [Fact]
    public async Task ExpiredHolds_AreReleasedByMaintenance()
    {
        await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 5m }));

        var expiring = await ReadAsync<BalanceHold>(await SendAsync(HttpMethod.Post, $"{Url}/holds", new { quantity = 3m, expiresInSeconds = 60 }));
        var lasting = await ReadAsync<BalanceHold>(await SendAsync(HttpMethod.Post, $"{Url}/holds", new { quantity = 1m, expiresInSeconds = 86_400 }));

        // Süresi dolmamış tutmalara bakım dokunmaz.
        await HaldusMaintenance.RunAsync(fixture.Services, CancellationToken);
        var before = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Get, Url));
        Assert.Equal((1m, 4m), (before.Available, before.Reserved));

        // Tutmanın süresi geçer (saat ileri alınamadığı için kayıt geriye çekilir).
        await fixture.WithDbContextAsync(async context =>
        {
            var hold = await context.Set<HaldusBalanceHold>().SingleAsync(candidate => candidate.Id == expiring.Id, CancellationToken);
            hold.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync(CancellationToken);
        });

        // Süresi geçmiş tutma tahsil edilemez, ama elle bırakılabilir; bakım onu kendiliğinden bırakır.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{Url}/holds/{expiring.Id}/capture")).StatusCode);

        await HaldusMaintenance.RunAsync(fixture.Services, CancellationToken);

        var after = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Get, Url));
        Assert.Equal((4m, 1m), (after.Available, after.Reserved));
        Assert.Equal(lasting.Id, Assert.Single(await ReadAsync<List<BalanceHold>>(await SendAsync(HttpMethod.Get, $"{Url}/holds"))).Id);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{Url}/holds/{expiring.Id}/release")).StatusCode);

        var entries = await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{Url}/entries?pageSize=50"));
        Assert.Contains(entries.Items, entry => entry.Bucket == "available" && entry.Delta == 3m && entry.Reason == "Expired");

        // İkinci tur aynı tutmayı yeniden bırakmaz.
        await HaldusMaintenance.RunAsync(fixture.Services, CancellationToken);
        Assert.Equal(4m, (await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Get, Url))).Available);
    }

    [Fact]
    public async Task Requests_AreValidated()
    {
        Assert.Equal(["delta"], await ErrorsAsync(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 0m })));
        Assert.Equal(["bucket"], await ErrorsAsync(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 1m, bucket = "lost" })));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 1m, reason = new string('x', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/inventories/{new string('k', 201)}/adjust", new { delta = 1m })).StatusCode);

        // Kiracısı bilinmeyen istek reddedilir.
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, $"{Url}/adjust") { Content = JsonContent.Create(new { delta = 1m }) };
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(anonymous, CancellationToken)).StatusCode);

        var turkish = await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 1m, bucket = "lost" }, language: "tr");
        var problem = await turkish.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.Equal("'lost' bu bakiyenin bir kovası değil. Kovalar: available, reserved, damaged.", Assert.Single(problem!.Errors["bucket"]));
    }

    [Fact]
    public async Task Balances_AndTheirLedgers_BelongToTheTenant()
    {
        await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 10m }));

        // Aynı anahtar başka bir kiracıda ayrı bir bakiyedir.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, Url, tenant: _otherTenant)).StatusCode);
        Assert.Equal(0, (await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{Url}/entries", tenant: _otherTenant))).TotalCount);

        var theirs = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 3m }, tenant: _otherTenant));
        Assert.Equal(3m, theirs.Available);
        Assert.Equal(10m, (await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Get, Url))).Available);
        Assert.Equal(1, (await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{Url}/entries", tenant: _otherTenant))).TotalCount);
    }

    [Fact]
    public async Task Idempotent_AppliesARetriedAdjustmentOnce()
    {
        var key = Guid.NewGuid().ToString("N");

        var first = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 5m }, idempotencyKey: key));
        var retried = await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Post, $"{Url}/adjust", new { delta = 5m }, idempotencyKey: key));

        Assert.Equal((5m, 5m), (first.Available, retried.Available));
        Assert.Equal(5m, (await ReadAsync<InventoryResponse>(await SendAsync(HttpMethod.Get, Url))).Available);
        Assert.Equal(1, (await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{Url}/entries"))).TotalCount);
    }

    [Fact]
    public async Task SingleBucketBalance_MayGoNegativeWhenAllowed()
    {
        var url = $"/leave-allowances/EMP-{Guid.NewGuid():N}";

        var granted = await ReadAsync<LeaveAllowanceResponse>(await SendAsync(HttpMethod.Post, $"{url}/adjust", new { delta = 14m, reason = "Annual grant" }));
        var advanced = await ReadAsync<LeaveAllowanceResponse>(await SendAsync(HttpMethod.Post, $"{url}/adjust", new { delta = -15.5m, reason = "Advance" }));
        Assert.Equal((14m, -1.5m), (granted.Quantity, advanced.Quantity));

        var reset = await ReadAsync<LeaveAllowanceResponse>(await SendAsync(HttpMethod.Post, $"{url}/set", new { quantity = -3m }));
        Assert.Equal(-3m, reset.Quantity);

        // Tek kovalı bakiyede taşınacak bir yer yoktur.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"{url}/move", new { from = "quantity", to = "quantity", quantity = 1m })).StatusCode);

        var entries = await ReadAsync<PagedResult<BalanceEntry>>(await SendAsync(HttpMethod.Get, $"{url}/entries"));
        Assert.All(entries.Items, entry => Assert.Equal("quantity", entry.Bucket));
        Assert.Equal(3, entries.TotalCount);
    }

    private static async Task<IEnumerable<string>> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!.Errors.Keys;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        object? body = null,
        Guid? tenant = null,
        string? language = null,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, (tenant ?? _tenant).ToString());

        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
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
