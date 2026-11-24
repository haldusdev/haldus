using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Modular.Catalog;
using Haldus.Sample.Modular.Host;
using Haldus.Sample.Modular.Sales;
using Haldus.Sample.Modular.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus.IntegrationTests;

/// <summary>Modüler yapı ve iki bağlam modu: ortak bağlam ile modül başına bağlam aynı uygulamada.</summary>
[Collection(ModularCollection.Name)]
public sealed class ModularEndpointTests(ModularFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachModule_KeepsItsTablesInTheContextItIsBoundTo()
    {
        List<string> tables = [];
        await fixture.WithScopeAsync(async services => tables = await services.GetRequiredService<HostDbContext>().Database
            .SqlQueryRaw<string>(
                "SELECT table_schema || '.' || table_name AS \"Value\" FROM information_schema.tables "
                + "WHERE table_schema IN ('public', 'catalog', 'sales')")
            .ToListAsync(CancellationToken));

        // Kendi bağlamı olan modüller kendi şemalarında; Haldus'un tabloları da onları kullanan modülle birlikte.
        Assert.Contains("catalog.Products", tables);
        Assert.Contains("catalog.HaldusIdempotencyRecords", tables);
        Assert.Contains("sales.Orders", tables);
        Assert.Contains("sales.HaldusCounters", tables);

        // Kendi bağlamı olmayan modül ve ana proje ortak bağlamda.
        Assert.True(tables.Contains("public.Tickets"), string.Join(", ", tables.Order()));
        Assert.Contains("public.Feedbacks", tables);
        Assert.Contains("public.TicketAttachments", tables);

        // Hiçbir entity ikinci bir bağlama sızmaz: başka modüle referans vermek onun tablosunu getirmez.
        Assert.Equal(1, tables.Count(table => table.EndsWith(".Products", StringComparison.Ordinal)));
        Assert.Equal(1, tables.Count(table => table.EndsWith(".Orders", StringComparison.Ordinal)));
        Assert.Equal(1, tables.Count(table => table.EndsWith(".Tickets", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AModulesOwnContext_ServesItsResourcesAndItsHandlers()
    {
        var product = await CreateProductAsync(price: 25);

        var read = await ReadAsync<ProductResponse>(await _client.GetAsync($"/products/{product.Id}", CancellationToken));
        var check = await ReadAsync<PriceCheck>(
            await _client.GetAsync($"/price-check?productId={product.Id}&quantity=3", CancellationToken));

        Assert.Equal(product.Name, read.Name);

        // Handler sınıfı modülün içindedir ve modülün kendi bağlamını kullanır.
        Assert.True(check.Found);
        Assert.Equal(75m, check.Total);
    }

    [Fact]
    public async Task AReferenceAcrossContexts_IsCheckedWhenWrittenButIsNotAConstraint()
    {
        var product = await CreateProductAsync();

        var missing = await _client.PostAsJsonAsync(
            "/orders", new { customerName = "Ada", productId = Guid.NewGuid() }, CancellationToken);
        var created = await _client.PostAsJsonAsync(
            "/orders", new { customerName = "Ada", productId = product.Id }, CancellationToken);

        // Ürünün varlığı katalog bağlamında doğrulanır.
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var order = await ReadAsync<OrderResponse>(created);
        Assert.Equal(product.Id, order.ProductId);

        // İki bağlam arasında yabancı anahtar yoktur: ürün, ona bağlı sipariş varken de silinebilir.
        var deleted = await _client.DeleteAsync($"/products/{product.Id}", CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/orders/{order.Id}", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task AReferenceBetweenModulesThatShareAContext_IsARealConstraint()
    {
        var ticket = await ReadAsync<TicketResponse>(
            await _client.PostAsJsonAsync("/tickets", new { subject = "Broken" }, CancellationToken));
        var feedback = await ReadAsync<FeedbackResponse>(
            await _client.PostAsJsonAsync("/feedbacks", new { rating = 5, ticketId = ticket.Id }, CancellationToken));

        // Talep başka bir modülün kaydıdır, ama aynı bağlamdadır; ona referans varken silinemez. Talebin
        // modülü bu referanstan habersizdir: çakışmayı veritabanı bildirir, uç 409'a çevirir.
        var refused = await _client.DeleteAsync($"/tickets/{ticket.Id}", CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await _client.DeleteAsync($"/feedbacks/{feedback.Id}", CancellationToken);
        var deleted = await _client.DeleteAsync($"/tickets/{ticket.Id}", CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task AResource_CanLiveUnderAParentOfAnotherModuleInTheSameContext()
    {
        var ticket = await ReadAsync<TicketResponse>(
            await _client.PostAsJsonAsync("/tickets", new { subject = "With files" }, CancellationToken));
        var url = $"/tickets/{ticket.Id}/ticket-attachments";

        var attachment = await ReadAsync<TicketAttachmentResponse>(
            await _client.PostAsJsonAsync(url, new { fileName = "log.txt" }, CancellationToken));
        var orphan = await _client.PostAsJsonAsync(
            $"/tickets/{Guid.NewGuid()}/ticket-attachments", new { fileName = "x.txt" }, CancellationToken);

        Assert.Equal(ticket.Id, attachment.TicketId);
        Assert.Equal(HttpStatusCode.NotFound, orphan.StatusCode);

        // Ebeveyn silinince altındakiler de gider.
        await _client.DeleteAsync($"/tickets/{ticket.Id}", CancellationToken);
        var remaining = -1;
        await fixture.WithScopeAsync(async services => remaining = await services.GetRequiredService<HostDbContext>()
            .TicketAttachments.IgnoreQueryFilters().CountAsync(row => row.TicketId == ticket.Id, CancellationToken));
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task ASharedContextModule_CanReferToARecordOfAModuleWithItsOwnContext()
    {
        var product = await CreateProductAsync();
        var order = await ReadAsync<OrderResponse>(
            await _client.PostAsJsonAsync("/orders", new { customerName = "Ada", productId = product.Id }, CancellationToken));

        var linked = await _client.PostAsJsonAsync("/tickets", new { subject = "Late", orderId = order.Id }, CancellationToken);
        var dangling = await _client.PostAsJsonAsync("/tickets", new { subject = "Late", orderId = Guid.NewGuid() }, CancellationToken);

        Assert.Equal(order.Id, (await ReadAsync<TicketResponse>(linked)).OrderId);
        Assert.Equal(HttpStatusCode.BadRequest, dangling.StatusCode);
    }

    [Fact]
    public async Task HaldusOwnTables_FollowTheModuleThatUsesThem()
    {
        var product = await CreateProductAsync();

        // Numara sayacı satış modülünün bağlamındadır.
        var first = await ReadAsync<OrderResponse>(
            await _client.PostAsJsonAsync("/orders", new { customerName = "Ada", productId = product.Id }, CancellationToken));
        var second = await ReadAsync<OrderResponse>(
            await _client.PostAsJsonAsync("/orders", new { customerName = "Ada", productId = product.Id }, CancellationToken));

        Assert.Matches(@"^S-\d{4}$", first.Number);
        Assert.Equal(1, int.Parse(second.Number[2..]) - int.Parse(first.Number[2..]));

        // Idempotency kayıtları katalog modülünün bağlamındadır; bakım bütün bağlamları dolaşır.
        var key = Guid.NewGuid().ToString();
        var created = await SendWithKeyAsync(key);
        var replayed = await SendWithKeyAsync(key);
        Assert.Equal((await ReadAsync<ProductResponse>(created)).Id, (await ReadAsync<ProductResponse>(replayed)).Id);

        await fixture.WithScopeAsync(services => services.GetRequiredService<CatalogDbContext>().Set<HaldusIdempotencyRecord>()
            .Where(record => record.Key == key)
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken));

        await HaldusMaintenance.RunAsync(fixture.Services, CancellationToken);

        var remaining = -1;
        await fixture.WithScopeAsync(async services => remaining = await services.GetRequiredService<CatalogDbContext>()
            .Set<HaldusIdempotencyRecord>().CountAsync(record => record.Key == key, CancellationToken));
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task TheProvider_RoutesEachEntityToItsModulesContext()
    {
        await fixture.WithScopeAsync(services =>
        {
            var contexts = services.GetRequiredService<IHaldusDbContextProvider>();

            Assert.IsType<CatalogDbContext>(contexts.For<Product>());
            Assert.IsType<SalesDbContext>(contexts.For<Order>());
            Assert.IsType<HostDbContext>(contexts.For<Ticket>());
            Assert.IsType<HostDbContext>(contexts.For<Feedback>());
            Assert.Equal(3, contexts.All.Count);

            // Bir bağlamın modeli yalnızca kendi modüllerinin entity'lerini içerir.
            Assert.Null(services.GetRequiredService<SalesDbContext>().Model.FindEntityType(typeof(Product)));
            Assert.Null(services.GetRequiredService<HostDbContext>().Model.FindEntityType(typeof(Order)));
            Assert.NotNull(services.GetRequiredService<HostDbContext>().Model.FindEntityType(typeof(Ticket)));

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AParent_MustShareTheContextOfWhatLivesUnderIt()
    {
        await fixture.WithScopeAsync(services =>
        {
            var host = services.GetRequiredService<HostDbContext>();
            var sales = services.GetRequiredService<SalesDbContext>();

            Assert.True(HaldusModel.Shares<Ticket>(host));
            Assert.False(HaldusModel.Shares<Product>(sales));

            // Üretilen model kodu bunu, başka modüldeki bir ebeveynin altına yerleşen her kaynak için çağırır;
            // ayrı bağlamlarda uygulama ilk istekte değil, model kurulurken durur.
            HaldusModel.RequireShared<Ticket>(host, "TicketAttachment");
            var error = Assert.Throws<InvalidOperationException>(() => HaldusModel.RequireShared<Product>(sales, "OrderLine"));
            Assert.Contains("'OrderLine' is declared under 'Product', but they live in different DbContexts", error.Message);

            return Task.CompletedTask;
        });
    }

    private async Task<HttpResponseMessage> SendWithKeyAsync(string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/products")
        {
            Content = JsonContent.Create(new { name = "Keyed", price = 1 }),
        };
        request.Headers.Add("Idempotency-Key", key);

        return await _client.SendAsync(request, CancellationToken);
    }

    private async Task<ProductResponse> CreateProductAsync(decimal price = 10) =>
        await ReadAsync<ProductResponse>(
            await _client.PostAsJsonAsync("/products", new { name = $"Product {Guid.NewGuid():N}", price }, CancellationToken));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken)}");

        return (await response.Content.ReadFromJsonAsync<T>(CancellationToken))!;
    }
}