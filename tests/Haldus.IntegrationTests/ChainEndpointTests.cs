using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Sales;
using Haldus.Sample.Shop.Features.Stock;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

/// <summary>Kayıtlar arası zincir: ters kayıt, yerine geçme, başka bir kayıttan üretme ve türetme.</summary>
[Collection(ShopCollection.Name)]
public sealed class ChainEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ── Ters kayıt ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reversible_CreatesTheOppositeRecordAndLinksTheTwo()
    {
        var product = Guid.NewGuid();
        var movement = await CreateMovementAsync(product, 10, "Delivery");

        var response = await SendAsync(HttpMethod.Post, $"/stock-movements/{movement.Id}/reverse");
        var reversal = await ReadAsync<StockMovementResponse>(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/stock-movements/{reversal.Id}", response.Headers.Location!.OriginalString);

        // Ters kayıt asıl kaydın kopyasıdır; entity'nin sözleşmesi yalnızca miktarın yönünü çevirdi.
        Assert.Equal((product, -10, "Delivery"), (reversal.ProductId, reversal.Quantity, reversal.Reason));
        Assert.Equal(movement.Id, reversal.ReversesId);
        Assert.Null(reversal.ReversedById);

        var original = await ReadAsync<StockMovementResponse>(await SendAsync(HttpMethod.Get, $"/stock-movements/{movement.Id}"));
        Assert.Equal(reversal.Id, original.ReversedById);
        Assert.Equal(10, original.Quantity);
    }

    [Fact]
    public async Task Reversible_RequestCanOverrideFieldsOfTheReversal()
    {
        var movement = await CreateMovementAsync(Guid.NewGuid(), 4, "Delivery");

        var reversal = await ReadAsync<StockMovementResponse>(
            await SendAsync(HttpMethod.Post, $"/stock-movements/{movement.Id}/reverse", new { reason = "Miscounted" }));

        Assert.Equal((-4, "Miscounted"), (reversal.Quantity, reversal.Reason));
    }

    [Fact]
    public async Task Reversible_ARecordIsReversedOnlyOnceAndAReversalNeverIs()
    {
        var movement = await CreateMovementAsync(Guid.NewGuid(), 7, null);
        var reversal = await ReadAsync<StockMovementResponse>(await SendAsync(HttpMethod.Post, $"/stock-movements/{movement.Id}/reverse"));

        var again = await SendAsync(HttpMethod.Post, $"/stock-movements/{movement.Id}/reverse");
        var ofReversal = await SendAsync(HttpMethod.Post, $"/stock-movements/{reversal.Id}/reverse");
        var turkish = await SendAsync(HttpMethod.Post, $"/stock-movements/{movement.Id}/reverse", language: "tr");
        var missing = await SendAsync(HttpMethod.Post, $"/stock-movements/{Guid.NewGuid()}/reverse");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, ofReversal.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        Assert.Equal(
            $"StockMovement '{movement.Id}' has been reversed and can no longer be changed or reversed again.",
            (await again.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Detail);
        Assert.Equal(
            $"StockMovement '{reversal.Id}' is itself a reversal and cannot be reversed.",
            (await ofReversal.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Detail);
        Assert.Equal("Kayıt artık değiştirilemez", (await turkish.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Title);
    }

    [Fact]
    public async Task Reversible_ReversedConcurrently_ProducesExactlyOneReversal()
    {
        var movement = await CreateMovementAsync(Guid.NewGuid(), 3, null);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => SendAsync(HttpMethod.Post, $"/stock-movements/{movement.Id}/reverse")));

        // Kontrolü birlikte geçen istekleri benzersiz indeks durdurur; o da 500 değil çakışmadır.
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));

        await fixture.WithDbContextAsync(async context =>
            Assert.Equal(1, await context.StockMovements.CountAsync(item => item.ReversesId == movement.Id, CancellationToken)));
    }

    // ── Yerine geçme ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Supersedes_ReplacesTheRecordWithANewOneThatPointsBack()
    {
        var quote = await SentQuoteAsync("Ada", 1000m, "30 days");

        var response = await SendAsync(HttpMethod.Post, $"/quotes/{quote.Id}/supersede", new { amount = 900m });
        var revised = await ReadAsync<QuoteResponse>(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/quotes/{revised.Id}", response.Headers.Location!.OriginalString);

        // Yeni kayıt eskisinin kopyasıdır; istekte gönderilen alan onun yerine geçti. Durumu baştan başlar.
        Assert.Equal(("Ada", 900m, "30 days"), (revised.CustomerName, revised.Amount, revised.Terms));
        Assert.Equal((quote.Id, QuoteStatus.Draft), (revised.SupersedesId, revised.Status));

        var old = await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Get, $"/quotes/{quote.Id}"));
        Assert.Equal((revised.Id, 1000m, QuoteStatus.Sent), (old.SupersededById, old.Amount, old.Status));
    }

    [Fact]
    public async Task Supersedes_IsAllowedOnlyInTheDeclaredStates()
    {
        var draft = await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Post, "/quotes", new { customerName = "Ada", amount = 1000m }));

        // Yerine geçme, kaydın düzenlenemediği durumlarda da çalışan bir yazmadır; hangi durumlarda yapılabileceği
        // açıkça bildirilir ve başka bir durumda reddedilir.
        var early = await SendAsync(HttpMethod.Post, $"/quotes/{draft.Id}/supersede", new { amount = 1m });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);

        await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Post, $"/quotes/{draft.Id}/send"));
        await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Post, $"/quotes/{draft.Id}/withdraw"));

        var late = await SendAsync(HttpMethod.Post, $"/quotes/{draft.Id}/supersede", new { amount = 1m });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task Supersedes_TheOldRecordIsClosedForEveryWrite()
    {
        var quote = await SentQuoteAsync("Ada", 1000m, null);
        var revised = await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Post, $"/quotes/{quote.Id}/supersede", new { amount = 900m }));

        var patched = await SendAsync(HttpMethod.Patch, $"/quotes/{quote.Id}", new { amount = 1m });
        var again = await SendAsync(HttpMethod.Post, $"/quotes/{quote.Id}/supersede", new { amount = 1m });

        // Geçişler de bir yazmadır; kapanmış kayıtta çalışmazlar.
        var withdrawn = await SendAsync(HttpMethod.Post, $"/quotes/{quote.Id}/withdraw");

        Assert.Equal(HttpStatusCode.Conflict, patched.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, withdrawn.StatusCode);
        Assert.Equal(
            $"Quote '{quote.Id}' has been superseded and can no longer be changed or superseded again.",
            (await withdrawn.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Detail);
        Assert.Equal(QuoteStatus.Sent, (await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Get, $"/quotes/{quote.Id}"))).Status);

        // Geçerli kayıt yeni bir taslaktır: düzenlenir, gönderilir ve onun da yerine geçilebilir; zincir uzar.
        var edited = await SendAsync(HttpMethod.Patch, $"/quotes/{revised.Id}", new { terms = "Net 15" });
        await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Post, $"/quotes/{revised.Id}/send"));
        var third = await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Post, $"/quotes/{revised.Id}/supersede", new { amount = 800m }));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal((revised.Id, "Net 15"), (third.SupersedesId, third.Terms));
    }

    [Fact]
    public async Task Supersedes_ARecordThatIsPartOfAPairCannotBeDeleted()
    {
        var standalone = await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Post, "/quotes", new { customerName = "Bob", amount = 5m }));
        var quote = await SentQuoteAsync("Ada", 1000m, null);
        var revised = await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Post, $"/quotes/{quote.Id}/supersede", new { amount = 900m }));

        // Silinseydi öteki uç hiçbir şeye işaret etmez ya da sonsuza dek kapalı kalırdı.
        var old = await SendAsync(HttpMethod.Delete, $"/quotes/{quote.Id}");
        var current = await SendAsync(HttpMethod.Delete, $"/quotes/{revised.Id}");
        var free = await SendAsync(HttpMethod.Delete, $"/quotes/{standalone.Id}");

        Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, current.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, free.StatusCode);
        Assert.Equal(quote.Id, (await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Get, $"/quotes/{revised.Id}"))).SupersedesId);
    }

    private async Task<QuoteResponse> SentQuoteAsync(string customerName, decimal amount, string? terms)
    {
        var quote = await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Post, "/quotes", new { customerName, amount, terms }));
        return await ReadAsync<QuoteResponse>(await SendAsync(HttpMethod.Post, $"/quotes/{quote.Id}/send"));
    }
    // ── Başka bir kayıttan üretme ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatedFrom_BuildsTheRecordFromItsSourceAndKeepsTheLink()
    {
        var cart = await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Post, "/carts", new
        {
            note = "Ada",
            cartItems = new[] { new { productName = "Keyboard", quantity = 2 }, new { productName = "Mouse", quantity = 1 } },
        }));

        var response = await SendAsync(HttpMethod.Post, $"/orders/from-cart/{cart.Id}");
        var order = await ReadAsync<OrderResponse>(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/orders/{order.Id}", response.Headers.Location!.OriginalString);
        Assert.NotNull(response.Headers.ETag);

        // Bağ tutulur; içeriği entity'nin sözleşmesi doldurdu ve gerisi yeni bir siparişte olduğu gibi atandı.
        Assert.Equal(cart.Id, order.CartId);
        Assert.Equal("Ada", order.CustomerName);
        Assert.Equal([("Keyboard", 2), ("Mouse", 1)], order.OrderItems.Select(item => (item.ProductName, item.Quantity)));
        Assert.Equal((2, OrderStatus.Draft), (order.ItemCount, order.Status));
        Assert.All(order.OrderItems, item => Assert.NotEqual(Guid.Empty, item.Id));

        // İstekte gönderilen alan, kaynaktan gelenin yerine geçer.
        var named = await ReadAsync<OrderResponse>(
            await SendAsync(HttpMethod.Post, $"/orders/from-cart/{cart.Id}", new { customerName = "Grace" }));
        var missing = await SendAsync(HttpMethod.Post, $"/orders/from-cart/{Guid.NewGuid()}");

        Assert.Equal("Grace", named.CustomerName);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task CreatedFrom_TheLinkIsSetOnlyByTheFromEndpoint()
    {
        var cart = await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Post, "/carts", new { note = "Ada" }));

        // Kaynağa işaret eden alan istek gövdesinden yazılamaz.
        var created = await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Direct", cartId = cart.Id });
        var direct = await ReadAsync<OrderResponse>(created);
        Assert.Null(direct.CartId);

        var patched = await ReadAsync<OrderResponse>(await SendAsync(
            HttpMethod.Patch, $"/orders/{direct.Id}", new { cartId = cart.Id }, ifMatch: created.Headers.ETag!.Tag));
        Assert.Null(patched.CartId);

        // Kaynak silinince bağ boşalır (onDelete: SetNull); sipariş kalır.
        var linked = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Post, $"/orders/from-cart/{cart.Id}"));
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/carts/{cart.Id}")).StatusCode);

        var orphaned = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"/orders/{linked.Id}"));
        Assert.Null(orphaned.CartId);
    }

    [Fact]
    public async Task CreatedFrom_OnATransition_CreatesTheRecordTogetherWithIt()
    {
        var order = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada" }));
        var captured = await ReadAsync<PaymentResponse>(await SendAsync(HttpMethod.Post, "/payments", new { orderId = order.Id, amount = 240.5 }));
        var voided = await ReadAsync<PaymentResponse>(await SendAsync(HttpMethod.Post, "/payments", new { orderId = order.Id, amount = 10 }));

        Assert.Empty((await ReadAsync<PagedResult<ReceiptResponse>>(await SendAsync(HttpMethod.Get, "/receipts"))).Items);

        await ReadAsync<PaymentResponse>(await SendAsync(HttpMethod.Post, $"/payments/{captured.Id}/capture"));
        await ReadAsync<PaymentResponse>(await SendAsync(HttpMethod.Post, $"/payments/{voided.Id}/void"));

        // Makbuz yalnızca tahsilat geçişiyle doğar; tutarı, aynı adı taşıdığı için ödemeden kopyalandı.
        var receipt = Assert.Single((await ReadAsync<PagedResult<ReceiptResponse>>(await SendAsync(HttpMethod.Get, "/receipts"))).Items);
        Assert.Equal((captured.Id, 240.5m), (receipt.PaymentId, receipt.Amount));

        // Geçişin koşullarını atlayarak aynı kaydı üretmenin bir yolu yoktur.
        var direct = await SendAsync(HttpMethod.Post, "/receipts", new { amount = 1 });
        var explicitly = await SendAsync(HttpMethod.Post, $"/receipts/from-payment/{voided.Id}");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, direct.StatusCode);
        Assert.Contains(explicitly.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });

        // Makbuz da kiracıya aittir.
        var foreign = await ReadAsync<PagedResult<ReceiptResponse>>(await SendAsync(HttpMethod.Get, "/receipts", tenant: Guid.NewGuid()));
        Assert.Empty(foreign.Items);
    }

    // ── Türetme ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DerivedFrom_KeepsTheDerivedRecordInStepWithItsSources()
    {
        var product = Guid.NewGuid();

        var delivery = await CreateMovementAsync(product, 10, "Delivery");
        await CreateMovementAsync(product, -3, "Sale");
        Assert.Equal(7, await StockLevelAsync(product));

        // Ters hareket de bir harekettir; seviye onunla da güncellenir.
        await ReadAsync<StockMovementResponse>(await SendAsync(HttpMethod.Post, $"/stock-movements/{delivery.Id}/reverse"));
        Assert.Equal(-3, await StockLevelAsync(product));

        // Reddedilen bir hareket seviyeyi değiştirmez.
        var invalid = await SendAsync(HttpMethod.Post, "/stock-movements", new { productId = product, quantity = 5_000_000 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(-3, await StockLevelAsync(product));
    }

    [Fact]
    public async Task DerivedFrom_ConcurrentSources_DoNotOverwriteEachOther()
    {
        var product = Guid.NewGuid();

        // İzdüşüm seviyeyi okuyup artırır; sıraya girmeseler aynı değeri okuyan iki hareketten biri kaybolurdu.
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => SendAsync(
            HttpMethod.Post, "/stock-movements", new { productId = product, quantity = 1 })));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Equal(12, await StockLevelAsync(product));

        await fixture.WithDbContextAsync(async context =>
            Assert.Equal(1, await context.StockLevels.CountAsync(item => item.ProductId == product, CancellationToken)));
    }

    private async Task<int?> StockLevelAsync(Guid productId)
    {
        int? quantity = null;

        await fixture.WithDbContextAsync(async context =>
            quantity = await context.StockLevels
                .Where(item => item.ProductId == productId)
                .Select(item => (int?)item.Quantity)
                .SingleOrDefaultAsync(CancellationToken));

        return quantity;
    }

    private async Task<StockMovementResponse> CreateMovementAsync(Guid productId, int quantity, string? reason) =>
        await ReadAsync<StockMovementResponse>(await SendAsync(HttpMethod.Post, "/stock-movements", new { productId, quantity, reason }));

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string? language = null, string? ifMatch = null, Guid? tenant = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, (tenant ?? _tenant).ToString());

        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
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
