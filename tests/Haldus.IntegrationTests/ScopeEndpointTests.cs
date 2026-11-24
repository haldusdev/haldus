using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Supply;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

/// <summary>
/// Bağlar ve kapsam: bağların topluca değiştirilmesi, yük taşıyan bağ, ebeveyn altında tek kayıt, kısa adres ve
/// komutla yazılan satırlar.
/// </summary>
[Collection(ShopCollection.Name)]
public sealed class ScopeEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ── Bağların topluca değiştirilmesi ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReplaceLinks_MakesTheLinksOfARecordEqualToTheGivenList()
    {
        var product = await CreateProductAsync();
        var first = await CreateTagAsync();
        var second = await CreateTagAsync();
        var third = await CreateTagAsync();
        var url = $"/products/{product.Id}/tags";

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, url, new { ids = new[] { first.Id, second.Id } })).StatusCode);
        Assert.Equal([first.Id, second.Id], await TagIdsAsync(product.Id));

        // Listede olmayan bağ kaldırılır, eksik olan kurulur, ortak olan kalır.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, url, new { ids = new[] { second.Id, third.Id, third.Id } })).StatusCode);
        Assert.Equal([second.Id, third.Id], await TagIdsAsync(product.Id));

        // Öteki yönden de çalışır.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Put, $"/tags/{first.Id}/products", new { ids = new[] { product.Id } })).StatusCode);
        Assert.Equal([first.Id, second.Id, third.Id], await TagIdsAsync(product.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, url, new { ids = Array.Empty<Guid>() })).StatusCode);
        Assert.Empty(await TagIdsAsync(product.Id));
    }

    [Fact]
    public async Task ReplaceLinks_ChangesNothingWhenAnyRecordIsUnknown()
    {
        var product = await CreateProductAsync();
        var tag = await CreateTagAsync();
        await SendAsync(HttpMethod.Put, $"/products/{product.Id}/tags", new { ids = new[] { tag.Id } });

        var unknownTag = await SendAsync(HttpMethod.Put, $"/products/{product.Id}/tags", new { ids = new[] { Guid.NewGuid() } });
        var unknownProduct = await SendAsync(HttpMethod.Put, $"/products/{Guid.NewGuid()}/tags", new { ids = new[] { tag.Id } });
        var missingList = await SendAsync(HttpMethod.Put, $"/products/{product.Id}/tags", new { });

        Assert.Equal(HttpStatusCode.BadRequest, unknownTag.StatusCode);
        Assert.Equal(["ids"], (await unknownTag.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!.Errors.Keys);
        Assert.Equal(HttpStatusCode.NotFound, unknownProduct.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingList.StatusCode);
        Assert.Equal([tag.Id], await TagIdsAsync(product.Id));
    }

    // ── Yük taşıyan bağ ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LinkWithPayload_CarriesItsOwnFields()
    {
        var supplier = await CreateSupplierAsync("Acme");
        var product = await CreateProductAsync();
        var url = $"/suppliers/{supplier.Id}/products/{product.Id}";

        var linked = await ReadAsync<SupplierProductResponse>(await SendAsync(HttpMethod.Put, url, new { cost = 12.5m, leadTimeDays = 7 }));
        Assert.Equal((supplier.Id, product.Id, 12.5m, 7), (linked.SupplierId, linked.ProductId, linked.Cost, linked.LeadTimeDays));

        // Aynı bağa yeniden yazmak yükünü değiştirir; bağ tektir.
        var changed = await ReadAsync<SupplierProductResponse>(await SendAsync(HttpMethod.Put, url, new { cost = 11m, leadTimeDays = 3 }));
        var read = await ReadAsync<SupplierProductResponse>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal((11m, 3), (changed.Cost, changed.LeadTimeDays));
        Assert.Equal((11m, 3), (read.Cost, read.LeadTimeDays));

        // Yükün kuralları isteğe uygulanır.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, url, new { cost = -1m, leadTimeDays = 3 })).StatusCode);

        // Uçlar eskisi gibi listelenir; bağların kendisi yükleriyle ayrı bir listededir, iki yönden de.
        var products = await ReadAsync<PagedResult<ProductResponse>>(await SendAsync(HttpMethod.Get, $"/suppliers/{supplier.Id}/products"));
        var links = await ReadAsync<PagedResult<SupplierProductResponse>>(await SendAsync(HttpMethod.Get, $"/suppliers/{supplier.Id}/products/links"));
        var reverse = await ReadAsync<PagedResult<SupplierProductResponse>>(await SendAsync(HttpMethod.Get, $"/products/{product.Id}/suppliers/links"));

        Assert.Equal([product.Id], products.Items.Select(item => item.Id));
        Assert.Equal(11m, Assert.Single(links.Items).Cost);
        Assert.Equal(supplier.Id, Assert.Single(reverse.Items).SupplierId);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, url)).StatusCode);
    }

    [Fact]
    public async Task LinkWithState_ChangesOnlyThroughItsTransitions()
    {
        var supplier = await CreateSupplierAsync("Acme");
        var product = await CreateProductAsync();
        var other = await CreateProductAsync();
        var url = $"/suppliers/{supplier.Id}/products/{product.Id}";

        // Yeni bağ başlangıç durumunda doğar; durum istekle yazılamaz.
        var linked = await ReadAsync<SupplierProductResponse>(await SendAsync(HttpMethod.Put, url, new { cost = 5m, leadTimeDays = 2, status = "Approved" }));
        Assert.Equal(SupplyStatus.Proposed, linked.Status);

        var raw = await (await SendAsync(HttpMethod.Get, url)).Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains("\"status\":\"Proposed\"", raw);

        var approved = await ReadAsync<SupplierProductResponse>(await SendAsync(HttpMethod.Post, $"{url}/approve"));
        Assert.Equal(SupplyStatus.Approved, approved.Status);

        // Geçiş yalnızca sayılan durumlardan yapılır.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/approve")).StatusCode);

        // Yükü yeniden yazmak durumu başa döndürmez.
        var rewritten = await ReadAsync<SupplierProductResponse>(await SendAsync(HttpMethod.Put, url, new { cost = 6m, leadTimeDays = 2 }));
        Assert.Equal((6m, SupplyStatus.Approved), (rewritten.Cost, rewritten.Status));

        // Bağlar duruma göre süzülür.
        await SendAsync(HttpMethod.Put, $"/suppliers/{supplier.Id}/products/{other.Id}", new { cost = 1m, leadTimeDays = 1 });
        var approvedOnly = await ReadAsync<PagedResult<SupplierProductResponse>>(
            await SendAsync(HttpMethod.Get, $"/suppliers/{supplier.Id}/products/links?status=Approved"));
        var all = await ReadAsync<PagedResult<SupplierProductResponse>>(await SendAsync(HttpMethod.Get, $"/suppliers/{supplier.Id}/products/links"));
        Assert.Equal(product.Id, Assert.Single(approvedOnly.Items).ProductId);
        Assert.Equal(2, all.Items.Count);

        var discontinued = await ReadAsync<SupplierProductResponse>(await SendAsync(HttpMethod.Post, $"{url}/discontinue"));
        Assert.Equal(SupplyStatus.Discontinued, discontinued.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/discontinue")).StatusCode);

        // Olmayan bağın geçişi de yoktur.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/suppliers/{supplier.Id}/products/{Guid.NewGuid()}/approve")).StatusCode);

        await fixture.WithDbContextAsync(async context =>
            Assert.Equal(
                "Discontinued",
                await context.Database
                    .SqlQuery<string>($"""SELECT "Status" AS "Value" FROM "SupplierProducts" WHERE "ProductId" = {product.Id}""")
                    .SingleAsync(CancellationToken)));
    }

    // ── Ebeveyn altında tek kayıt ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SingletonUnderAParent_IsOneRecordPerParent()
    {
        var supplier = await CreateSupplierAsync("Acme");
        var other = await CreateSupplierAsync("Globex");
        var url = $"/suppliers/{supplier.Id}/supplier-terms";

        // Hiç yazılmadıysa varsayılan değerler döner.
        var initial = await ReadAsync<SupplierTermsResponse>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal((supplier.Id, 30, "TRY"), (initial.SupplierId, initial.PaymentDays, initial.Currency));

        var written = await ReadAsync<SupplierTermsResponse>(await SendAsync(HttpMethod.Put, url, new { paymentDays = 60, currency = "EUR" }));
        var patched = await ReadAsync<SupplierTermsResponse>(await SendAsync(HttpMethod.Patch, url, new { paymentDays = 45 }));
        Assert.Equal((60, "EUR"), (written.PaymentDays, written.Currency));
        Assert.Equal((45, "EUR"), (patched.PaymentDays, patched.Currency));

        // Kayıt ebeveyne aittir; başka bir ebeveyninki etkilenmez.
        var untouched = await ReadAsync<SupplierTermsResponse>(await SendAsync(HttpMethod.Get, $"/suppliers/{other.Id}/supplier-terms"));
        Assert.Equal((30, "TRY"), (untouched.PaymentDays, untouched.Currency));

        // Ebeveyn yoksa kayıt da yoktur; yazılamaz da.
        var missing = $"/suppliers/{Guid.NewGuid()}/supplier-terms";
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, missing)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, missing, new { paymentDays = 1, currency = "USD" })).StatusCode);

        await fixture.WithDbContextAsync(async context =>
            Assert.Equal(1, await context.SupplierTerms.CountAsync(item => item.SupplierId == supplier.Id, CancellationToken)));
    }

    // ── Kısa adres ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShallowChild_LivesAtItsOwnAddressButBelongsToItsParent()
    {
        var supplier = await CreateSupplierAsync("Acme");
        var other = await CreateSupplierAsync("Globex");

        var created = await SendAsync(HttpMethod.Post, "/supplier-contacts", new { supplierId = supplier.Id, name = "Ada" });
        var contact = await ReadAsync<SupplierContactResponse>(created);

        Assert.Equal($"/supplier-contacts/{contact.Id}", created.Headers.Location!.OriginalString);
        Assert.Equal(supplier.Id, contact.SupplierId);

        // Ebeveyn zorunludur ve görünür olmalıdır.
        var orphan = await SendAsync(HttpMethod.Post, "/supplier-contacts", new { supplierId = Guid.NewGuid(), name = "Nobody" });
        Assert.Equal(HttpStatusCode.BadRequest, orphan.StatusCode);
        Assert.Equal(["supplierId"], (await orphan.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!.Errors.Keys);

        // Kayıt ebeveyni bilinmeden adreslenir; ebeveyni sonradan değişmez.
        var moved = await ReadAsync<SupplierContactResponse>(await SendAsync(
            HttpMethod.Put, $"/supplier-contacts/{contact.Id}", new { supplierId = other.Id, name = "Ada L." }));
        Assert.Equal((supplier.Id, "Ada L."), (moved.SupplierId, moved.Name));

        // Liste ebeveynler arasında döner ve ebeveyne göre süzülür; sıra ebeveyn içindedir.
        var second = await ReadAsync<SupplierContactResponse>(await SendAsync(HttpMethod.Post, "/supplier-contacts", new { supplierId = supplier.Id, name = "Grace" }));
        var elsewhere = await ReadAsync<SupplierContactResponse>(await SendAsync(HttpMethod.Post, "/supplier-contacts", new { supplierId = other.Id, name = "Linus" }));
        Assert.Equal((0, 1, 0), (contact.Position, second.Position, elsewhere.Position));

        var ofSupplier = await ReadAsync<PagedResult<SupplierContactResponse>>(await SendAsync(HttpMethod.Get, $"/supplier-contacts?supplierId={supplier.Id}"));
        Assert.Equal(["Ada L.", "Grace"], ofSupplier.Items.Select(item => item.Name));

        // Ebeveyn silinince altındakiler de gider.
        var etag = (await SendAsync(HttpMethod.Get, $"/suppliers/{supplier.Id}")).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/suppliers/{supplier.Id}", ifMatch: etag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/supplier-contacts/{contact.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/supplier-contacts/{elsewhere.Id}")).StatusCode);
    }

    // ── Komutla yazılan satırlar ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetailCommands_WriteRowsOneByOneAsWritesOfTheOwner()
    {
        var created = await SendAsync(HttpMethod.Post, "/suppliers", new
        {
            name = "Acme",
            supplierCertificates = new[] { new { name = "ISO 9001" } },
        });
        var supplier = await ReadAsync<SupplierResponse>(created);
        var rows = $"/suppliers/{supplier.Id}/supplier-certificates";
        var etag = created.Headers.ETag!.Tag;

        Assert.Equal(1, supplier.CertificateCount);

        // Komut üst kaydın bir yazmasıdır: sürüm ister, hesaplanan alanı yeniler ve üst kaydı döndürür.
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await SendAsync(HttpMethod.Post, rows, new { name = "ISO 14001" })).StatusCode);

        var added = await SendAsync(HttpMethod.Post, rows, new { name = "ISO 14001" }, ifMatch: etag);
        var afterAdd = await ReadAsync<SupplierResponse>(added);
        Assert.Equal(["ISO 9001", "ISO 14001"], afterAdd.SupplierCertificates.Select(row => row.Name));
        Assert.Equal(2, afterAdd.CertificateCount);
        Assert.NotEqual(etag, added.Headers.ETag!.Tag);

        // Eski sürümle gelen komut reddedilir.
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SendAsync(HttpMethod.Post, rows, new { name = "Stale" }, ifMatch: etag)).StatusCode);

        var rowId = afterAdd.SupplierCertificates[0].Id;
        var changed = await SendAsync(HttpMethod.Put, $"{rows}/{rowId}", new { name = "ISO 9001:2015" }, ifMatch: added.Headers.ETag!.Tag);
        var afterChange = await ReadAsync<SupplierResponse>(changed);
        Assert.Equal(["ISO 9001:2015", "ISO 14001"], afterChange.SupplierCertificates.Select(row => row.Name));

        var removed = await SendAsync(HttpMethod.Delete, $"{rows}/{rowId}", ifMatch: changed.Headers.ETag!.Tag);
        var afterRemove = await ReadAsync<SupplierResponse>(removed);
        Assert.Equal(["ISO 14001"], afterRemove.SupplierCertificates.Select(row => row.Name));
        Assert.Equal(1, afterRemove.CertificateCount);

        var unknownRow = await SendAsync(HttpMethod.Delete, $"{rows}/{Guid.NewGuid()}", ifMatch: removed.Headers.ETag!.Tag);
        var invalid = await SendAsync(HttpMethod.Post, rows, new { name = "" }, ifMatch: removed.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotFound, unknownRow.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // Satırlar güncelleme isteğinde taşınmaz: üst kaydı güncellemek onlara dokunmaz.
        var renamed = await ReadAsync<SupplierResponse>(await SendAsync(
            HttpMethod.Put, $"/suppliers/{supplier.Id}", new { name = "Acme Ltd", supplierCertificates = Array.Empty<object>() }, ifMatch: removed.Headers.ETag!.Tag));
        Assert.Equal(("Acme Ltd", 1), (renamed.Name, renamed.SupplierCertificates.Count));
    }

    private async Task<Guid[]> TagIdsAsync(Guid productId)
    {
        var page = await ReadAsync<PagedResult<TagResponse>>(await SendAsync(HttpMethod.Get, $"/products/{productId}/tags"));
        return [.. page.Items.Select(item => item.Id)];
    }

    private async Task<ProductResponse> CreateProductAsync() =>
        await ReadAsync<ProductResponse>(await SendAsync(HttpMethod.Post, "/products", new { name = $"Scoped {Guid.NewGuid():N}", price = 10 }));

    private async Task<TagResponse> CreateTagAsync() =>
        await ReadAsync<TagResponse>(await SendAsync(HttpMethod.Post, "/tags", new { name = $"tag-{Guid.NewGuid():N}" }));

    private async Task<SupplierResponse> CreateSupplierAsync(string name) =>
        await ReadAsync<SupplierResponse>(await SendAsync(HttpMethod.Post, "/suppliers", new { name }));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);

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
