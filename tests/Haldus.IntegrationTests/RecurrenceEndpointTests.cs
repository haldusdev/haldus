using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Haldus.Sample.Shop.Features.Engagement;

namespace Haldus.IntegrationTests;

/// <summary>Tekrar serisi (<c>Recurring</c>) ve alana özgü mantığın aksiyon olarak yazılması (varyant üretimi).</summary>
[Collection(ShopCollection.Name)]
public sealed class RecurrenceEndpointTests(ShopFixture fixture)
{
    private static readonly DateTimeOffset Monday = new(2027, 1, 4, 9, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Series_RequiresItsFirstOccurrence()
    {
        // Gönderilmeyen ilk oluş 0001-01-01 sayılmaz; alan düzeyinde 400 olur.
        var missing = await SendAsync(HttpMethod.Post, "/meetings", new { title = "No start", recurrenceFrequency = "Weekly" });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var problem = await missing.Content.ReadFromJsonAsync<ValidationProblemDetails>(CancellationToken);
        Assert.True(problem!.Errors.ContainsKey("startsAt"), string.Join(", ", problem.Errors.Keys));
    }

    [Fact]
    public async Task Occurrences_AreComputedFromTheRule()
    {
        var created = await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Weekly sync",
            durationMinutes = 45,
            startsAt = Monday,
            recurrenceFrequency = "Weekly",
            recurrenceCount = 5,
        });
        var json = await created.Content.ReadAsStringAsync(CancellationToken);
        var meeting = await ReadAsync<MeetingResponse>(created);

        // Kural kaydın yazılabilir alanlarıdır; sıklık adıyla yazılır ve okunur.
        Assert.Equal((RecurrenceFrequency.Weekly, 5, null), (meeting.RecurrenceFrequency, meeting.RecurrenceCount, meeting.RecurrenceInterval));
        Assert.Contains("\"recurrenceFrequency\":\"Weekly\"", json);

        // Oluşlar saklanmaz; her okumada kuraldan hesaplanır. Aralık verilmezse ilk oluştan başlayarak bir yıl.
        var url = $"/meetings/{meeting.Id}/occurrences";
        var all = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal(Enumerable.Range(0, 5).Select(week => Monday.AddDays(7 * week)), all.Select(item => item.Start));
        Assert.All(all, item => Assert.Equal((item.OriginalStart, false), (item.Start, item.Cancelled)));

        var window = await ReadAsync<List<Occurrence>>(await SendAsync(
            HttpMethod.Get, $"{url}?from={Escape(Monday.AddDays(8))}&to={Escape(Monday.AddDays(21))}"));
        Assert.Equal([Monday.AddDays(14), Monday.AddDays(21)], window.Select(item => item.Start));

