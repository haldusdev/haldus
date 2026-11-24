using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus.IntegrationTests;

/// <summary><c>Expiring</c>, <c>Idempotent</c> ve sequence'li <c>Numbered</c>.</summary>
[Collection(ShopCollection.Name)]
public sealed class LifecycleTraitEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Expiring_WithALifetime_SetsTheExpiryOnTheServer()
    {
        var before = DateTimeOffset.UtcNow;

        // İstekteki son kullanma zamanı yok sayılır; ömrü sunucu verir.
        var reservation = await ReadAsync<ReservationResponse>(await SendAsync(
            HttpMethod.Post, "/reservations", new { productName = "Keyboard", quantity = 1, expiresAt = before.AddYears(1) }));

        Assert.NotNull(reservation.ExpiresAt);
        Assert.InRange(reservation.ExpiresAt.Value, before.AddMinutes(15).AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(15));
    }

    [Fact]
    public async Task Expiring_HidesTheRecordFromEveryEndpointOnceItsTimeHasPassed()
    {
        var kept = await ReadAsync<ReservationResponse>(await SendAsync(
            HttpMethod.Post, "/reservations", new { productName = "Kept", quantity = 1 }));
        var expired = await ReadAsync<ReservationResponse>(await SendAsync(
            HttpMethod.Post, "/reservations", new { productName = "Expired", quantity = 1 }));

        await fixture.WithDbContextAsync(db => db.Reservations.IgnoreQueryFilters()
            .Where(reservation => reservation.Id == expired.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(reservation => reservation.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)),
                CancellationToken));

        var read = await SendAsync(HttpMethod.Get, $"/reservations/{expired.Id}");
        var update = await SendAsync(HttpMethod.Put, $"/reservations/{expired.Id}", new { productName = "Revived", quantity = 1 });
        var delete = await SendAsync(HttpMethod.Delete, $"/reservations/{expired.Id}");
        var list = await ReadAsync<PagedResult<ReservationResponse>>(await SendAsync(HttpMethod.Get, "/reservations"));

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal([kept.Id], list.Items.Select(reservation => reservation.Id));

        // Kayıt silinmedi; yalnızca erişimi kesildi.
        var stored = "";
        await fixture.WithDbContextAsync(async db => stored = await db.Reservations.IgnoreQueryFilters()
            .Where(reservation => reservation.Id == expired.Id)
            .Select(reservation => reservation.ProductName)
            .SingleAsync(CancellationToken));
        Assert.Equal("Expired", stored);
    }

    [Fact]
    public async Task Expire_EndsTheRecordImmediately()
    {
        var reservation = await ReadAsync<ReservationResponse>(await SendAsync(
            HttpMethod.Post, "/reservations", new { productName = "Keyboard", quantity = 1 }));

        var expire = await SendAsync(HttpMethod.Post, $"/reservations/{reservation.Id}/expire");
        var read = await SendAsync(HttpMethod.Get, $"/reservations/{reservation.Id}");
        var again = await SendAsync(HttpMethod.Post, $"/reservations/{reservation.Id}/expire");

        Assert.Equal(HttpStatusCode.NoContent, expire.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Expiring_Sliding_RestartsTheLifetimeOnEveryWrite()
    {
        var cart = await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Post, "/carts", new { note = "First" }));

        // Sepetin ömrünün çoğu geçmiş olsun.
        var almostOver = DateTimeOffset.UtcNow.AddMinutes(5);
        await fixture.WithDbContextAsync(db => db.Carts
            .Where(candidate => candidate.Id == cart.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.ExpiresAt, almostOver), CancellationToken));

        var read = await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Get, $"/carts/{cart.Id}"));
        var written = await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Patch, $"/carts/{cart.Id}", new { note = "Second" }));

        // Okumak süreyi uzatmaz, yazmak uzatır.
        Assert.InRange(read.ExpiresAt!.Value, almostOver.AddSeconds(-1), almostOver.AddSeconds(1));
        Assert.True(written.ExpiresAt > DateTimeOffset.UtcNow.AddDays(6), $"Expected a fresh lifetime, got {written.ExpiresAt}.");
    }

    [Fact]
    public async Task Expiring_WithoutALifetime_TakesTheExpiryFromTheClient()
    {
        var future = DateTimeOffset.UtcNow.AddDays(1);

        var open = await ReadAsync<CouponResponse>(await SendAsync(HttpMethod.Post, "/coupons", new { code = "OPEN", percent = 10 }));
        var limited = await ReadAsync<CouponResponse>(
            await SendAsync(HttpMethod.Post, "/coupons", new { code = "LIMITED", percent = 10, expiresAt = future }));

        Assert.Null(open.ExpiresAt);
        Assert.InRange(limited.ExpiresAt!.Value, future.AddSeconds(-1), future.AddSeconds(1));

        // Son kullanma zamanı geçmişe çekilirse kupon o andan itibaren görünmez.
        var shortened = await SendAsync(
            HttpMethod.Patch, $"/coupons/{limited.Id}", new { expiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        var read = await SendAsync(HttpMethod.Get, $"/coupons/{limited.Id}");
        var stillOpen = await SendAsync(HttpMethod.Get, $"/coupons/{open.Id}");

        Assert.Equal(HttpStatusCode.OK, shortened.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillOpen.StatusCode);
    }

    [Fact]
    public async Task Idempotent_ReplaysTheStoredResponseInsteadOfRepeatingTheWork()
    {
        var order = await CreateOrderAsync();
        var key = Guid.NewGuid().ToString();
        var body = new { orderId = order.Id, amount = 25.5, reason = "Damaged" };

        var first = await SendAsync(HttpMethod.Post, "/refunds", body, idempotencyKey: key);
        var second = await SendAsync(HttpMethod.Post, "/refunds", body, idempotencyKey: key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(["true"], second.Headers.GetValues("Idempotent-Replayed"));
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(CancellationToken),
            await second.Content.ReadAsStringAsync(CancellationToken));

        var refunds = await ReadAsync<PagedResult<RefundResponse>>(await SendAsync(HttpMethod.Get, "/refunds"));
        Assert.Single(refunds.Items);
    }

    [Fact]
    public async Task Idempotent_Required_RejectsARequestWithoutTheKey()
    {
        var order = await CreateOrderAsync();
        var body = new { orderId = order.Id, amount = 10 };

        var missing = await SendAsync(HttpMethod.Post, "/refunds", body);
        var tooLong = await SendAsync(HttpMethod.Post, "/refunds", body, idempotencyKey: new string('k', 256));

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Empty((await ReadAsync<PagedResult<RefundResponse>>(await SendAsync(HttpMethod.Get, "/refunds"))).Items);
    }

    [Fact]
    public async Task Idempotent_RejectsAKeyReusedWithADifferentRequest()
    {
        var order = await CreateOrderAsync();
        var key = Guid.NewGuid().ToString();

        await SendAsync(HttpMethod.Post, "/refunds", new { orderId = order.Id, amount = 10 }, idempotencyKey: key);
        var reused = await SendAsync(HttpMethod.Post, "/refunds", new { orderId = order.Id, amount = 9999 }, idempotencyKey: key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(10m, Assert.Single((await ReadAsync<PagedResult<RefundResponse>>(await SendAsync(HttpMethod.Get, "/refunds"))).Items).Amount);
    }

    [Fact]
    public async Task Idempotent_DoesNotRememberAFailedRequest()
    {
        var order = await CreateOrderAsync();
        var key = Guid.NewGuid().ToString();

        // Var olmayan siparişe iade: istek başarısız olur ve anahtarla birlikte geri alınır.
        var failed = await SendAsync(HttpMethod.Post, "/refunds", new { orderId = Guid.NewGuid(), amount = 10 }, idempotencyKey: key);
        var corrected = await SendAsync(HttpMethod.Post, "/refunds", new { orderId = order.Id, amount = 10 }, idempotencyKey: key);

        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
        Assert.False(corrected.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task Idempotent_KeysBelongToTheTenantThatUsedThem()
    {
        var key = Guid.NewGuid().ToString();
        var other = Guid.NewGuid();

        var mine = await ReadAsync<OrderResponse>(
            await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada" }, idempotencyKey: key));
        var theirs = await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada" }, idempotencyKey: key, tenant: other);

        // Aynı anahtar başka bir kiracıda yeni bir istektir; ilk kiracının yanıtı ona dönmez.
        Assert.False(theirs.Headers.Contains("Idempotent-Replayed"));
        Assert.NotEqual(mine.Id, (await ReadAsync<OrderResponse>(theirs)).Id);
    }

    [Fact]
    public async Task Idempotent_AlsoCoversTransitions()
    {
        var created = await SendAsync(HttpMethod.Post, "/orders", new
        {
            customerName = "Ada",
            orderItems = new object[] { new { productName = "Keyboard", quantity = 1, unitPrice = 100 } },
        });
        var order = await ReadAsync<OrderResponse>(created);
        var tag = created.Headers.ETag!.Tag;
        var key = Guid.NewGuid().ToString();

        var first = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/place", new { }, ifMatch: tag, idempotencyKey: key);

        // İkinci istek eski sürümü gönderir ve sipariş artık taslak değildir; yine de ilk yanıt döner.
        var second = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/place", new { }, ifMatch: tag, idempotencyKey: key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(first.Headers.ETag, second.Headers.ETag);
        Assert.Equal((await ReadAsync<OrderResponse>(first)).Number, (await ReadAsync<OrderResponse>(second)).Number);
    }

    [Fact]
    public async Task Idempotent_SimultaneousRequestsWithOneKeyCreateOneRecord()
    {
        var order = await CreateOrderAsync();
        var key = Guid.NewGuid().ToString();
        var body = new { orderId = order.Id, amount = 10 };

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => SendAsync(HttpMethod.Post, "/refunds", body, idempotencyKey: key)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Single(responses, response => !response.Headers.Contains("Idempotent-Replayed"));
        Assert.Single((await ReadAsync<PagedResult<RefundResponse>>(await SendAsync(HttpMethod.Get, "/refunds"))).Items);
    }

    [Fact]
    public async Task Numbered_FromASequence_GivesEveryRecordItsOwnNumber()
    {
        var order = await CreateOrderAsync();

        var first = await ReadAsync<RefundResponse>(await SendAsync(
            HttpMethod.Post, "/refunds", new { orderId = order.Id, amount = 1 }, idempotencyKey: Guid.NewGuid().ToString()));
        var second = await ReadAsync<RefundResponse>(await SendAsync(
            HttpMethod.Post, "/refunds", new { orderId = order.Id, amount = 2 }, idempotencyKey: Guid.NewGuid().ToString()));

        Assert.Matches(@"^RF-\d{8}$", first.Number);
        Assert.Matches(@"^RF-\d{8}$", second.Number);
        Assert.True(string.CompareOrdinal(first.Number, second.Number) < 0, $"{first.Number} should come before {second.Number}.");
    }

    [Fact]
    public async Task Maintenance_RemovesIdempotencyRecordsPastTheirRetention()
    {
        var key = Guid.NewGuid().ToString();
        var first = await ReadAsync<OrderResponse>(
            await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada" }, idempotencyKey: key));

        // Kaydın saklama süresi dolmuş olsun.
        await fixture.WithDbContextAsync(db => db.Set<HaldusIdempotencyRecord>()
            .Where(record => record.Key == key)
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken));

        await HaldusMaintenance.RunAsync(fixture.Services, CancellationToken);

        var remaining = -1;
        await fixture.WithDbContextAsync(async db =>
            remaining = await db.Set<HaldusIdempotencyRecord>().CountAsync(record => record.Key == key, CancellationToken));
        Assert.Equal(0, remaining);

        // Anahtar artık yeni bir istektir.
        var second = await ReadAsync<OrderResponse>(
            await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada" }, idempotencyKey: key));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Maintenance_PurgesExpiredRecordsOnlyWhenTheApplicationAsksForIt()
    {
        var reservation = await ReadAsync<ReservationResponse>(await SendAsync(
            HttpMethod.Post, "/reservations", new { productName = "Keyboard", quantity = 1 }));
        await SendAsync(HttpMethod.Post, $"/reservations/{reservation.Id}/expire");

        async Task<int> CountAsync()
        {
            var count = -1;
            await fixture.WithDbContextAsync(async db => count = await db.Reservations.IgnoreQueryFilters()
                .CountAsync(candidate => candidate.Id == reservation.Id, CancellationToken));
            return count;
        }

        // Varsayılan: süresi dolan kayıt gizlenir ama silinmez.
        await HaldusMaintenance.RunAsync(fixture.Services, CancellationToken);
        Assert.Equal(1, await CountAsync());

        await using var purging = fixture.CreateFactory(services =>
            services.Configure<HaldusOptions>(options => options.PurgeExpiredAfter = TimeSpan.Zero));
        await HaldusMaintenance.RunAsync(purging.Services, CancellationToken);
        Assert.Equal(0, await CountAsync());
    }

    private async Task<OrderResponse> CreateOrderAsync() =>
        await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada" }));

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        object? body = null,
        string? ifMatch = null,
        Guid? tenant = null,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, (tenant ?? _tenant).ToString());

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
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
