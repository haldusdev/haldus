using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Sales;

namespace Haldus.IntegrationTests;

/// <summary>
/// Araştırmadaki entity tablosundan, başka testlerin uçtan uca denemediği birleşimler: satırlı ve kayan ömürlü
/// sepet, hareketleri eklenen ödeme, iptal edilebilen iade ve ürün altındaki yanıtlı yorumlar.
/// </summary>
[Collection(ShopCollection.Name)]
public sealed class TaxonomyEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cart_KeepsItsRowsAndRestartsItsLifetimeWhenTheyChange()
    {
        var created = await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Post, "/carts", new
        {
            note = "Gift",
            cartItems = new object[] { new { productName = "Keyboard", quantity = 1 }, new { productName = "Mouse", quantity = 2 } },
        }));

        Assert.Equal(["Keyboard", "Mouse"], created.CartItems.Select(item => item.ProductName));

        await Task.Delay(20, CancellationToken);

        var updated = await ReadAsync<CartResponse>(await SendAsync(HttpMethod.Put, $"/carts/{created.Id}", new
        {
            note = "Gift",
            cartItems = new object[]
            {
                new { id = created.CartItems[1].Id, productName = "Mouse", quantity = 3 },
                new { productName = "Cable", quantity = 1 },
            },
        }));

        // Satırlar eşitlendi ve yalnızca satırları değişen sepetin ömrü de yeniden başladı.
        Assert.Equal(["Mouse", "Cable"], updated.CartItems.Select(item => item.ProductName));
        Assert.Equal(3, updated.CartItems[0].Quantity);
        Assert.True(updated.ExpiresAt > created.ExpiresAt, $"{updated.ExpiresAt} should be later than {created.ExpiresAt}.");
    }

    [Fact]
    public async Task Payment_IsCapturedOrVoidedButNeverDeletedAndItsTransactionsOnlyGrow()
    {
        var order = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada" }));
        var payment = await ReadAsync<PaymentResponse>(
            await SendAsync(HttpMethod.Post, "/payments", new { orderId = order.Id, amount = 240.5 }));
        var transactions = $"/payments/{payment.Id}/payment-transactions";

        Assert.Equal(PaymentStatus.RequiresCapture, payment.Status);

        // Hareketler eklenir; değiştirilemez ve silinemez.
        var transaction = await ReadAsync<PaymentTransactionResponse>(
            await SendAsync(HttpMethod.Post, transactions, new { kind = "authorize", amount = 240.5 }));
        var rewrite = await SendAsync(HttpMethod.Put, $"{transactions}/{transaction.Id}", new { kind = "x", amount = 1 });
        var erase = await SendAsync(HttpMethod.Delete, $"{transactions}/{transaction.Id}");

        Assert.Equal(payment.Id, transaction.PaymentId);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, rewrite.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, erase.StatusCode);

        var captured = await ReadAsync<PaymentResponse>(await SendAsync(HttpMethod.Post, $"/payments/{payment.Id}/capture"));
        Assert.Equal(PaymentStatus.Captured, captured.Status);

        // Tahsil edilen ödeme artık düzenlenemez, iptal edilemez ve hiçbir zaman silinemez.
        var edit = await SendAsync(HttpMethod.Patch, $"/payments/{payment.Id}", new { amount = 1 });
        var voided = await SendAsync(HttpMethod.Post, $"/payments/{payment.Id}/void");
        var delete = await SendAsync(HttpMethod.Delete, $"/payments/{payment.Id}");

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, voided.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);

        // Hareketler, ödemesi kiracıya ait olduğu için başka bir kiracıya görünmez.
        var intruder = await SendAsync(HttpMethod.Post, transactions, new { kind = "refund", amount = 1 }, tenant: Guid.NewGuid());
        var list = await ReadAsync<PagedResult<PaymentTransactionResponse>>(await SendAsync(HttpMethod.Get, transactions));

        Assert.Equal(HttpStatusCode.NotFound, intruder.StatusCode);
        Assert.Single(list.Items);
    }

    [Fact]
    public async Task Refund_CannotBeChangedButCanBeCancelledOnce()
    {
        var order = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Ada" }));
        var refund = await ReadAsync<RefundResponse>(await SendAsync(
            HttpMethod.Post, "/refunds", new { orderId = order.Id, amount = 10 }, idempotencyKey: Guid.NewGuid().ToString()));

        Assert.Equal(RefundStatus.Pending, refund.Status);

        // Anahtar zorunluluğu iadenin geçişini de kapsar.
        var withoutKey = await SendAsync(HttpMethod.Post, $"/refunds/{refund.Id}/cancel");
        var cancelled = await ReadAsync<RefundResponse>(await SendAsync(
            HttpMethod.Post, $"/refunds/{refund.Id}/cancel", idempotencyKey: Guid.NewGuid().ToString()));
        var again = await SendAsync(HttpMethod.Post, $"/refunds/{refund.Id}/cancel", idempotencyKey: Guid.NewGuid().ToString());
        var edit = await SendAsync(HttpMethod.Put, $"/refunds/{refund.Id}", new { orderId = order.Id, amount = 99 });

        Assert.Equal(HttpStatusCode.BadRequest, withoutKey.StatusCode);
        Assert.Equal(RefundStatus.Cancelled, cancelled.Status);
        Assert.Equal(refund.Number, cancelled.Number);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, edit.StatusCode);
    }

    [Fact]
    public async Task Reviews_AreThreadedBelowTheirProductAndCanBeRestored()
    {
        var product = await CreateProductAsync();
        var other = await CreateProductAsync();
        var url = $"/products/{product.Id}/product-reviews";

        var root = await ReadAsync<ProductReviewResponse>(await SendAsync(HttpMethod.Post, url, new { text = "Great", rating = 5 }));
        var reply = await ReadAsync<ProductReviewResponse>(
            await SendAsync(HttpMethod.Post, url, new { text = "Agreed", rating = 5, parentId = root.Id }));

        Assert.Equal(product.Id, reply.ProductId);
        Assert.Equal(root.Id, reply.ParentId);

        // Yanıt, aynı ürünün bir yorumuna verilmelidir; bir yorum kendi yanıtının altına da taşınamaz.
        var foreign = await SendAsync(
            HttpMethod.Post, $"/products/{other.Id}/product-reviews", new { text = "Lost", rating = 1, parentId = root.Id });
        var cycle = await SendAsync(HttpMethod.Put, $"{url}/{root.Id}", new { text = "Great", rating = 5, parentId = reply.Id });

        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);

        var roots = await ReadAsync<PagedResult<ProductReviewResponse>>(await SendAsync(HttpMethod.Get, $"{url}?roots=true"));
        var replies = await ReadAsync<PagedResult<ProductReviewResponse>>(await SendAsync(HttpMethod.Get, $"{url}?parentId={root.Id}"));

        Assert.Equal([root.Id], roots.Items.Select(review => review.Id));
        Assert.Equal([reply.Id], replies.Items.Select(review => review.Id));

        // Silinen yorum gizlenir ve geri yüklenebilir.
        var deleted = await SendAsync(HttpMethod.Delete, $"{url}/{reply.Id}");
        var hidden = await SendAsync(HttpMethod.Get, $"{url}/{reply.Id}");
        var restored = await SendAsync(HttpMethod.Post, $"{url}/{reply.Id}/restore");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    private async Task<ProductResponse> CreateProductAsync() =>
        await ReadAsync<ProductResponse>(
            await SendAsync(HttpMethod.Post, "/products", new { name = $"Reviewed {Guid.NewGuid():N}", price = 10 }));

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, Guid? tenant = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(DemoIdentity.TenantHeader, (tenant ?? _tenant).ToString());

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
