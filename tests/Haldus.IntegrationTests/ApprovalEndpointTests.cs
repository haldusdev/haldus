using System.Net;
using System.Net.Http.Json;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Supply;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haldus.IntegrationTests;

/// <summary>Onaya bağlı kayıtlar (<c>Approvable</c>) ve belgeler (<c>Document</c>).</summary>
[Collection(ShopCollection.Name)]
public sealed class ApprovalEndpointTests(ShopFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();
    private readonly string _maker = $"maker-{Guid.NewGuid():N}";
    private readonly string _first = $"first-{Guid.NewGuid():N}";
    private readonly string _second = $"second-{Guid.NewGuid():N}";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ── Onay ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approval_IsDecidedBySomeoneOtherThanTheWriter()
    {
        var claim = await ReadAsync<ExpenseClaimResponse>(await SendAsync(
            HttpMethod.Post, "/expense-claims", _maker, new { description = "Taxi", amount = 40, approvalStatus = "Approved", requestedBy = _first }));
        var url = $"/expense-claims/{claim.Id}";

        // Onay alanları istekle yazılamaz; kayıt karar bekleyerek doğar ve onu yazan kişiye bağlanır.
        Assert.Equal((ApprovalStatus.Pending, 0, _maker), (claim.ApprovalStatus, claim.ApprovalLevel, claim.RequestedBy));

        var raw = await (await SendAsync(HttpMethod.Get, url, _maker)).Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains("\"approvalStatus\":\"Pending\"", raw);

        // Yazan kişi kendi yazdığına karar veremez; kullanıcısı çözülemeyen istek de veremez.
        var own = await SendAsync(HttpMethod.Post, $"{url}/approve", _maker);
        var anonymous = await SendAsync(HttpMethod.Post, $"{url}/approve", user: null);
        var turkish = await SendAsync(HttpMethod.Post, $"{url}/approve", _maker, language: "tr");

        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Bu kararı veremezsiniz", (await turkish.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken))!.Title);

        var approved = await ReadAsync<ExpenseClaimResponse>(await SendAsync(HttpMethod.Post, $"{url}/approve", _first, new { note = "Looks fine" }));
        Assert.Equal(
            (ApprovalStatus.Approved, 1, _first, "Looks fine"),
            (approved.ApprovalStatus, approved.ApprovalLevel, approved.DecidedBy, approved.DecisionNote));
        Assert.NotNull(approved.DecidedAt);

        // Karar bir kez verilir; onaylanmış kaydın içeriği artık değişmez.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/approve", _second)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/reject", _second)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Put, url, _maker, new { description = "Limo", amount = 400 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Patch, url, _maker, new { amount = 400 })).StatusCode);

        var decisions = await ReadAsync<List<ApprovalDecision>>(await SendAsync(HttpMethod.Get, $"{url}/approvals", _maker));
        var decision = Assert.Single(decisions);
        Assert.Equal((1, true, _first, "Looks fine"), (decision.Level, decision.Approved, decision.UserId, decision.Note));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/expense-claims/{Guid.NewGuid()}/approvals", _maker)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/expense-claims/{Guid.NewGuid()}/approve", _first)).StatusCode);
    }

    [Fact]
    public async Task Approval_RejectedRecordWaitsAgainOnceItIsEdited()
    {
        var claim = await ReadAsync<ExpenseClaimResponse>(await SendAsync(
            HttpMethod.Post, "/expense-claims", _maker, new { description = "Dinner", amount = 900 }));
        var url = $"/expense-claims/{claim.Id}";

        var rejected = await ReadAsync<ExpenseClaimResponse>(await SendAsync(HttpMethod.Post, $"{url}/reject", _first, new { note = "Too much" }));
        Assert.Equal((ApprovalStatus.Rejected, 0, "Too much"), (rejected.ApprovalStatus, rejected.ApprovalLevel, rejected.DecisionNote));

        // Reddedilen kayıt düzenlenebilir; düzenlenince yeniden karar bekler ve son yazan artık düzenleyendir.
        var edited = await ReadAsync<ExpenseClaimResponse>(await SendAsync(HttpMethod.Patch, url, _first, new { amount = 90 }));
        Assert.Equal((ApprovalStatus.Pending, _first, null), (edited.ApprovalStatus, edited.RequestedBy, edited.DecidedBy));
        Assert.True(edited.RequestedAt > claim.RequestedAt);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"{url}/approve", _first)).StatusCode);

        var approved = await ReadAsync<ExpenseClaimResponse>(await SendAsync(HttpMethod.Post, $"{url}/approve", _maker));
        Assert.Equal(ApprovalStatus.Approved, approved.ApprovalStatus);

        // Kararların kaydı silinmez: ret ve sonraki onay birlikte durur.
        var decisions = await ReadAsync<List<ApprovalDecision>>(await SendAsync(HttpMethod.Get, $"{url}/approvals", _maker));
        Assert.Equal([false, true], decisions.Select(decision => decision.Approved));

        // Not sınırlıdır.
        var other = await ReadAsync<ExpenseClaimResponse>(await SendAsync(HttpMethod.Post, "/expense-claims", _maker, new { description = "Hotel", amount = 1 }));
        var tooLong = await SendAsync(HttpMethod.Post, $"/expense-claims/{other.Id}/reject", _first, new { note = new string('x', 1001) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Approval_WithLevels_NeedsDifferentPeopleInTurnAndGatesTransitions()
    {
        var request = await ReadAsync<PurchaseRequestResponse>(await SendAsync(
            HttpMethod.Post, "/purchase-requests", _maker, new { title = "Laptops", amount = 5000 }));
        var url = $"/purchase-requests/{request.Id}";

        // Geçiş, kayıt onaylanmadan çalışmaz.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/order", _maker)).StatusCode);

        var once = await ReadAsync<PurchaseRequestResponse>(await SendAsync(HttpMethod.Post, $"{url}/approve", _first));
        Assert.Equal((ApprovalStatus.Pending, 1), (once.ApprovalStatus, once.ApprovalLevel));

        // Aynı kişi iki düzeyi birden onaylayamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"{url}/approve", _first)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/order", _maker)).StatusCode);

        // İçerik değişince toplanan onay geçersiz olur.
        var edited = await ReadAsync<PurchaseRequestResponse>(await SendAsync(HttpMethod.Patch, url, _maker, new { amount = 6000 }));
        Assert.Equal((ApprovalStatus.Pending, 0), (edited.ApprovalStatus, edited.ApprovalLevel));

        // Düzey sıfırlandığı için ilk onaylayan yeniden onaylayabilir; ikinci onayı başka biri verir.
        await ReadAsync<PurchaseRequestResponse>(await SendAsync(HttpMethod.Post, $"{url}/approve", _first));
        var twice = await ReadAsync<PurchaseRequestResponse>(await SendAsync(HttpMethod.Post, $"{url}/approve", _second, new { note = "Budget ok" }));
        Assert.Equal((ApprovalStatus.Approved, 2, _second), (twice.ApprovalStatus, twice.ApprovalLevel, twice.DecidedBy));

        var ordered = await ReadAsync<PurchaseRequestResponse>(await SendAsync(HttpMethod.Post, $"{url}/order", _maker));
        Assert.Equal(PurchaseRequestStatus.Ordered, ordered.Status);

        var decisions = await ReadAsync<List<ApprovalDecision>>(await SendAsync(HttpMethod.Get, $"{url}/approvals", _maker));
        Assert.Equal([1, 1, 2], decisions.Select(decision => decision.Level));
    }

    [Fact]
    public async Task Approval_DecidedConcurrently_IsRecordedOnce()
    {
        var claim = await ReadAsync<ExpenseClaimResponse>(await SendAsync(
            HttpMethod.Post, "/expense-claims", _maker, new { description = "Train", amount = 30 }));
        var url = $"/expense-claims/{claim.Id}";

        var responses = await Task.WhenAll(
            SendAsync(HttpMethod.Post, $"{url}/approve", _first),
            SendAsync(HttpMethod.Post, $"{url}/reject", _second));

        // İki karardan yalnızca biri geçerli olur; öteki çakışma alır ve iz bırakmaz.
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);

        var decisions = await ReadAsync<List<ApprovalDecision>>(await SendAsync(HttpMethod.Get, $"{url}/approvals", _maker));
        Assert.Single(decisions);

        await fixture.WithDbContextAsync(async context =>
            Assert.Equal(
                1,
                await context.Set<HaldusApprovalDecision>().CountAsync(decision => decision.RecordId == claim.Id.ToString(), CancellationToken)));
    }

    // ── Belge ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Document_GoesFromDraftToSubmittedToCancelled()
    {
        var created = await ReadAsync<SupplyContractResponse>(await SendAsync(
            HttpMethod.Post, "/supply-contracts", _maker, new { title = "Paper", value = 1000, status = "Submitted" }));
        var url = $"/supply-contracts/{created.Id}";

        Assert.Equal((DocumentStatus.Draft, null, 1), (created.Status, created.Number, created.Revision));

        // Taslak düzenlenir; iptal edilemez ve düzeltilemez.
        var edited = await ReadAsync<SupplyContractResponse>(await SendAsync(HttpMethod.Patch, url, _maker, new { value = 1200 }));
        Assert.Equal(1200, edited.Value);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/cancel", _maker)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/amend", _maker)).StatusCode);

        // Gönderilen belge numarasını alır ve kilitlenir.
        var submitted = await ReadAsync<SupplyContractResponse>(await SendAsync(HttpMethod.Post, $"{url}/submit", _maker));
        Assert.Equal(DocumentStatus.Submitted, submitted.Status);
        Assert.StartsWith("CTR-", submitted.Number);

        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Patch, url, _maker, new { value = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Delete, url, _maker)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/submit", _maker)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/amend", _maker)).StatusCode);

        var cancelled = await ReadAsync<SupplyContractResponse>(await SendAsync(HttpMethod.Post, $"{url}/cancel", _maker));
        Assert.Equal(DocumentStatus.Cancelled, cancelled.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Patch, url, _maker, new { value = 1 })).StatusCode);

        var byStatus = await ReadAsync<PagedResult<SupplyContractResponse>>(await SendAsync(HttpMethod.Get, "/supply-contracts?status=Cancelled&pageSize=100", _maker));
        Assert.Contains(byStatus.Items, item => item.Id == created.Id);
        Assert.All(byStatus.Items, item => Assert.Equal(DocumentStatus.Cancelled, item.Status));
    }

    [Fact]
    public async Task Document_IsAmendedByANewDraftThatPointsAtTheCancelledOne()
    {
        var created = await ReadAsync<SupplyContractResponse>(await SendAsync(
            HttpMethod.Post, "/supply-contracts", _maker, new { title = "Ink", value = 500 }));
        var url = $"/supply-contracts/{created.Id}";

        await SendAsync(HttpMethod.Post, $"{url}/submit", _maker);
        var cancelled = await ReadAsync<SupplyContractResponse>(await SendAsync(HttpMethod.Post, $"{url}/cancel", _maker));

        var response = await SendAsync(HttpMethod.Post, $"{url}/amend", _maker, new { value = 550 });
        var amendment = await ReadAsync<SupplyContractResponse>(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/supply-contracts/{amendment.Id}", response.Headers.Location!.OriginalString);

        // Yeni taslak eskisinin kopyasıdır, gönderilen alan onun yerine geçer, sözleşme revizyonu ilerletir.
        Assert.Equal(
            (DocumentStatus.Draft, "Ink", 550m, 2, created.Id, null),
            (amendment.Status, amendment.Title, amendment.Value, amendment.Revision, amendment.AmendedFromId, amendment.Number));

        // Eski belge yenisine işaret eder, değişmez ve ikinci kez düzeltilemez; ikisi de silinemez.
        var original = await ReadAsync<SupplyContractResponse>(await SendAsync(HttpMethod.Get, url, _maker));
        Assert.Equal((DocumentStatus.Cancelled, amendment.Id, cancelled.Number), (original.Status, original.AmendedById, original.Number));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{url}/amend", _maker)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Delete, $"/supply-contracts/{amendment.Id}", _maker)).StatusCode);

        // Düzeltme kendi yaşam döngüsünden geçer ve kendi numarasını alır.
        var submitted = await ReadAsync<SupplyContractResponse>(await SendAsync(HttpMethod.Post, $"/supply-contracts/{amendment.Id}/submit", _maker));
        Assert.Equal(DocumentStatus.Submitted, submitted.Status);
        Assert.NotEqual(cancelled.Number, submitted.Number);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string? user, object? body = null, string? language = null)
    {
        using var request = new HttpRequestMessage(method, url);

        if (user is not null)
        {
            request.Headers.Add(DemoIdentity.UserHeader, user);
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
