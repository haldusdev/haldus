using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Engagement;
using Haldus.Sample.Shop.Features.Sales;
using Microsoft.AspNetCore.Mvc;

namespace Haldus.IntegrationTests;

/// <summary>Değişiklik kümeleri (<c>ChangeSets</c>): biriktir, önizle, tek seferde uygula.</summary>
[Collection(ShopCollection.Name)]
public sealed class ChangeSetEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ChangeSet_EditsARecordThatCannotBeEditedDirectly()
    {
        var (order, tag) = await PlaceOrderAsync();
        var url = $"/orders/{order.Id}";

        // Verilmiş sipariş doğrudan düzenlenemez.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Patch, url, new { customerName = "Direct" }, ifMatch: tag)).StatusCode);

        var begun = await SendAsync(HttpMethod.Post, $"{url}/edits");
        Assert.Equal(HttpStatusCode.Created, begun.StatusCode);
        var edit = await ReadAsync<ChangeSetInfo>(begun);
        var editUrl = $"{url}/edits/{edit.Id}";
        Assert.EndsWith(editUrl, begun.Headers.Location!.ToString());
        Assert.Equal((ChangeSetStatus.Open, "{}"), (edit.Status, edit.Changes.GetRawText()));

        // Değişiklikler kısmi güncelleme gövdesiyle aynı biçimde, adım adım birikir.
        await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Patch, editUrl, new { customerName = "Typo" }));
        await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Patch, editUrl, new
        {
            orderItems = new[] { new { productName = "Lamp", quantity = 3, unitPrice = 20m } },
        }));
        var staged = await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Patch, editUrl, new { customerName = "Ada Lovelace" }));

        // Aynı alan yeniden gönderilirse sonuncusu geçerlidir.
        Assert.Equal("Ada Lovelace", staged.Changes.GetProperty("customerName").GetString());
        Assert.Equal(1, staged.Changes.GetProperty("orderItems").GetArrayLength());

        // Önizleme sonucu gösterir, hesaplanan alanlarıyla; kayıt değişmez.
        var preview = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, $"{editUrl}/preview"));
        Assert.Equal(("Ada Lovelace", 60m, 1), (preview.CustomerName, preview.Total, preview.ItemCount));

        var untouched = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal(("Grace", 35m, 2), (untouched.CustomerName, untouched.Total, untouched.ItemCount));

        // Uygulama kaydın bir yazmasıdır: sürümü ister.
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await SendAsync(HttpMethod.Post, $"{editUrl}/commit")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SendAsync(HttpMethod.Post, $"{editUrl}/commit", ifMatch: "\"1\"")).StatusCode);

        var committed = await SendAsync(HttpMethod.Post, $"{editUrl}/commit", ifMatch: tag);
        var edited = await ReadAsync<OrderResponse>(committed);
        Assert.Equal(("Ada Lovelace", 60m, 1, OrderStatus.Placed), (edited.CustomerName, edited.Total, edited.ItemCount, edited.Status));
        Assert.NotEqual(tag, committed.Headers.ETag!.Tag);

        var stored = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal(("Ada Lovelace", 60m), (stored.CustomerName, stored.Total));
        Assert.Equal("Lamp", Assert.Single(stored.OrderItems).ProductName);

        // Kapanmış küme yeniden uygulanamaz ve değiştirilemez.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{editUrl}/commit", ifMatch: committed.Headers.ETag.Tag)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Patch, editUrl, new { customerName = "Late" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Delete, editUrl)).StatusCode);

        var closed = await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Get, editUrl));
        Assert.Equal(ChangeSetStatus.Committed, closed.Status);
        Assert.NotNull(closed.ClosedAt);
    }

    [Fact]
    public async Task ChangeSet_ValidatesWhatItStagesAndWhereItApplies()
    {
        var (order, tag) = await PlaceOrderAsync();
        var url = $"/orders/{order.Id}";
        var edit = await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Post, $"{url}/edits"));
        var editUrl = $"{url}/edits/{edit.Id}";

        // Kısmi güncellemenin kuralları kümede de geçerlidir; reddedilen değişiklik kümeye girmez.
        var tooLong = await SendAsync(HttpMethod.Patch, editUrl, new { customerName = new string('x', 201) });
        var badRow = await SendAsync(HttpMethod.Patch, editUrl, new { orderItems = new[] { new { productName = "Lamp", quantity = 0, unitPrice = 1m } } });
        var wrongType = await SendAsync(HttpMethod.Patch, editUrl, new { orderItems = "many" });
        var notAnObject = await SendAsync(HttpMethod.Patch, editUrl, new[] { 1, 2 });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badRow.StatusCode);
        Assert.Equal(["changes"], await ErrorsAsync(wrongType));
        Assert.Equal(["changes"], await ErrorsAsync(notAnObject));
        Assert.Equal("{}", (await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Get, editUrl))).Changes.GetRawText());

        // Başka bir kaydın kümesi bu kaydın adresinde bulunamaz; başka bir kiracı kaydı da kümeyi de göremez.
        var (other, _) = await PlaceOrderAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/orders/{other.Id}/edits/{edit.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/orders/{other.Id}/edits/{edit.Id}/commit", ifMatch: tag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, editUrl, tenant: Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"{url}/edits", tenant: Guid.NewGuid())).StatusCode);

        // Vazgeçilen küme uygulanamaz.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, editUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{editUrl}/commit", ifMatch: tag)).StatusCode);

        var second = await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Post, $"{url}/edits"));
        var all = await ReadAsync<List<ChangeSetInfo>>(await SendAsync(HttpMethod.Get, $"{url}/edits"));
        Assert.Equal([(second.Id, ChangeSetStatus.Open), (edit.Id, ChangeSetStatus.Discarded)], all.Select(item => (item.Id, item.Status)));

        // Küme yalnızca sayılan durumlarda açılır ve uygulanır: taslak sipariş zaten doğrudan düzenlenir.
        var draft = await ReadAsync<OrderResponse>(await SendAsync(HttpMethod.Post, "/orders", new { customerName = "Draft" }));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"/orders/{draft.Id}/edits")).StatusCode);

        // Açıldıktan sonra kaydın durumu değişirse küme artık uygulanamaz.
        var cancelled = await SendAsync(HttpMethod.Post, $"{url}/cancel", new { reason = "Changed my mind" }, ifMatch: tag);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Patch, $"{url}/edits/{second.Id}", new { customerName = "Too late" }));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/edits/{second.Id}/commit", ifMatch: cancelled.Headers.ETag!.Tag)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Get, $"{url}/edits/{second.Id}/preview")).StatusCode);
    }

    [Fact]
    public async Task ChangeSet_AppliesToTheCurrentRecordAndOnlyOnce()
    {
        var discussion = await ReadAsync<DiscussionResponse>(await SendAsync(HttpMethod.Post, "/discussions", new { title = "Original" }));
        var url = $"/discussions/{discussion.Id}";

        var edit = await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Post, $"{url}/edits"));
        var editUrl = $"{url}/edits/{edit.Id}";
        await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Patch, editUrl, new { title = "From the change set" }));

        // Küme hazırlanırken kayıt başka yoldan değişebilir; küme, kaydın o andaki haline uygulanır.
        await ReadAsync<DiscussionResponse>(await SendAsync(HttpMethod.Patch, url, new { title = "Changed meanwhile" }));
        await ReadAsync<ReplyResponse>(await SendAsync(HttpMethod.Post, $"{url}/replies", new { text = "Hi", score = 2 }));

        // Aynı kümeyi birlikte uygulamak isteyen iki istekten yalnızca biri geçer.
        var responses = await Task.WhenAll(
            SendAsync(HttpMethod.Post, $"{editUrl}/commit"),
            SendAsync(HttpMethod.Post, $"{editUrl}/commit"));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(response => response.StatusCode).Order());

        var current = await ReadAsync<DiscussionResponse>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal(("From the change set", 1, 2), (current.Title, current.ReplyCount, current.Score));

        // Kümeye yazılamayan bir alan konsa da kayda geçmez: toplanan alanlar istekle değişmez.
        var forged = await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Post, $"{url}/edits"));
        await ReadAsync<ChangeSetInfo>(await SendAsync(HttpMethod.Patch, $"{url}/edits/{forged.Id}", new { replyCount = 99, title = "Renamed" }));
        var applied = await ReadAsync<DiscussionResponse>(await SendAsync(HttpMethod.Post, $"{url}/edits/{forged.Id}/commit"));
        Assert.Equal(("Renamed", 1), (applied.Title, applied.ReplyCount));
    }

    private async Task<(OrderResponse Order, string Tag)> PlaceOrderAsync()
    {
        var created = await SendAsync(HttpMethod.Post, "/orders", new
        {
            customerName = "Grace",
            orderItems = new[]
            {
                new { productName = "Desk", quantity = 1, unitPrice = 30m },
                new { productName = "Pen", quantity = 5, unitPrice = 1m },
            },
        });
        var order = await ReadAsync<OrderResponse>(created);

        var placed = await SendAsync(HttpMethod.Post, $"/orders/{order.Id}/place", new { }, ifMatch: created.Headers.ETag!.Tag);
        return (await ReadAsync<OrderResponse>(placed), placed.Headers.ETag!.Tag);
    }

    private static async Task<IEnumerable<string>> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken))!.Errors.Keys;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? ifMatch = null, Guid? tenant = null)
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