        // Kural değişince oluşlar da değişir: kayıt tektir.
        await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Patch, $"/meetings/{meeting.Id}", new { recurrenceInterval = 2, recurrenceCount = 3 }));
        var biweekly = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal([Monday, Monday.AddDays(14), Monday.AddDays(28)], biweekly.Select(item => item.Start));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/meetings/{Guid.NewGuid()}/occurrences")).StatusCode);
    }

    [Fact]
    public async Task Occurrences_FollowTheCalendar()
    {
        // Ayın 31'inde başlayan aylık seri kısa aylarda ayın son gününe düşer, sonra yine 31'ine döner.
        var monthEnd = new DateTimeOffset(2027, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var monthly = await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Month end",
            durationMinutes = 30,
            startsAt = monthEnd,
            recurrenceFrequency = "Monthly",
            recurrenceCount = 4,
        }));

        var months = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, $"/meetings/{monthly.Id}/occurrences"));
        Assert.Equal(
            [monthEnd, new(2027, 2, 28, 12, 0, 0, TimeSpan.Zero), new(2027, 3, 31, 12, 0, 0, TimeSpan.Zero), new(2027, 4, 30, 12, 0, 0, TimeSpan.Zero)],
            months.Select(item => item.Start));

        // İki günde bir, bir bitiş zamanına kadar.
        var daily = await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Stand-up",
            durationMinutes = 15,
            startsAt = Monday,
            recurrenceFrequency = "Daily",
            recurrenceInterval = 2,
            recurrenceUntil = Monday.AddDays(5),
        }));

        var days = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, $"/meetings/{daily.Id}/occurrences"));
        Assert.Equal([Monday, Monday.AddDays(2), Monday.AddDays(4)], days.Select(item => item.Start));

        // Uzun süredir süren bir seride uzak bir aralık da doğru hesaplanır.
        var far = await ReadAsync<List<Occurrence>>(await SendAsync(
            HttpMethod.Get, $"/meetings/{daily.Id}/occurrences?from={Escape(Monday.AddDays(3))}&to={Escape(Monday.AddDays(4))}"));
        Assert.Equal(Monday.AddDays(4), Assert.Single(far).Start);

        // Yinelenmeyen kaydın tek bir oluşu vardır.
        var once = await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Kick-off",
            durationMinutes = 60,
            startsAt = Monday,
            recurrenceFrequency = "None",
        }));

        Assert.Equal(Monday, Assert.Single(await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, $"/meetings/{once.Id}/occurrences"))).Start);

        // Kuralın alanları da her alan gibi doğrulanır.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Bad",
            durationMinutes = 30,
            startsAt = Monday,
            recurrenceFrequency = "Weekly",
            recurrenceInterval = 0,
        })).StatusCode);
    }

    [Fact]
    public async Task SingleOccurrences_CanBeMovedCancelledAndRestored()
    {
        var meeting = await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Review",
            durationMinutes = 30,
            startsAt = Monday,
            recurrenceFrequency = "Weekly",
            recurrenceCount = 4,
        }));
        var url = $"/meetings/{meeting.Id}/occurrences";
        var second = Monday.AddDays(7);
        var third = Monday.AddDays(14);

        // Tek bir oluş ertelenir; serinin geri kalanı kurala uyar.
        var moved = await ReadAsync<Occurrence>(await SendAsync(HttpMethod.Put, $"{url}/{Escape(second)}", new { start = second.AddDays(1).AddHours(2) }));
        Assert.Equal((second, second.AddDays(1).AddHours(2), false), (moved.OriginalStart, moved.Start, moved.Cancelled));

        var cancelled = await ReadAsync<Occurrence>(await SendAsync(HttpMethod.Put, $"{url}/{Escape(third)}", new { cancelled = true }));
        Assert.True(cancelled.Cancelled);

        var series = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal(
            [(Monday, Monday, false), (second, second.AddDays(1).AddHours(2), false), (third, third, true), (Monday.AddDays(21), Monday.AddDays(21), false)],
            series.Select(item => (item.OriginalStart, item.Start, item.Cancelled)));

        // Kuralın vermediği bir zaman bir oluş değildir.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"{url}/{Escape(Monday.AddDays(3))}", new { cancelled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"{url}/{Escape(Monday.AddDays(28))}", new { cancelled = true })).StatusCode);

        // İstisna kaldırılınca oluş kurala geri döner.
        var restored = await ReadAsync<Occurrence>(await SendAsync(HttpMethod.Delete, $"{url}/{Escape(second)}"));
        Assert.Equal((second, second, false), (restored.OriginalStart, restored.Start, restored.Cancelled));

        var after = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal([false, false, true, false], after.Select(item => item.Cancelled));
        Assert.Equal(second, after[1].Start);
    }

    [Fact]
    public async Task Occurrences_FallOnTheListedWeekdays()
    {
        // Pazartesi, çarşamba ve cuma: bir hafta üç oluş verir; sayı oluşları sayar, haftaları değil.
        var weekly = await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Training",
            durationMinutes = 60,
            startsAt = Monday,
            recurrenceFrequency = "Weekly",
            recurrenceByDay = "MO,WE,FR",
            recurrenceCount = 6,
        }));

        Assert.Equal("MO,WE,FR", weekly.RecurrenceByDay);

        var url = $"/meetings/{weekly.Id}/occurrences";
        var days = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, url));
        Assert.Equal(
            new[] { 0, 2, 4, 7, 9, 11 }.Select(day => Monday.AddDays(day)),
            days.Select(item => item.Start));

        // Uzak bir aralık da aynı sırayı görür: sayı sınırı baştan sayılır.
        var late = await ReadAsync<List<Occurrence>>(await SendAsync(
            HttpMethod.Get, $"{url}?from={Escape(Monday.AddDays(8))}&to={Escape(Monday.AddDays(60))}"));
        Assert.Equal([Monday.AddDays(9), Monday.AddDays(11)], late.Select(item => item.Start));

        // Seri ilk oluştan önce başlamaz: çarşamba başlayan seride o haftanın pazartesisi yoktur.
        var wednesday = Monday.AddDays(2);
        var midweek = await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Midweek",
            durationMinutes = 30,
            startsAt = wednesday,
            recurrenceFrequency = "Weekly",
            recurrenceInterval = 2,
            recurrenceByDay = "MO,WE",
            recurrenceCount = 3,
        }));

        var biweekly = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, $"/meetings/{midweek.Id}/occurrences"));
        Assert.Equal([wednesday, Monday.AddDays(14), Monday.AddDays(16)], biweekly.Select(item => item.Start));

        // Günlere düşen tek bir oluş da ertelenebilir ve iptal edilebilir.
        var cancelled = await ReadAsync<Occurrence>(await SendAsync(HttpMethod.Put, $"{url}/{Escape(Monday.AddDays(2))}", new { cancelled = true }));
        Assert.True(cancelled.Cancelled);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"{url}/{Escape(Monday.AddDays(1))}", new { cancelled = true })).StatusCode);
    }

    [Fact]
    public async Task Occurrences_FallOnTheNthWeekdayOfTheMonth()
    {
        // Ayın ikinci salısı.
        var secondTuesday = new DateTimeOffset(2027, 1, 12, 10, 0, 0, TimeSpan.Zero);
        var board = await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Board",
            durationMinutes = 90,
            startsAt = secondTuesday,
            recurrenceFrequency = "Monthly",
            recurrenceByDay = "2TU",
            recurrenceCount = 3,
        }));

        var tuesdays = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, $"/meetings/{board.Id}/occurrences"));
        Assert.Equal(
            [secondTuesday, new(2027, 2, 9, 10, 0, 0, TimeSpan.Zero), new(2027, 3, 9, 10, 0, 0, TimeSpan.Zero)],
            tuesdays.Select(item => item.Start));

        // Ayın son cuması: eksi sıra sondan sayılır.
        var lastFriday = new DateTimeOffset(2027, 1, 29, 16, 0, 0, TimeSpan.Zero);
        var review = await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Month review",
            durationMinutes = 45,
            startsAt = lastFriday,
            recurrenceFrequency = "Monthly",
            recurrenceByDay = "-1FR",
            recurrenceCount = 3,
        }));

        var fridays = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, $"/meetings/{review.Id}/occurrences"));
        Assert.Equal(
            [lastFriday, new(2027, 2, 26, 16, 0, 0, TimeSpan.Zero), new(2027, 3, 26, 16, 0, 0, TimeSpan.Zero)],
            fridays.Select(item => item.Start));

        // Ayda o sıra yoksa o ay oluş yoktur: beşinci cuma Şubat 2027'de yok.
        var fifth = await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Fifth Friday",
            durationMinutes = 30,
            startsAt = lastFriday,
            recurrenceFrequency = "Monthly",
            recurrenceByDay = "5FR",
            recurrenceCount = 2,
        }));

        var rare = await ReadAsync<List<Occurrence>>(await SendAsync(
            HttpMethod.Get, $"/meetings/{fifth.Id}/occurrences?to={Escape(lastFriday.AddYears(1))}"));
        Assert.Equal([lastFriday, new(2027, 4, 30, 16, 0, 0, TimeSpan.Zero)], rare.Select(item => item.Start));
    }

    [Fact]
    public async Task Occurrences_KeepTheWallClockOfTheirTimeZone()
    {
        // Berlin'de 09.00: 28 Mart 2027'de yaz saatine geçilir, toplantı yine 09.00'dadır (UTC'de bir saat erken).
        var winter = new DateTimeOffset(2027, 3, 22, 8, 0, 0, TimeSpan.Zero);
        var body = new
        {
            title = "Berlin stand-up",
            durationMinutes = 15,
            startsAt = winter,
            recurrenceFrequency = "Weekly",
            recurrenceCount = 2,
            recurrenceTimeZone = (string?)"Europe/Berlin",
        };

        var zoned = await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Post, "/meetings", body));
        Assert.Equal("Europe/Berlin", zoned.RecurrenceTimeZone);

        var local = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, $"/meetings/{zoned.Id}/occurrences"));
        Assert.Equal([winter, new(2027, 3, 29, 7, 0, 0, TimeSpan.Zero)], local.Select(item => item.Start));

        // Saat dilimi verilmezse oluşlar ilk oluşun UTC farkını korur.
        var plain = await ReadAsync<MeetingResponse>(await SendAsync(HttpMethod.Post, "/meetings", body with { recurrenceTimeZone = null }));
        var utc = await ReadAsync<List<Occurrence>>(await SendAsync(HttpMethod.Get, $"/meetings/{plain.Id}/occurrences"));
        Assert.Equal([winter, winter.AddDays(7)], utc.Select(item => item.Start));
    }

    [Theory]
    [InlineData("Weekly", "XX", null, "recurrenceByDay")]
    [InlineData("Weekly", "MO,,FR", null, "recurrenceByDay")]
    [InlineData("Weekly", "2TU", null, "recurrenceByDay")]
    [InlineData("Monthly", "6TU", null, "recurrenceByDay")]
    [InlineData("Weekly", null, "Mars/Phobos", "recurrenceTimeZone")]
    public async Task InvalidRules_AreRejectedAtTheField(string frequency, string? byDay, string? timeZone, string field)
    {
        var response = await SendAsync(HttpMethod.Post, "/meetings", new
        {
            title = "Bad rule",
            durationMinutes = 30,
            startsAt = Monday,
            recurrenceFrequency = frequency,
            recurrenceByDay = byDay,
            recurrenceTimeZone = timeZone,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(CancellationToken));
    }

    [Fact]
    public async Task DomainLogic_IsWrittenAsAnEntityAction()
    {
        // Kombinatoryal varyant üretimi: seçeneklerin bütün birleşimleri, kaydın satırları olarak.
        var garment = await ReadAsync<GarmentResponse>(await SendAsync(HttpMethod.Post, "/garments", new
        {
            name = "Tee",
            garmentVariants = new[] { new { sku = "TEE-S-RED", size = "S", color = "red" } },
        }));

        var generated = await ReadAsync<GarmentResponse>(await SendAsync(
            HttpMethod.Post, $"/garments/{garment.Id}/generate-variants", new { sizes = new[] { "S", "M" }, colors = new[] { "red", "blue" } }));

        // Var olan birleşim korunur, eksikler eklenir; satırlar kayıtla birlikte kaydedilir.
        Assert.Equal(
            ["TEE-S-RED", "TEE-S-BLUE", "TEE-M-RED", "TEE-M-BLUE"],
            generated.GarmentVariants.Select(variant => variant.Sku));
        Assert.Equal(garment.GarmentVariants[0].Id, generated.GarmentVariants[0].Id);

        var stored = await ReadAsync<GarmentResponse>(await SendAsync(HttpMethod.Get, $"/garments/{garment.Id}"));
        Assert.Equal(4, stored.GarmentVariants.Count);

        // Aksiyonun isteği de doğrulanır.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, $"/garments/{garment.Id}/generate-variants", new { sizes = Array.Empty<string>(), colors = new[] { "red" } })).StatusCode);
    }

    private static string Escape(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);

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
