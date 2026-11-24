using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Account;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

/// <summary>Kayıt işlemleri: sıra ve taşıma, kopyalama, kaydetmeyen önizleme.</summary>
[Collection(ShopCollection.Name)]
public sealed class RecordOperationEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly string _ada = $"ada-{Guid.NewGuid():N}";
    private readonly string _bob = $"bob-{Guid.NewGuid():N}";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ── Sıra ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Orderable_AppendsNewRecordsAndListsThemInOrder()
    {
        var parent = await CreateCategoryAsync("Parent");
        var first = await CreateCategoryAsync("First", parent.Id);
        var second = await CreateCategoryAsync("Second", parent.Id);
        var third = await CreateCategoryAsync("Third", parent.Id);

        Assert.Equal([0, 1, 2], new[] { first.Position, second.Position, third.Position });
        Assert.Equal(["First", "Second", "Third"], await ChildNamesAsync(parent.Id));
    }

    [Fact]
    public async Task Orderable_MoveRenumbersTheSiblingsWithoutGaps()
    {
        var parent = await CreateCategoryAsync("Parent");
        var first = await CreateCategoryAsync("First", parent.Id);
        await CreateCategoryAsync("Second", parent.Id);
        var third = await CreateCategoryAsync("Third", parent.Id);

        var toTop = await ReadAsync<CategoryResponse>(await SendAsync(HttpMethod.Post, $"/categories/{third.Id}/move", new { position = 0 }));

        Assert.Equal(0, toTop.Position);
        Assert.Equal([("Third", 0), ("First", 1), ("Second", 2)], await ChildrenAsync(parent.Id));

        // Listenin dışındaki bir konum sona taşır.
        var toEnd = await ReadAsync<CategoryResponse>(await SendAsync(HttpMethod.Post, $"/categories/{first.Id}/move", new { position = 99 }));

        Assert.Equal(2, toEnd.Position);
        Assert.Equal([("Third", 0), ("Second", 1), ("First", 2)], await ChildrenAsync(parent.Id));
    }

    [Fact]
    public async Task Orderable_MoveNeedsAValidPositionAndAnExistingRecord()
    {
        var category = await CreateCategoryAsync("Lonely");

        var missingPosition = await SendAsync(HttpMethod.Post, $"/categories/{category.Id}/move", new { });
        var negative = await SendAsync(HttpMethod.Post, $"/categories/{category.Id}/move", new { position = -1 });
        var missingRecord = await SendAsync(HttpMethod.Post, $"/categories/{int.MaxValue}/move", new { position = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, missingPosition.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRecord.StatusCode);
    }

    [Fact]
    public async Task Orderable_DeletingARecordClosesTheGap()
    {
        var parent = await CreateCategoryAsync("Parent");
        await CreateCategoryAsync("First", parent.Id);
        var second = await CreateCategoryAsync("Second", parent.Id);
        await CreateCategoryAsync("Third", parent.Id);

        var deleted = await SendAsync(HttpMethod.Delete, $"/categories/{second.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal([("First", 0), ("Third", 1)], await ChildrenAsync(parent.Id));
    }

    [Fact]
    public async Task Orderable_MovingANodeToAnotherParentReordersBothBranches()
    {
        var from = await CreateCategoryAsync("From");
        var to = await CreateCategoryAsync("To");
        var first = await CreateCategoryAsync("First", from.Id);
        await CreateCategoryAsync("Second", from.Id);
        await CreateCategoryAsync("Existing", to.Id);

        // Üst düğümü değiştiren güncelleme düğümü yeni dalın sonuna koyar ve eski daldaki boşluğu kapatır.
        var moved = await ReadAsync<CategoryResponse>(
            await SendAsync(HttpMethod.Put, $"/categories/{first.Id}", new { name = "First", parentId = to.Id }));

        Assert.Equal(1, moved.Position);
        Assert.Equal([("Second", 0)], await ChildrenAsync(from.Id));
        Assert.Equal([("Existing", 0), ("First", 1)], await ChildrenAsync(to.Id));

        // Aynı dalda kalan bir güncelleme sıraya dokunmaz.
        var renamed = await ReadAsync<CategoryResponse>(
            await SendAsync(HttpMethod.Patch, $"/categories/{first.Id}", new { name = "Renamed" }));
        Assert.Equal(1, renamed.Position);
    }

    [Fact]
    public async Task Orderable_ConcurrentWrites_KeepThePositionsDense()
    {
        var parent = await CreateCategoryAsync("Parent");

        // Aynı kapsama aynı anda eklenen kayıtlar kilidin altında sıraya girer; kimse aynı konumu almaz.
        var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => CreateCategoryAsync($"Child {index}", parent.Id)));
        Assert.Equal(Enumerable.Range(0, 8), created.Select(category => category.Position).Order());

        // Aynı anda yapılan taşımalar da birbirini bozmaz: sonuç yine 0'dan başlayan boşluksuz bir sıradır.
        var moves = await Task.WhenAll(created.Select((category, index) =>
            SendAsync(HttpMethod.Post, $"/categories/{category.Id}/move", new { position = (index * 3) % 8 })));

        Assert.All(moves, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(Enumerable.Range(0, 8), (await ChildrenAsync(parent.Id)).Select(child => child.Position));
    }

    [Fact]
    public async Task Orderable_UnderAParent_OrdersEachParentsRecordsOnTheirOwn()
    {
        var groceries = await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Post, "/me/shopping-lists", new { name = "Groceries" }, user: _ada));
        var hardware = await ReadAsync<ShoppingListResponse>(await SendAsync(HttpMethod.Post, "/me/shopping-lists", new { name = "Hardware" }, user: _ada));
        var groceryItems = $"/me/shopping-lists/{groceries.Id}/shopping-list-items";
        var hardwareItems = $"/me/shopping-lists/{hardware.Id}/shopping-list-items";

        await SendAsync(HttpMethod.Post, groceryItems, new { text = "Milk" }, user: _ada);
        await SendAsync(HttpMethod.Post, groceryItems, new { text = "Eggs" }, user: _ada);
        var bread = await ReadAsync<ShoppingListItemResponse>(await SendAsync(HttpMethod.Post, groceryItems, new { text = "Bread" }, user: _ada));
        var nails = await ReadAsync<ShoppingListItemResponse>(await SendAsync(HttpMethod.Post, hardwareItems, new { text = "Nails" }, user: _ada));

        // Her listenin sırası kendi içindedir.
        Assert.Equal(2, bread.Position);
        Assert.Equal(0, nails.Position);

        var moved = await SendAsync(HttpMethod.Post, $"{groceryItems}/{bread.Id}/move", new { position = 0 }, user: _ada);
        var stranger = await SendAsync(HttpMethod.Post, $"{groceryItems}/{bread.Id}/move", new { position = 2 }, user: _bob);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);

        var groceriesNow = await ReadAsync<PagedResult<ShoppingListItemResponse>>(await SendAsync(HttpMethod.Get, groceryItems, user: _ada));
        var hardwareNow = await ReadAsync<PagedResult<ShoppingListItemResponse>>(await SendAsync(HttpMethod.Get, hardwareItems, user: _ada));

        Assert.Equal([("Bread", 0), ("Milk", 1), ("Eggs", 2)], groceriesNow.Items.Select(item => (item.Text, item.Position)));
        Assert.Equal([("Nails", 0)], hardwareNow.Items.Select(item => (item.Text, item.Position)));
    }

    // ── Kopya ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cloneable_CopiesTheFieldsAndTheRowsIntoANewRecord()
    {
        var source = await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Post, "/carts", new
        {
            note = "Weekly",
            cartItems = new[] { new { productName = "Milk", quantity = 2 }, new { productName = "Eggs", quantity = 12 } },
        }));

        // Gövde isteğe bağlıdır.
        var response = await SendAsync(HttpMethod.Post, $"/carts/{source.Id}/clone");
        var copy = await ReadAsync<CartResponse>(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/carts/{copy.Id}", response.Headers.Location!.OriginalString);
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Weekly", copy.Note);
        Assert.Equal([("Milk", 2), ("Eggs", 12)], copy.CartItems.Select(item => (item.ProductName, item.Quantity)));

        // Satırlar kopyalanır, paylaşılmaz: her birinin kendi kimliği vardır ve kopyadaki değişiklik kaynağa dokunmaz.
        Assert.Empty(copy.CartItems.Select(item => item.Id).Intersect(source.CartItems.Select(item => item.Id)));

        await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Put, $"/carts/{copy.Id}", new { note = "Changed", cartItems = Array.Empty<object>() }));
        var untouched = await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Get, $"/carts/{source.Id}"));

        Assert.Equal("Weekly", untouched.Note);
        Assert.Equal(2, untouched.CartItems.Count);
    }

    [Fact]
    public async Task Cloneable_RequestCanOverrideFieldsOfTheCopy()
    {
        var source = await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Post, "/carts", new
        {
            note = "Original",
            cartItems = new[] { new { productName = "Milk", quantity = 1 } },
        }));

        var copy = await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Post, $"/carts/{source.Id}/clone", new { note = "Copy" }));
        var missing = await SendAsync(HttpMethod.Post, $"/carts/{Guid.NewGuid()}/clone");

        Assert.Equal("Copy", copy.Note);
        Assert.Single(copy.CartItems);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Cloneable_CopyIsANewRecord_UniqueFieldsNeedANewValue()
    {
        var code = NewCode();
        var source = await ReadAsync<CouponResponse>(
            await SendAsync(HttpMethod.Post, "/coupons", new { code, percent = 15, maxRedemptions = 3 }));
        await SendAsync(HttpMethod.Post, $"/coupons/{source.Id}/redeem");

        // Kod benzersizdir; değiştirilmeden kopyalanırsa kaynağınkiyle çakışır.
        var sameCode = await SendAsync(HttpMethod.Post, $"/coupons/{source.Id}/clone");
        var invalid = await SendAsync(HttpMethod.Post, $"/coupons/{source.Id}/clone", new { code = NewCode(), percent = 500 });

        Assert.Equal(HttpStatusCode.Conflict, sameCode.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var newCode = NewCode();
        var copy = await ReadAsync<CouponResponse>(await SendAsync(HttpMethod.Post, $"/coupons/{source.Id}/clone", new { code = newCode }));

        // Yazılabilir alanlar (yalnızca oluştururken yazılanlar dahil) kaynaktan gelir; kullanım sayacı gibi
        // sunucunun tuttuğu alanlar yeni kayıtta olduğu gibi başlar.
        Assert.Equal((newCode, 15, 3), (copy.Code, copy.Percent, copy.MaxRedemptions));
        Assert.Equal(0, copy.TimesRedeemed);
        Assert.Equal(1, (await ReadAsync<CouponResponse>(await SendAsync(HttpMethod.Get, $"/coupons/{source.Id}"))).TimesRedeemed);
    }

    // ── Önizleme ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Previewable_ShowsTheComputedResultOfACreateWithoutSavingIt()
    {
        var body = new
        {
            customerName = "Ada",
            orderItems = new[]
            {
                new { productName = "Keyboard", quantity = 2, unitPrice = 100m },
                new { productName = "Mouse", quantity = 1, unitPrice = 50m },
            },
        };

        var preview = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Post, "/orders/preview", body));
        var invalid = await SendAsync(HttpMethod.Post, "/orders/preview", new { customerName = new string('x', 201) });

        Assert.Equal(250m, preview.Total);
        Assert.Equal(2, preview.ItemCount);
        Assert.Equal(OrderStatus.Draft, preview.Status);

        // Sunucunun kaydederken atadığı alanlar önizlemede boştur.
        Assert.Equal(Guid.Empty, preview.Id);
        Assert.Null(preview.Number);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var orders = await ReadAsync<PagedResult<OrderResponse>>(await SendAsync(HttpMethod.Get, "/orders"));
        Assert.Empty(orders.Items);
    }

    [Fact]
    public async Task Previewable_ShowsTheResultOfAnUpdateAndLeavesTheRecordAsItWas()
    {
        var created = await SendAsync(HttpMethod.Post, "/orders", new
        {
            customerName = "Ada",
            orderItems = new[] { new { productName = "Keyboard", quantity = 1, unitPrice = 100m } },
        });
        var order = await ReadAsync<OrderResponse>(created);
        var tag = created.Headers.ETag!.Tag;

        var preview = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/preview", new
        {
            customerName = "Grace",
            orderItems = new object[]
            {
                new { id = order.OrderItems[0].Id, productName = "Keyboard", quantity = 3, unitPrice = 100m },
                new { productName = "Mouse", quantity = 1, unitPrice = 50m },
            },
        }));

        // Önizleme bir yazma değildir: sürüm istemez ve hiçbir şey değiştirmez.
        Assert.Equal(("Grace", 350m, 2), (preview.CustomerName, preview.Total, preview.ItemCount));

        var read = await SendAsync(HttpMethod.Get, $"/orders/{order.Id}");
        var unchanged = await ReadAsync<OrderResponse>(read);

        Assert.Equal(("Ada", 100m, 1), (unchanged.CustomerName, unchanged.Total, unchanged.ItemCount));
        Assert.Equal(tag, read.Headers.ETag!.Tag);

        var missing = await SendAsync(HttpMethod.Post, $"/orders/{Guid.NewGuid()}/preview", new { customerName = "Ada", orderItems = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Previewable_FollowsTheRulesOfTheRealWrite()
    {
        var created = await SendAsync(HttpMethod.Post, "/orders", new
        {
            customerName = "Ada",
            orderItems = new[] { new { productName = "Keyboard", quantity = 1, unitPrice = 100m } },
        });
        var order = await ReadAsync<OrderResponse>(created);

        var placed = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/place", new { }, ifMatch: created.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);

        // Verilmiş sipariş düzenlenemez; önizlemesi de aynı yanıtı verir.
        var preview = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/preview", new { customerName = "Grace", orderItems = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.Conflict, preview.StatusCode);
    }

    private async Task<CategoryResponse> CreateCategoryAsync(string name, int? parentId = null) =>
        await ReadAsync<CategoryResponse>(await SendAsync(HttpMethod.Post, "/categories", new { name, parentId }));

    private async Task<(string Name, int Position)[]> ChildrenAsync(int parentId)
    {
        var page = await ReadAsync<PagedResult<CategoryResponse>>(await SendAsync(HttpMethod.Get, $"/categories?parentId={parentId}&pageSize=100"));
        return [.. page.Items.Select(item => (item.Name, item.Position))];
    }

    private async Task<string[]> ChildNamesAsync(int parentId) => [.. (await ChildrenAsync(parentId)).Select(child => child.Name)];

    private static string NewCode() => $"C-{Guid.NewGuid():N}";

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string? user = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, _tenant.ToString());

        if (user is not null)
        {
            request.Headers.Add(DemoIdentity.UserHeader, user);
        }

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
