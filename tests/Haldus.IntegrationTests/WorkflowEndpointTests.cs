using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

[Collection(ShopCollection.Name)]
public sealed partial class WorkflowEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_StartsInTheInitialStateWhateverTheBodySays()
    {
        var response = await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada", status = "Shipped", number = "ORD-1" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Durum yanıtta sayı olarak değil adıyla görünür.
        Assert.Contains("\"status\":\"Draft\"", await response.Content.ReadAsStringAsync(CancellationToken));

        var order = await ReadAsync<OrderResponse>(response);
        Assert.Equal(OrderStatus.Draft, order.Status);
        Assert.Null(order.Number);
    }

    [Fact]
    public async Task Transition_MovesTheRecordAndAssignsItsNumber()
    {
        var (first, firstTag) = await CreateOrderAsync();
        var (second, secondTag) = await CreateOrderAsync();

        var response = await SendAsync(HttpMethod.Post, $"/orders/{first.Id}/place", new { note = "Leave at the door" }, ifMatch: firstTag);
        var placed = await ReadAsync<OrderResponse>(response);
        var next = await ReadAsync<OrderResponse>(
            await SendAsync(HttpMethod.Post, $"/orders/{second.Id}/place", new { }, ifMatch: secondTag));

        Assert.Equal(OrderStatus.Placed, placed.Status);
        Assert.Equal("Leave at the door", placed.PlacementNote);
        Assert.NotEqual(firstTag, response.Headers.ETag!.Tag);

        // Sayaç kiracı ve yıl başınadır; bu kiracının ilk iki siparişi 1 ve 2'dir.
        Assert.Matches(NumberPattern(), placed.Number);
        Assert.EndsWith("-000001", placed.Number, StringComparison.Ordinal);
        Assert.EndsWith("-000002", next.Number, StringComparison.Ordinal);

        var persisted = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"/orders/{first.Id}"));
        Assert.Equal(OrderStatus.Placed, persisted.Status);
        Assert.Equal(placed.Number, persisted.Number);
    }

    [Fact]
    public async Task Transition_WithoutABody_OnlyChangesTheState()
    {
        var (order, tag) = await PlaceOrderAsync();

        var shipped = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/ship", ifMatch: tag));

        Assert.Equal(OrderStatus.Shipped, shipped.Status);
        Assert.Equal(order.Number, shipped.Number);
    }

    [Fact]
    public async Task Transition_FromAStateItDoesNotLeave_IsAConflict()
    {
        var (order, tag) = await CreateOrderAsync();

        var response = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/ship", ifMatch: tag);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken);
        Assert.Equal("'ship' is not allowed while the record is in the 'Draft' state.", problem!.Detail);

        var unchanged = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"/orders/{order.Id}"));
        Assert.Equal(OrderStatus.Draft, unchanged.Status);
    }

    [Fact]
    public async Task Transition_ChecksTheVersionLikeEveryOtherWrite()
    {
        var (order, tag) = await CreateOrderAsync();

        var missing = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/place", new { });
        await SendAsync(HttpMethod.Patch, $"/orders/{order.Id}", new { customerName = "Grace" }, ifMatch: tag);
        var stale = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/place", new { }, ifMatch: tag);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task Transition_RejectedByItsHandler_SavesNothingAndBurnsNoNumber()
    {
        var (empty, emptyTag) = await CreateOrderAsync(withItems: false);

        var english = await SendAsync(HttpMethod.Post, $"/orders/{empty.Id}/place", new { }, ifMatch: emptyTag);
        var turkish = await SendAsync(HttpMethod.Post, $"/orders/{empty.Id}/place", new { }, ifMatch: emptyTag, language: "tr");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, english.StatusCode);
        Assert.Equal(
            "An order without items cannot be placed.",
            (await english.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Detail);
        Assert.Equal(
            "Satırı olmayan bir sipariş verilemez.",
            (await turkish.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Detail);

        var unchanged = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"/orders/{empty.Id}"));
        Assert.Equal(OrderStatus.Draft, unchanged.Status);
        Assert.Null(unchanged.Number);

        // Reddedilen geçiş numara harcamadı: verilen ilk sipariş yine 1 numarayı alır.
        var (placed, _) = await PlaceOrderAsync();
        Assert.EndsWith("-000001", placed.Number, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transition_WhoseRequestAppliesItself_IsValidatedAndApplied()
    {
        var (order, tag) = await PlaceOrderAsync();

        var invalid = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/cancel", new { reason = "" }, ifMatch: tag);
        var cancelled = await ReadAsync<OrderResponse>(
            await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/cancel", new { reason = "Changed my mind" }, ifMatch: tag));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Equal("Changed my mind", cancelled.CancellationReason);
    }

    [Fact]
    public async Task Transition_WithALambda_RunsItAndKeepsTheNumberAlreadyAssigned()
    {
        var (order, tag) = await PlaceOrderAsync();

        var cancel = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/cancel", new { reason = "Oops" }, ifMatch: tag);
        var reopen = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/reopen", new { }, ifMatch: cancel.Headers.ETag!.Tag);
        var reopened = await ReadAsync<OrderResponse>(reopen);
        var placedAgain = await ReadAsync<OrderResponse>(
            await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/place", new { }, ifMatch: reopen.Headers.ETag!.Tag));

        Assert.Equal(OrderStatus.Draft, reopened.Status);
        Assert.Null(reopened.CancellationReason);

        // Numara bir kez atanır; aynı duruma yeniden girmek yeni numara vermez.
        Assert.Equal(order.Number, placedAgain.Number);
    }

    [Fact]
    public async Task EditingAndDeleting_AreOnlyAllowedInTheDeclaredStates()
    {
        var (order, tag) = await PlaceOrderAsync();

        var put = await SendAsync(
            HttpMethod.Put, $"/orders/{order.Id}", new { customerName = "Grace", orderItems = Array.Empty<object>() }, ifMatch: tag);
        var patch = await SendAsync(HttpMethod.Patch, $"/orders/{order.Id}", new { customerName = "Grace" }, ifMatch: tag);
        var delete = await SendAsync(HttpMethod.Delete, $"/orders/{order.Id}", ifMatch: tag);

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(
            "'update' is not allowed while the record is in the 'Placed' state.",
            (await patch.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Detail);

        var unchanged = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"/orders/{order.Id}"));
        Assert.Equal("Ada", unchanged.CustomerName);
        Assert.Single(unchanged.OrderItems);

        // İptal edilen sipariş silinebilir durumdadır.
        var cancel = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/cancel", new { reason = "Oops" }, ifMatch: tag);
        var deleted = await SendAsync(HttpMethod.Delete, $"/orders/{order.Id}", ifMatch: cancel.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task SubresourceDetail_CanOnlyBeWrittenWhileItsOwnerIsEditable()
    {
        var (order, tag) = await CreateOrderAsync();
        var url = $"/orders/{order.Id}/order-notes";
        var note = await ReadAsync<OrderNoteResponse>(await SendAsync(HttpMethod.Post, url, new { text = "While draft" }));

        await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/place", new { }, ifMatch: tag);

        var create = await SendAsync(HttpMethod.Post, url, new { text = "Too late" });
        var update = await SendAsync(HttpMethod.Put, $"{url}/{note.Id}", new { text = "Edited" });
        var delete = await SendAsync(HttpMethod.Delete, $"{url}/{note.Id}");
        var read = await ReadAsync<PagedResult<OrderNoteResponse>>(await SendAsync(HttpMethod.Get, url));

        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        // Okumak her durumda serbesttir.
        Assert.Equal("While draft", Assert.Single(read.Items).Text);
    }

    [Fact]
    public async Task List_CanBeFilteredByState()
    {
        var (draft, _) = await CreateOrderAsync();
        var (placed, _) = await PlaceOrderAsync();

        var placedOnly = await ReadAsync<PagedResult<OrderResponse>>(await SendAsync(HttpMethod.Get, "/orders?status=Placed"));
        var all = await ReadAsync<PagedResult<OrderResponse>>(await SendAsync(HttpMethod.Get, "/orders"));

        Assert.Equal([placed.Id], placedOnly.Items.Select(order => order.Id));
        Assert.Equal(new[] { draft.Id, placed.Id }.Order(), all.Items.Select(order => order.Id).Order());
    }

    [Fact]
    public async Task Transition_IsNotReachableByAnotherTenant()
    {
        var (order, tag) = await CreateOrderAsync();

        var response = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/place", new { }, ifMatch: tag, tenant: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Numbers_AreGaplessEvenWhenOrdersArePlacedAtTheSameTime()
    {
        const int Count = 6;
        var orders = new List<(OrderResponse Order, string ETag)>();

        for (var index = 0; index < Count; index++)
        {
            orders.Add(await CreateOrderAsync());
        }

        var placed = await Task.WhenAll(orders.Select(async entry => await ReadAsync<OrderResponse>(
            await SendAsync(HttpMethod.Post, $"/orders/{entry.Order.Id}/place", new { }, ifMatch: entry.ETag))));

        var sequences = placed.Select(order => int.Parse(order.Number![^6..], System.Globalization.CultureInfo.InvariantCulture)).Order();
        Assert.Equal(Enumerable.Range(1, Count), sequences);

        // Başka bir kiracının sayacı ayrıdır.
        var other = Guid.NewGuid();
        var created = await SendAsync(HttpMethod.Post, "/orders", NewOrder(withItems: true), tenant: other);
        var otherOrder = await ReadAsync<OrderResponse>(created);
        var otherPlaced = await ReadAsync<OrderResponse>(await SendAsync(
            HttpMethod.Post, $"/orders/{otherOrder.Id}/place", new { }, ifMatch: created.Headers.ETag!.Tag, tenant: other));

        Assert.EndsWith("-000001", otherPlaced.Number, StringComparison.Ordinal);

        var counters = 0;
        await fixture.WithDbContextAsync(async db => counters = await db.Set<HaldusCounter>()
            .CountAsync(counter => counter.Scope.Contains(_tenant.ToString("N")), CancellationToken));
        Assert.Equal(1, counters);
    }

    [GeneratedRegex(@"^ORD-\d{4}-\d{6}$")]
    private static partial Regex NumberPattern();

    private static object NewOrder(bool withItems) => new
    {
        customerName = "Ada",
        orderItems = withItems
            ? new object[] { new { productName = "Keyboard", quantity = 1, unitPrice = 100 } }
            : [],
    };

    private async Task<(OrderResponse Order, string ETag)> CreateOrderAsync(bool withItems = true)
    {
        var response = await SendAsync(HttpMethod.Post, "/orders", NewOrder(withItems));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<OrderResponse>(response), response.Headers.ETag!.Tag);
    }

    private async Task<(OrderResponse Order, string ETag)> PlaceOrderAsync()
    {
        var (order, tag) = await CreateOrderAsync();
        var response = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/place", new { }, ifMatch: tag);

        return (await ReadAsync<OrderResponse>(response), response.Headers.ETag!.Tag);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string? ifMatch = null, Guid? tenant = null, string? language = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, (tenant ?? _tenant).ToString());

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

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
