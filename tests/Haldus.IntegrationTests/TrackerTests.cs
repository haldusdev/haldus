using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Tracker;

namespace Haldus.IntegrationTests;

/// <summary>
/// Belgelerdeki eğitimin (<c>docs/getting-started/tutorial.md</c>) uygulaması. Eğitim, bu uygulamadan alınmış istek ve
/// yanıtları gösterir; bu test onların hâlâ doğru olduğunu denetler: migration'lar boş bir veritabanına uygulanır ve
/// eğitimdeki akış baştan sona yürütülür. Burada bir şey değişirse eğitim de güncellenmelidir.
/// </summary>
public sealed class TrackerTests(TrackerFixture fixture) : IClassFixture<TrackerFixture>
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TutorialFlow_BehavesAsDocumented()
    {
        using var client = fixture.CreateClient();

        // 5. Uçları denemek
        var created = await client.PostAsJsonAsync("/projects", new { name = "Web sitesi", description = "Kurumsal sitenin yenilenmesi" }, CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = await ReadAsync(created);
        var projectId = project.GetProperty("id").GetGuid();
        Assert.Equal($"/projects/{projectId}", created.Headers.Location?.OriginalString);

        using (var invalid = new HttpRequestMessage(HttpMethod.Post, "/projects") { Content = JsonContent.Create(new { name = "" }) })
        {
            invalid.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("tr"));
            var rejected = await client.SendAsync(invalid, CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var problem = await ReadAsync(rejected);
            Assert.Equal("Bir veya daha fazla doğrulama hatası oluştu.", problem.GetProperty("title").GetString());
            Assert.Equal("Name alanı zorunludur.", problem.GetProperty("errors").GetProperty("name")[0].GetString());
        }

        var list = await ReadAsync(await client.GetAsync("/projects", CancellationToken));
        Assert.Equal((1, 20, 1), (list.GetProperty("page").GetInt32(), list.GetProperty("pageSize").GetInt32(), list.GetProperty("totalCount").GetInt32()));

        // 7–8. İşler, durum ve numara
        var issues = $"/projects/{projectId}/issues";
        var first = await client.PostAsJsonAsync(issues, new { title = "Giriş sayfası açılmıyor", priority = 4, assignee = "ayse" }, CancellationToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var issue = await ReadAsync(first);
        var issueId = issue.GetProperty("id").GetGuid();
        Assert.Equal(("Open", "ISS-00001", projectId), (issue.GetProperty("status").GetString(), issue.GetProperty("number").GetString(), issue.GetProperty("projectId").GetGuid()));
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        var second = await ReadAsync(await client.PostAsJsonAsync(issues, new { title = "Logo eski", priority = 2 }, CancellationToken));
        Assert.Equal("ISS-00002", second.GetProperty("number").GetString());

        var issueUrl = $"{issues}/{issueId}";

        // 9. Eşzamanlılık: sürümsüz istek 428, eski sürüm 412
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.PostAsync($"{issueUrl}/start", null, CancellationToken)).StatusCode);

        var started = await SendAsync(client, HttpMethod.Post, $"{issueUrl}/start", etag);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal("InProgress", (await ReadAsync(started)).GetProperty("status").GetString());
        etag = started.Headers.ETag;

        var again = await SendAsync(client, HttpMethod.Post, $"{issueUrl}/start", etag);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("'start' is not allowed while the record is in the 'InProgress' state.", (await ReadAsync(again)).GetProperty("detail").GetString());

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SendAsync(client, HttpMethod.Patch, issueUrl, new EntityTagHeaderValue("\"0\""), new { priority = 5 })).StatusCode);

        // Geçişin istek tipi de doğrulanır.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Post, $"{issueUrl}/resolve", etag, new { resolution = "" })).StatusCode);

        var resolved = await SendAsync(client, HttpMethod.Post, $"{issueUrl}/resolve", etag, new { resolution = "Oturum çerezi düzeltildi" });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var resolvedBody = await ReadAsync(resolved);
        Assert.Equal(("Resolved", "Oturum çerezi düzeltildi"), (resolvedBody.GetProperty("status").GetString(), resolvedBody.GetProperty("resolution").GetString()));
        etag = resolved.Headers.ETag;

        // Çözülmüş iş düzenlenemez ve silinemez.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Patch, issueUrl, etag, new { priority = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Delete, issueUrl, etag)).StatusCode);

        // 9. Süzme ve sıralama
        var assigned = await ReadAsync(await client.GetAsync($"{issues}?assignee=ayse&sort=-priority", CancellationToken));
        Assert.Equal(1, assigned.GetProperty("totalCount").GetInt32());

        var badSort = await client.GetAsync($"{issues}?sort=title", CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, badSort.StatusCode);
        Assert.Equal("Cannot sort by 'title'. Sortable fields: priority, createdAt.", (await ReadAsync(badSort)).GetProperty("errors").GetProperty("sort")[0].GetString());

        var open = await ReadAsync(await client.GetAsync($"{issues}?status=Open", CancellationToken));
        Assert.Equal(1, open.GetProperty("totalCount").GetInt32());

        // Toplanan alan ve API şeması
        Assert.Equal(2, (await ReadAsync(await client.GetAsync($"/projects/{projectId}", CancellationToken))).GetProperty("issueCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json", CancellationToken)).StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, EntityTagHeaderValue? etag, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);

        if (etag is not null)
        {
            request.Headers.IfMatch.Add(etag);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await client.SendAsync(request, CancellationToken);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
}

/// <summary>Eğitimin uygulaması, kendi PostgreSQL kapsayıcısında ve migration'ları uygulanmış olarak.</summary>
public sealed class TrackerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private WebApplicationFactory<AppDbContext>? _factory;

    public HttpClient CreateClient() => _factory!.CreateClient();

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();

        _factory = new WebApplicationFactory<AppDbContext>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString()));

        // Eğitimdeki gibi şema migration'larla kurulur (EnsureCreated değil): migration'lar da sınanmış olur.
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _database.DisposeAsync();
    }
}
