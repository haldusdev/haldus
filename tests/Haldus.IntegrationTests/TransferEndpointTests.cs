using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Haldus.Sample.Shop.Features.Catalog;

namespace Haldus.IntegrationTests;

/// <summary>İçe ve dışa aktarma işleri (<c>Exports</c>, <c>Imports</c>).</summary>
[Collection(ShopCollection.Name)]
public sealed class TransferEndpointTests(ShopFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client = fixture.CreateClient();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Export_WritesTheRecordsToAFileThatIsDownloadedFromTheJob()
    {
        var marker = $"export-{Guid.NewGuid():N}";
        var plain = await CreateProductAsync($"{marker} plain", 10);

        // Virgül, tırnak, satır sonu ve formül gibi görünen değerler dosyayı bozmamalı, açıldığı yerde de çalışmamalıdır.
        var tricky = await CreateProductAsync($"=SUM(1,2) \"{marker}\"", 2.5m, description: "line one\nline two");

        var accepted = await SendAsync(HttpMethod.Post, "/product-exports", new { });
        var job = await ReadAsync<ProductExportResponse>(accepted);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        // İş bitmeden dosya yoktur.
        var finished = await WaitForAsync<ProductExportResponse>($"/product-exports/{job.Id}", export => export.Status is JobStatus.Succeeded or JobStatus.Failed);
        Assert.Equal((JobStatus.Succeeded, null), (finished.Status, finished.Error));
        Assert.True(finished.Rows >= 2);
        Assert.True(finished.Size > 0);
        Assert.EndsWith(".csv", finished.FileName);

        var download = await SendAsync(HttpMethod.Get, $"/product-exports/{job.Id}/content");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/csv", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);

        var csv = await download.Content.ReadAsStringAsync(CancellationToken);
        var lines = csv.Split("\r\n");
        Assert.StartsWith("id,name,price", lines[0].TrimStart('\uFEFF'));
        Assert.Contains(lines, line => line.StartsWith($"{plain.Id},{marker} plain,10", StringComparison.Ordinal));

        // Formül gibi başlayan metin kesme işaretiyle metne çevrilir; tırnaklar çiftlenir, satır sonu hücrenin içinde kalır.
        Assert.Contains($"{tricky.Id},\"'=SUM(1,2) \"\"{marker}\"\"\",2.5,\"line one\nline two\"", csv);
        Assert.DoesNotContain($",=SUM(1,2)", csv);

        // JSON biçimi, kayıtların yanıt biçimindeki dizisidir.
        var jsonJob = await ReadAsync<ProductExportResponse>(await SendAsync(HttpMethod.Post, "/product-exports", new { format = "json" }));
        var jsonDone = await WaitForAsync<ProductExportResponse>($"/product-exports/{jsonJob.Id}", export => export.Status is JobStatus.Succeeded or JobStatus.Failed);
        Assert.Equal(JobStatus.Succeeded, jsonDone.Status);

        var json = await (await SendAsync(HttpMethod.Get, $"/product-exports/{jsonJob.Id}/content")).Content.ReadFromJsonAsync<List<ProductResponse>>(CancellationToken);
        Assert.Contains(json!, product => product.Id == tricky.Id && product.Name == $"=SUM(1,2) \"{marker}\"" && product.Description == "line one\nline two");

        // Bilinmeyen biçim işi başarısız yapar; başarısız işin dosyası yoktur.
        var badJob = await ReadAsync<ProductExportResponse>(await SendAsync(HttpMethod.Post, "/product-exports", new { format = "xml" }));
        var bad = await WaitForAsync<ProductExportResponse>($"/product-exports/{badJob.Id}", export => export.Status is JobStatus.Succeeded or JobStatus.Failed);
        Assert.Equal((JobStatus.Failed, "The format must be 'csv' or 'json'."), (bad.Status, bad.Error));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/product-exports/{badJob.Id}/content")).StatusCode);
    }

    [Fact]
    public async Task Import_CreatesEveryRowThroughTheRulesOfTheResource()
    {
        var marker = $"import-{Guid.NewGuid():N}";
        var csv = new StringBuilder()
            .Append("name,price,description,metadata\r\n")
            .Append($"{marker} one,10.5,\"With a comma, and a \"\"quote\"\"\",\r\n")
            .Append($"{marker} two,20,,\"{{\"\"origin\"\":\"\"import\"\"}}\"\r\n")
            .ToString();

        var accepted = await SendAsync(HttpMethod.Post, "/product-imports", new { format = "csv", content = csv });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        // İçerik işin girdisidir; yanıtta gösterilmez.
        Assert.DoesNotContain("content", await accepted.Content.ReadAsStringAsync(CancellationToken), StringComparison.OrdinalIgnoreCase);

        var job = await ReadAsync<ProductImportResponse>(accepted);
        var finished = await WaitForAsync<ProductImportResponse>($"/product-imports/{job.Id}", import => import.Status is JobStatus.Succeeded or JobStatus.Failed);
        Assert.Equal((JobStatus.Succeeded, 2, 0, null), (finished.Status, finished.Imported, finished.Failed, finished.Errors));

        var created = await FindAsync(marker);
        Assert.Equal(2, created.Count);

        var one = Assert.Single(created, product => product.Name == $"{marker} one");
        var two = Assert.Single(created, product => product.Name == $"{marker} two");
        Assert.Equal((10.5m, "With a comma, and a \"quote\""), (one.Price, one.Description));
        Assert.Equal((20m, null, "import"), (two.Price, two.Description, two.Metadata["origin"]));

        // JSON içerik aynı kayıt biçimindedir.
        var jsonJob = await ReadAsync<ProductImportResponse>(await SendAsync(HttpMethod.Post, "/product-imports", new
        {
            format = "json",
            content = JsonSerializer.Serialize(new[] { new { name = $"{marker} three", price = 30 } }),
        }));
        var jsonDone = await WaitForAsync<ProductImportResponse>($"/product-imports/{jsonJob.Id}", import => import.Status is JobStatus.Succeeded or JobStatus.Failed);
        Assert.Equal((JobStatus.Succeeded, 1), (jsonDone.Status, jsonDone.Imported));
        Assert.Equal(3, (await FindAsync(marker)).Count);
    }

    [Fact]
    public async Task Import_CreatesNothingWhenAnyRowIsRejected()
    {
        var marker = $"import-{Guid.NewGuid():N}";
        var csv = new StringBuilder()
            .Append("name,price,categoryId\n")
            .Append($"{marker} good,10,\n")
            .Append($"{marker} no category,10,{Guid.NewGuid()}\n")
            .Append($",5,\n")
            .Append($"{marker} bad price,not-a-number,\n")
            .ToString();

        var job = await ReadAsync<ProductImportResponse>(await SendAsync(HttpMethod.Post, "/product-imports", new { content = csv }));
        var finished = await WaitForAsync<ProductImportResponse>($"/product-imports/{job.Id}", import => import.Status is JobStatus.Succeeded or JobStatus.Failed);

        // İş çalıştı ve raporunu verdi; ama bir satır bile reddedildiği için hiçbir kayıt oluşmadı.
        Assert.Equal((JobStatus.Succeeded, 0, 3), (finished.Status, finished.Imported, finished.Failed));
        Assert.Empty(await FindAsync(marker));

        var errors = finished.Errors!.Split('\n');
        Assert.Equal(3, errors.Length);
        Assert.StartsWith("Row 2: ", errors[0]);
        Assert.Contains("categoryId", errors[0]);
        Assert.StartsWith("Row 3: ", errors[1]);
        Assert.Contains("name", errors[1], StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Row 4: ", errors[2]);
        Assert.Contains("price", errors[2], StringComparison.OrdinalIgnoreCase);

        // Okunamayan içerik işi başarısız yapar.
        var brokenJob = await ReadAsync<ProductImportResponse>(await SendAsync(HttpMethod.Post, "/product-imports", new { format = "json", content = "{ not json" }));
        var broken = await WaitForAsync<ProductImportResponse>($"/product-imports/{brokenJob.Id}", import => import.Status is JobStatus.Succeeded or JobStatus.Failed);
        Assert.Equal((JobStatus.Failed, "The content is not a valid JSON list of records."), (broken.Status, broken.Error));

        // İçerik zorunludur.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/product-imports", new { format = "csv" })).StatusCode);
    }

    private async Task<ProductResponse> CreateProductAsync(string name, decimal price, string? description = null) =>
        await ReadAsync<ProductResponse>(await SendAsync(HttpMethod.Post, "/products", new { name, price, description }));

    private async Task<List<ProductResponse>> FindAsync(string marker)
    {
        var found = await ReadAsync<PagedResult<ProductResponse>>(await SendAsync(HttpMethod.Post, "/products/search", new
        {
            where = new { name = new { startsWith = marker } },
            pageSize = 100,
        }));

        return [.. found.Items];
    }

    private async Task<T> WaitForAsync<T>(string url, Func<T, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        T last;

        do
        {
            last = await ReadAsync<T>(await SendAsync(HttpMethod.Get, url));

            if (condition(last))
            {
                return last;
            }

            await Task.Delay(100, CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail($"The job at {url} did not reach the expected state in time. Last seen: {last}");
        return last;
    }

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