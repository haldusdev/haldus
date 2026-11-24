using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed class DetailEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_SavesTheRowsWithTheRecordAndComputesTheTotals()
    {
        var response = await SendAsync(HttpMethod.Post, "/orders", new
        {
            customerName = "Ada",
            orderItems = new object[]
            {
                new { productName = "Keyboard", quantity = 2, unitPrice = 100 },
                new { productName = "Mouse", quantity = 1, unitPrice = 40.5 },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ReadAsync<OrderResponse>(response);

        Assert.Equal(["Keyboard", "Mouse"], order.OrderItems.Select(item => item.ProductName));
        Assert.All(order.OrderItems, item => Assert.Equal(order.Id, item.OrderId));
        Assert.Equal(240.5m, order.Total);
        Assert.Equal(2, order.ItemCount);

        var read = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"/orders/{order.Id}"));
        var list = await ReadAsync<PagedResult<OrderResponse>>(await SendAsync(HttpMethod.Get, "/orders"));

        Assert.Equal(order.OrderItems, read.OrderItems);
        Assert.Equal(240.5m, read.Total);
        Assert.Equal(order.OrderItems, Assert.Single(list.Items).OrderItems);
    }

    [Fact]
    public async Task Create_WithoutRows_IsAllowed()
    {
        var order = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada" }));

        Assert.Empty(order.OrderItems);
        Assert.Equal(0m, order.Total);
    }

    [Fact]
    public async Task Put_SynchronizesTheRows_UpdatingAddingAndRemoving()
    {
        var (order, etag) = await CreateOrderAsync(("Keep", 1, 10m), ("Change", 1, 20m), ("Drop", 1, 30m));
        var keep = order.OrderItems[0];
        var change = order.OrderItems[1];

        var response = await SendAsync(
            HttpMethod.Put, $"/orders/{order.Id}",
            new
            {
                customerName = "Ada Lovelace",
                orderItems = new object[]
                {
                    new { id = keep.Id, productName = "Keep", quantity = 1, unitPrice = 10 },
                    new { id = change.Id, productName = "Changed", quantity = 5, unitPrice = 20 },
                    new { productName = "New", quantity = 2, unitPrice = 7 },
                },
            },
            ifMatch: etag);

        var updated = await ReadAsync<OrderResponse>(response);

        Assert.Equal("Ada Lovelace", updated.CustomerName);
        Assert.Equal(["Keep", "Changed", "New"], updated.OrderItems.Select(item => item.ProductName));
        Assert.Equal([keep.Id, change.Id], updated.OrderItems.Take(2).Select(item => item.Id));
        Assert.Equal(5, updated.OrderItems[1].Quantity);
        Assert.Equal(10m + 100m + 14m, updated.Total);
        Assert.Equal(3, updated.ItemCount);

        var persisted = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"/orders/{order.Id}"));
        Assert.Equal(updated.OrderItems, persisted.OrderItems);
        Assert.Equal(124m, persisted.Total);
    }

    [Fact]
    public async Task Put_KeepsTheRowsInTheOrderTheyWereSent()
    {
        var (order, etag) = await CreateOrderAsync(("First", 1, 10m), ("Second", 1, 20m));

        var response = await SendAsync(
            HttpMethod.Put, $"/orders/{order.Id}",
            new
            {
                customerName = order.CustomerName,
                orderItems = new object[]
                {
                    new { productName = "Inserted", quantity = 1, unitPrice = 5 },
                    new { id = order.OrderItems[1].Id, productName = "Second", quantity = 1, unitPrice = 20 },
                    new { id = order.OrderItems[0].Id, productName = "First", quantity = 1, unitPrice = 10 },
                },
            },
            ifMatch: etag);

        var updated = await ReadAsync<OrderResponse>(response);
        var persisted = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"/orders/{order.Id}"));

        Assert.Equal(["Inserted", "Second", "First"], updated.OrderItems.Select(item => item.ProductName));
        Assert.Equal(["Inserted", "Second", "First"], persisted.OrderItems.Select(item => item.ProductName));
    }

    [Fact]
    public async Task Put_WithoutTheRowCollection_IsRejectedInsteadOfDeletingEveryRow()
    {
        var (order, etag) = await CreateOrderAsync(("Only", 1, 10m));

        var response = await SendAsync(HttpMethod.Put, $"/orders/{order.Id}", new { customerName = "Ada" }, ifMatch: etag);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(["orderItems"], problem.Errors.Keys);

        var unchanged = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"/orders/{order.Id}"));
        Assert.Single(unchanged.OrderItems);
    }

    [Fact]
    public async Task Put_WithARowIdThatDoesNotBelongToTheRecord_ChangesNothing()
    {
        var (order, etag) = await CreateOrderAsync(("Mine", 1, 10m));
        var (other, _) = await CreateOrderAsync(("Theirs", 1, 99m));
        var mine = order.OrderItems[0];

        object Request(params object[] rows) => new { customerName = "Hijack", orderItems = rows };

        var foreign = await SendAsync(
            HttpMethod.Put, $"/orders/{order.Id}",
            Request(new { id = other.OrderItems[0].Id, productName = "Stolen", quantity = 1, unitPrice = 1 }), ifMatch: etag);
        var unknown = await SendAsync(
            HttpMethod.Put, $"/orders/{order.Id}",
            Request(new { id = Guid.NewGuid(), productName = "Ghost", quantity = 1, unitPrice = 1 }), ifMatch: etag);
        var duplicated = await SendAsync(
            HttpMethod.Put, $"/orders/{order.Id}",
            Request(
                new { id = mine.Id, productName = "Once", quantity = 1, unitPrice = 1 },
                new { id = mine.Id, productName = "Twice", quantity = 1, unitPrice = 1 }),
            ifMatch: etag);

        foreach (var response in new[] { foreign, unknown, duplicated })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
            Assert.NotNull(problem);
            Assert.Equal(["orderItems"], problem.Errors.Keys);
        }

        var unchanged = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"/orders/{order.Id}"));
        var untouched = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"/orders/{other.Id}"));

        Assert.Equal(order.CustomerName, unchanged.CustomerName);
        Assert.Equal(order.OrderItems, unchanged.OrderItems);
        Assert.Equal("Theirs", Assert.Single(untouched.OrderItems).ProductName);
    }

    [Fact]
    public async Task Patch_LeavesTheRowsAloneUnlessTheCollectionIsSent()
    {
        var (order, etag) = await CreateOrderAsync(("A", 2, 10m), ("B", 1, 5m));

        var renamedResponse = await SendAsync(HttpMethod.Patch, $"/orders/{order.Id}", new { customerName = "Grace" }, ifMatch: etag);
        var renamed = await ReadAsync<OrderResponse>(renamedResponse);

        Assert.Equal("Grace", renamed.CustomerName);
        Assert.Equal(order.OrderItems, renamed.OrderItems);
        Assert.Equal(25m, renamed.Total);

        var emptied = await ReadAsync<OrderResponse>(await SendAsync(
            HttpMethod.Patch, $"/orders/{order.Id}", new { orderItems = Array.Empty<object>() },
            ifMatch: renamedResponse.Headers.ETag?.Tag));

        Assert.Equal("Grace", emptied.CustomerName);
        Assert.Empty(emptied.OrderItems);
        Assert.Equal(0m, emptied.Total);
    }

    [Fact]
    public async Task Patch_WithANullRowCollection_IsRejected()
    {
        var (order, etag) = await CreateOrderAsync(("A", 1, 10m));

        var response = await SendAsync(HttpMethod.Patch, $"/orders/{order.Id}", new { orderItems = (object?)null }, ifMatch: etag);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rows_AreValidatedAndErrorsPointAtTheRow()
    {
        var create = await SendAsync(HttpMethod.Post, "/orders", new
        {
            customerName = "Ada",
            orderItems = new object[]
            {
                new { productName = "Fine", quantity = 1, unitPrice = 1 },
                new { productName = new string('x', 201), quantity = 0, unitPrice = 1 },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        var problem = await create.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(["orderItems[1].productName", "orderItems[1].quantity"], problem.Errors.Keys.Order());
        Assert.Equal(["The Quantity field must be between 1 and 10000."], problem.Errors["orderItems[1].quantity"]);

        var (order, etag) = await CreateOrderAsync(("A", 1, 10m));
        var put = await SendAsync(
            HttpMethod.Put, $"/orders/{order.Id}",
            new { customerName = "Ada", orderItems = new object[] { new { productName = "Bad", quantity = -1, unitPrice = 1 } } },
            ifMatch: etag);
        var patch = await SendAsync(
            HttpMethod.Patch, $"/orders/{order.Id}",
            new { orderItems = new object[] { new { productName = "Bad", quantity = -1, unitPrice = 1 } } },
            ifMatch: etag);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
    }

    [Fact]
    public async Task ChangingOnlyTheRows_StillAdvancesTheVersionAndTheModificationTime()
    {
        var (order, etag) = await CreateOrderAsync(("A", 1, 10m));

        var changed = await SendAsync(
            HttpMethod.Patch, $"/orders/{order.Id}",
            new { orderItems = new object[] { new { productName = "B", quantity = 1, unitPrice = 10 } } },
            ifMatch: etag);
        var withStaleVersion = await SendAsync(HttpMethod.Patch, $"/orders/{order.Id}", new { customerName = "Late" }, ifMatch: etag);

        var updated = await ReadAsync<OrderResponse>(changed);

        Assert.NotEqual(etag, changed.Headers.ETag?.Tag);
        Assert.NotNull(updated.UpdatedAt);
        Assert.Equal(HttpStatusCode.PreconditionFailed, withStaleVersion.StatusCode);
    }

    [Fact]
    public async Task Rows_AreNotReachableByAnotherTenantAndGoWithTheRecord()
    {
        var (order, etag) = await CreateOrderAsync(("A", 1, 10m), ("B", 1, 10m));

        var seenByOther = await SendAsync(HttpMethod.Get, $"/orders/{order.Id}", tenant: Guid.NewGuid());
        var rowsBefore = await CountRowsAsync(order.Id);
        var deleted = await SendAsync(HttpMethod.Delete, $"/orders/{order.Id}", ifMatch: etag);
        var rowsAfter = await CountRowsAsync(order.Id);

        Assert.Equal(HttpStatusCode.NotFound, seenByOther.StatusCode);
        Assert.Equal(2, rowsBefore);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, rowsAfter);
    }

    [Fact]
    public async Task SubresourceDetail_HasItsOwnEndpointsBelowTheOwner()
    {
        var (order, _) = await CreateOrderAsync(("A", 1, 10m));
        var url = $"/orders/{order.Id}/order-notes";

        var created = await SendAsync(HttpMethod.Post, url, new { text = "Call first" });
        var note = await ReadAsync<OrderNoteResponse>(created);
        var list = await ReadAsync<PagedResult<OrderNoteResponse>>(await SendAsync(HttpMethod.Get, url));
        var edited = await ReadAsync<OrderNoteResponse>(
            await SendAsync(HttpMethod.Patch, $"{url}/{note.Id}", new { text = "Call twice" }));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"{url}/{note.Id}", created.Headers.Location?.OriginalString);
        Assert.Equal(order.Id, note.OrderId);
        Assert.Equal([note.Id], list.Items.Select(item => item.Id));
        Assert.Equal("Call twice", edited.Text);
    }

    [Fact]
    public async Task SubresourceDetail_InheritsTheVisibilityOfItsOwner()
    {
        var (order, etag) = await CreateOrderAsync(("A", 1, 10m));
        var url = $"/orders/{order.Id}/order-notes";
        var note = await ReadAsync<OrderNoteResponse>(await SendAsync(HttpMethod.Post, url, new { text = "Private" }));
        var intruder = Guid.NewGuid();

        var list = await ReadAsync<PagedResult<OrderNoteResponse>>(await SendAsync(HttpMethod.Get, url, tenant: intruder));
        var read = await SendAsync(HttpMethod.Get, $"{url}/{note.Id}", tenant: intruder);
        var planted = await SendAsync(HttpMethod.Post, url, new { text = "Planted" }, tenant: intruder);
        var withoutTenant = await _client.GetAsync(url, CancellationToken);

        Assert.Empty(list.Items);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, planted.StatusCode);

        // Notların uçları siparişin grubuna eşlendiği için onun kiracı zorunluluğunu da devralır.
        Assert.Equal(HttpStatusCode.Forbidden, withoutTenant.StatusCode);

        await SendAsync(HttpMethod.Delete, $"/orders/{order.Id}", ifMatch: etag);
        var notes = -1;
        await fixture.WithDbContextAsync(async db =>
            notes = await db.OrderNotes.IgnoreQueryFilters().CountAsync(row => row.OrderId == order.Id, CancellationToken));

        Assert.Equal(0, notes);
    }

    private async Task<(OrderResponse Order, string ETag)> CreateOrderAsync(
        params (string ProductName, int Quantity, decimal UnitPrice)[] rows)
    {
        var response = await SendAsync(HttpMethod.Post, "/orders", new
        {
            customerName = "Ada",
            orderItems = rows.Select(row => new { productName = row.ProductName, quantity = row.Quantity, unitPrice = row.UnitPrice }),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<OrderResponse>(response), response.Headers.ETag!.Tag);
    }

    private async Task<int> CountRowsAsync(Guid orderId)
    {
        var count = -1;
        await fixture.WithDbContextAsync(async db =>
            count = await db.OrderItems.IgnoreQueryFilters().CountAsync(row => row.OrderId == orderId, CancellationToken));

        return count;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string? ifMatch = null, Guid? tenant = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, (tenant ?? _tenant).ToString());

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
