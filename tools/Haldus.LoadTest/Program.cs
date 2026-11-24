using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

// Çalışan örnek mağazayı (samples/Haldus.Sample.Shop) gerçek HTTP üzerinden yük altında ölçer. Amaç mutlak bir
// kapasite sayısı değil, kilide dayanan özelliklerin (boşluksuz numara, sıra, bakiye, toplanan alan, türetme,
// dönem) eşzamanlılık arttıkça nasıl davrandığını yan yana görmektir. Nasıl çalıştırılacağı README.md dosyasındadır.

var options = Options.Parse(args);

if (options is null)
{
    Options.PrintUsage();
    return 1;
}

var selected = Scenarios.All
    .Where(scenario => options.Selected.Count == 0 || options.Selected.Contains(scenario.Name))
    .ToList();

var unknown = options.Selected.Except(Scenarios.All.Select(scenario => scenario.Name)).Except(["export"]).ToList();

if (unknown.Count > 0)
{
    Console.Error.WriteLine($"Unknown scenario: {string.Join(", ", unknown)}");
    Options.PrintUsage();
    return 1;
}

using (var probe = new Api(options.Url, connections: 1))
{
    if (await probe.FindProblemAsync() is { } problem)
    {
        Console.Error.WriteLine(problem);
        return 1;
    }
}

Console.WriteLine($"Target: {options.Url} · {options.Operations} operations per run (+{options.Warmup} warm-up)");
Console.WriteLine();
Console.WriteLine("| Scenario | Concurrency | Operations | Failed | Ops/s | p50 ms | p95 ms | p99 ms | Max ms |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");

foreach (var scenario in selected)
{
    foreach (var concurrency in options.Concurrency)
    {
        using var api = new Api(options.Url, concurrency);
        var result = await Runner.RunAsync(scenario.Create(), api, options.Operations, options.Warmup, concurrency);
        Console.WriteLine(result.ToRow(scenario.Name, concurrency));
    }
}

if (options.Selected.Count == 0 || options.Selected.Contains("export"))
{
    using var api = new Api(options.Url, connections: 32);
    Console.WriteLine();
    Console.WriteLine(await Export.MeasureAsync(api, options.ExportProducts));
}

return 0;

/// <summary>Komut satırı seçenekleri.</summary>
internal sealed record Options(
    Uri Url,
    int Operations,
    int Warmup,
    IReadOnlyList<int> Concurrency,
    IReadOnlySet<string> Selected,
    int ExportProducts)
{
    public static Options? Parse(string[] args)
    {
        var url = new Uri("http://localhost:5080");
        var operations = 2000;
        var warmup = 100;
        IReadOnlyList<int> concurrency = [1, 8, 32];
        var scenarios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exportProducts = 20_000;

        for (var index = 0; index < args.Length; index++)
        {
            var value = index + 1 < args.Length ? args[index + 1] : null;

            switch (args[index])
            {
                case "--url" when Uri.TryCreate(value, UriKind.Absolute, out var parsed):
                    url = parsed;
                    break;
                case "--operations" when int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed > 0:
                    operations = parsed;
                    break;
                case "--warmup" when int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0:
                    warmup = parsed;
                    break;
                case "--concurrency" when ParseList(value) is { Count: > 0 } parsed:
                    concurrency = parsed;
                    break;
                case "--scenario" when !string.IsNullOrWhiteSpace(value):
                    scenarios.UnionWith(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--export-products" when int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0:
                    exportProducts = parsed;
                    break;
                default:
                    return null;
            }

            index++;
        }

        return new Options(url, operations, warmup, concurrency, scenarios, exportProducts);
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: Haldus.LoadTest [--url http://localhost:5080] [--operations 2000] [--warmup 100]");
        Console.Error.WriteLine("                       [--concurrency 1,8,32] [--scenario name,name] [--export-products 20000]");
        Console.Error.WriteLine($"Scenarios: {string.Join(", ", Scenarios.All.Select(scenario => scenario.Name))}, export");
    }

    private static List<int>? ParseList(string? value)
    {
        var result = new List<int>();

        foreach (var part in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return null;
            }

            result.Add(parsed);
        }

        return result;
    }
}

/// <summary>
/// Örnek mağazaya giden istekler. Her çalıştırma kendi kiracısıyla çalışır; kimlik başlıklarla verilir, bu yüzden
/// backend <c>Shop:DemoIdentity=true</c> ile başlatılmış olmalıdır.
/// </summary>
internal sealed class Api : IDisposable
{
    private readonly HttpClient _client;

    public Api(Uri url, int connections)
    {
        _client = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = Math.Max(connections, 1) })
        {
            BaseAddress = url,
            Timeout = TimeSpan.FromMinutes(5),
        };
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.NewGuid().ToString());
        _client.DefaultRequestHeaders.Add("X-User-Id", "load-test");
    }

    public Task<HttpResponseMessage> GetAsync(string url) => _client.GetAsync(url);

    public async Task<HttpResponseMessage> PostAsync(string url, object? body = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await _client.SendAsync(request);
    }

    /// <summary>Ölçülen işlem: yalnızca durum kodu gerekir, gövde okunup atılır.</summary>
    public async Task<HttpStatusCode> SendAsync(string url, object? body = null, string? idempotencyKey = null)
    {
        using var response = await PostAsync(url, body, idempotencyKey);
        await response.Content.CopyToAsync(Stream.Null);
        return response.StatusCode;
    }

    /// <summary>
    /// Hazırlık isteği: kaydı oluşturur ve kimliğini döndürür; başarısızsa çalıştırma durur. Kimlik JSON değeri olarak
    /// taşınır: sayı olan kimlik (ör. kategori) sonraki isteğin gövdesine yine sayı olarak yazılmalıdır.
    /// </summary>
    public async Task<JsonElement> CreateAsync(string url, object body, string? idempotencyKey = null)
    {
        using var response = await PostAsync(url, body, idempotencyKey);
        using var document = await ReadAsync(response, url);
        return document.RootElement.GetProperty("id").Clone();
    }

    public async Task<JsonDocument> ReadAsync(string url)
    {
        using var response = await GetAsync(url);
        return await ReadAsync(response, url);
    }

    /// <summary>Backend'e ulaşılamıyorsa veya demo kimliği kapalıysa nedenini döndürür.</summary>
    public async Task<string?> FindProblemAsync()
    {
        try
        {
            // Sipariş kiracıya aittir: kiracı başlıktan okunmuyorsa bu istek reddedilir.
            using var response = await GetAsync("/orders?pageSize=1");

            return response.StatusCode switch
            {
                HttpStatusCode.OK => null,
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                    "The backend does not read the identity from request headers. Start it with Shop:DemoIdentity=true.",
                var status => $"The backend answered {(int)status} to GET /orders.",
            };
        }
        catch (HttpRequestException exception)
        {
            return $"The backend is not reachable at {_client.BaseAddress}: {exception.Message}";
        }
    }

    public void Dispose() => _client.Dispose();

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, string url)
    {
        var text = await response.Content.ReadAsStringAsync();

        return response.IsSuccessStatusCode
            ? JsonDocument.Parse(text)
            : throw new InvalidOperationException($"Setup request {url} failed with {(int)response.StatusCode}: {text}");
    }
}

/// <summary>
/// Bir senaryo. Hazırlık (<see cref="SetupAsync"/>, <see cref="PrepareAsync"/>) ölçülmez; yalnızca
/// <see cref="ExecuteAsync"/> ölçülür. Her çalıştırma yeni bir nesneyle yapılır.
/// </summary>
internal abstract class Scenario
{
    /// <summary>Çalıştırma başına bir kez: paylaşılan kayıtlar.</summary>
    public virtual Task SetupAsync(Api api, int operations, int concurrency) => Task.CompletedTask;

    /// <summary>İşlem başına bir kez, ölçümden önce: işlemin ihtiyaç duyduğu kayıt.</summary>
    public virtual Task PrepareAsync(Api api, int index) => Task.CompletedTask;

    public abstract Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker);
}

internal sealed record ScenarioDefinition(string Name, Func<Scenario> Create);

internal static class Scenarios
{
    public static readonly IReadOnlyList<ScenarioDefinition> All =
    [
        new("read", () => new ReadList()),
        new("create", () => new CreatePerson()),
        new("numbered-gapless", () => new NumberedGapless()),
        new("numbered-sequence", () => new NumberedSequence()),
        new("orderable-append", () => new OrderableAppend()),
        new("orderable-move", () => new OrderableMove()),
        new("balance-hot", () => new BalanceAdjust(spread: false)),
        new("balance-spread", () => new BalanceAdjust(spread: true)),
        new("balance-hold", () => new BalanceHold()),
        new("rollup-hot", () => new Rollup(spread: false)),
        new("rollup-spread", () => new Rollup(spread: true)),
        new("derived", () => new Derived()),
        new("effective-dated", () => new EffectiveDated()),
    ];

    /// <summary>Taban okuma: 20 kayıtlık bir sayfa (toplam sayımıyla birlikte).</summary>
    private sealed class ReadList : Scenario
    {
        public override async Task SetupAsync(Api api, int operations, int concurrency)
        {
            using var page = await api.ReadAsync("/products?pageSize=1");

            for (var missing = 200 - page.RootElement.GetProperty("totalCount").GetInt64(); missing > 0; missing--)
            {
                await api.CreateAsync("/products", new { name = $"Seed {Guid.NewGuid():N}", price = 10 });
            }
        }

        public override async Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker)
        {
            using var response = await api.GetAsync("/products?pageSize=20");
            await response.Content.CopyToAsync(Stream.Null);
            return response.StatusCode;
        }
    }

    /// <summary>Taban yazma: kilidi ve sayacı olmayan en yalın kayıt.</summary>
    private sealed class CreatePerson : Scenario
    {
        public override Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker) =>
            api.SendAsync("/persons", new { fullName = $"Person {index}" });
    }

    /// <summary>Boşluksuz numara: bütün istekler tek bir sayaç satırını bekler (<c>EMP-{seq:5}</c>).</summary>
    private sealed class NumberedGapless : Scenario
    {
        private readonly ConcurrentDictionary<int, JsonElement> _people = new();

        public override async Task PrepareAsync(Api api, int index) =>
            _people[index] = await api.CreateAsync("/persons", new { fullName = $"Staff {index}" });

        public override Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker) =>
            api.SendAsync("/staff-members", new { personId = _people[index] });
    }

    /// <summary>
    /// Sequence'ten gelen numara (<c>gapless: false</c>). İade ayrıca idempotency anahtarı ister ve bir siparişe
    /// referans verir; bu yüzden boşluksuz numarayla bire bir kıyas değildir.
    /// </summary>
    private sealed class NumberedSequence : Scenario
    {
        private JsonElement _order;

        public override async Task SetupAsync(Api api, int operations, int concurrency) =>
            _order = await api.CreateAsync("/orders", new { customerName = "Load" });

        public override Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker) =>
            api.SendAsync("/refunds", new { orderId = _order, amount = 1 }, idempotencyKey: Guid.NewGuid().ToString());
    }

    /// <summary>Sıralı kaynağa ekleme: her ekleme ağacın kilidini alır ve sona yerleşir.</summary>
    private sealed class OrderableAppend : Scenario
    {
        private JsonElement _parent;

        public override async Task SetupAsync(Api api, int operations, int concurrency) =>
            _parent = await api.CreateAsync("/categories", new { name = $"Append {Guid.NewGuid():N}" });

        public override Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker) =>
            api.SendAsync("/categories", new { name = $"Child {index}", parentId = _parent });
    }

    /// <summary>Sıralı kaynakta taşıma: 100 kardeşten biri başa alınır, hepsi yeniden numaralanır.</summary>
    private sealed class OrderableMove : Scenario
    {
        private const int Siblings = 100;
        private readonly List<JsonElement> _children = [];

        public override async Task SetupAsync(Api api, int operations, int concurrency)
        {
            var parent = await api.CreateAsync("/categories", new { name = $"Move {Guid.NewGuid():N}" });

            for (var index = 0; index < Siblings; index++)
            {
                _children.Add(await api.CreateAsync("/categories", new { name = $"Sibling {index}", parentId = parent }));
            }
        }

        public override Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker) =>
            api.SendAsync($"/categories/{_children[index % Siblings]}/move", new { position = 0 });
    }

    /// <summary>
    /// Bakiye: her komut bakiyenin kilidini alır. "hot" bütün istekleri tek bir anahtara, "spread" her işçiyi kendi
    /// anahtarına gönderir; ikisinin farkı kilidin anahtar başına olduğunu gösterir.
    /// </summary>
    private sealed class BalanceAdjust(bool spread) : Scenario
    {
        private readonly string _key = $"load-{Guid.NewGuid():N}";

        public override async Task SetupAsync(Api api, int operations, int concurrency)
        {
            for (var worker = 0; worker < (spread ? concurrency : 1); worker++)
            {
                (await api.SendAsync($"/inventories/{KeyOf(worker)}/adjust", new { delta = 1_000_000 })).EnsureSuccess();
            }
        }

        public override Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker) =>
            api.SendAsync($"/inventories/{KeyOf(worker)}/adjust", new { delta = -1 });

        private string KeyOf(int worker) => spread ? $"{_key}-{worker}" : _key;
    }

    /// <summary>Tutma: aynı bakiyede ayır ve tahsil et (iki istek tek işlem sayılır).</summary>
    private sealed class BalanceHold : Scenario
    {
        private readonly string _key = $"hold-{Guid.NewGuid():N}";

        public override async Task SetupAsync(Api api, int operations, int concurrency) =>
            (await api.SendAsync($"/inventories/{_key}/adjust", new { delta = 1_000_000 })).EnsureSuccess();

        public override async Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker)
        {
            using var placed = await api.PostAsync($"/inventories/{_key}/holds", new { quantity = 1 });

            if (!placed.IsSuccessStatusCode)
            {
                return placed.StatusCode;
            }

            using var hold = JsonDocument.Parse(await placed.Content.ReadAsStringAsync());
            return await api.SendAsync($"/inventories/{_key}/holds/{hold.RootElement.GetProperty("id")}/capture");
        }
    }

    /// <summary>
    /// Toplanan alan: her yanıt, tartışmanın sayısını ve puanını kilit altında baştan saydırır. "hot" tek bir
    /// tartışmaya, "spread" işçi başına bir tartışmaya yazar. Yanıt ayrıca bir olay yazar (<c>PublishesEvents</c>).
    /// </summary>
    private sealed class Rollup(bool spread) : Scenario
    {
        private readonly List<JsonElement> _discussions = [];

        public override async Task SetupAsync(Api api, int operations, int concurrency)
        {
            for (var worker = 0; worker < (spread ? concurrency : 1); worker++)
            {
                _discussions.Add(await api.CreateAsync("/discussions", new { title = $"Load {worker}" }));
            }
        }

        public override Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker) =>
            api.SendAsync($"/discussions/{_discussions[spread ? worker : 0]}/replies", new { text = $"Reply {index}", score = 1 });
    }

    /// <summary>Türetme: her stok hareketi, kaynak başına tek kilidin altında stok seviyesini günceller.</summary>
    private sealed class Derived : Scenario
    {
        private JsonElement _product;

        public override async Task SetupAsync(Api api, int operations, int concurrency) =>
            _product = await api.CreateAsync("/products", new { name = $"Stocked {Guid.NewGuid():N}", price = 10 });

        public override Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker) =>
            api.SendAsync("/stock-movements", new { productId = _product, quantity = 1 });
    }

    /// <summary>
    /// Dönemli kayıt: aynı tedarikçiye ve para birimine birer günlük, çakışmayan dönemler yazılır; her yazma kapsamı
    /// kilitler ve dönemi kapsamdaki öteki kayıtlarla sınar. Kapsam çalıştırma boyunca büyür.
    /// </summary>
    private sealed class EffectiveDated : Scenario
    {
        private static readonly DateOnly First = new(2000, 1, 1);
        private JsonElement _supplier;

        public override async Task SetupAsync(Api api, int operations, int concurrency) =>
            _supplier = await api.CreateAsync("/suppliers", new { name = $"Load {Guid.NewGuid():N}" });

        public override Task<HttpStatusCode> ExecuteAsync(Api api, int index, int worker) =>
            api.SendAsync($"/suppliers/{_supplier}/supplier-rates", new
            {
                currency = "TRY",
                unitPrice = 1,
                effectiveFrom = First.AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                effectiveTo = First.AddDays(index + 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });
    }

    private static void EnsureSuccess(this HttpStatusCode status)
    {
        if ((int)status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"Setup request failed with {(int)status}.");
        }
    }
}

internal static class Runner
{
    public static async Task<Result> RunAsync(Scenario scenario, Api api, int operations, int warmup, int concurrency)
    {
        var total = warmup + operations;

        await scenario.SetupAsync(api, total, concurrency);
        await Parallel.ForEachAsync(
            Enumerable.Range(0, total),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (index, _) => await scenario.PrepareAsync(api, index));

        // Isınma: JIT, bağlantı havuzları ve sorgu planları ölçüme karışmasın.
        await DriveAsync(scenario, api, from: 0, to: warmup, concurrency, durations: null, statuses: null);

        var durations = new long[operations];
        var statuses = new ConcurrentDictionary<int, int>();
        var started = Stopwatch.GetTimestamp();

        await DriveAsync(scenario, api, from: warmup, to: total, concurrency, durations, statuses);

        return new Result(Stopwatch.GetElapsedTime(started), durations, statuses);
    }

    private static Task DriveAsync(
        Scenario scenario,
        Api api,
        int from,
        int to,
        int concurrency,
        long[]? durations,
        ConcurrentDictionary<int, int>? statuses)
    {
        var next = from - 1;

        return Task.WhenAll(Enumerable.Range(0, concurrency).Select(worker => Task.Run(async () =>
        {
            while (Interlocked.Increment(ref next) is var index && index < to)
            {
                var begun = Stopwatch.GetTimestamp();
                int status;

                try
                {
                    status = (int)await scenario.ExecuteAsync(api, index, worker);
                }
                catch (HttpRequestException)
                {
                    // Bağlantı hatası: durum kodu yoktur.
                    status = 0;
                }

                if (durations is not null)
                {
                    durations[index - from] = Stopwatch.GetTimestamp() - begun;
                    statuses?.AddOrUpdate(status, 1, (_, count) => count + 1);
                }
            }
        })));
    }
}

internal sealed record Result(TimeSpan Elapsed, long[] Durations, IReadOnlyDictionary<int, int> Statuses)
{
    public string ToRow(string name, int concurrency)
    {
        var sorted = Durations.Order().ToArray();
        var failures = Statuses.Where(status => status.Key is < 200 or >= 300).OrderBy(status => status.Key).ToList();
        var failed = failures.Count == 0
            ? "0"
            : $"{failures.Sum(status => status.Value)} ({string.Join(", ", failures.Select(status => $"{status.Key}×{status.Value}"))})";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"| {name} | {concurrency} | {sorted.Length} | {failed} | {sorted.Length / Elapsed.TotalSeconds:F0} | {Percentile(sorted, 0.50):F1} | {Percentile(sorted, 0.95):F1} | {Percentile(sorted, 0.99):F1} | {Milliseconds(sorted[^1]):F1} |");
    }

    private static double Percentile(long[] sorted, double quantile) =>
        Milliseconds(sorted[Math.Clamp((int)Math.Ceiling(quantile * sorted.Length) - 1, 0, sorted.Length - 1)]);

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// Dışa aktarma: dosyayı bellekte kuran yolun büyük bir tabloda ne kadar sürdüğü. Ürün sayısı istenen sayıya
/// tamamlanır, bir dışa aktarma işi başlatılır ve bitmesi beklenir.
/// </summary>
internal static class Export
{
    public static async Task<string> MeasureAsync(Api api, int products)
    {
        using (var page = await api.ReadAsync("/products?pageSize=1"))
        {
            var missing = (int)Math.Max(0, products - page.RootElement.GetProperty("totalCount").GetInt64());

            await Parallel.ForEachAsync(
                Enumerable.Range(0, missing),
                new ParallelOptions { MaxDegreeOfParallelism = 32 },
                async (index, _) => await api.CreateAsync("/products", new { name = $"Export {index} {Guid.NewGuid():N}", price = 10 }));
        }

        long total;

        using (var page = await api.ReadAsync("/products?pageSize=1"))
        {
            total = page.RootElement.GetProperty("totalCount").GetInt64();
        }

        var started = Stopwatch.GetTimestamp();
        var job = await api.CreateAsync("/product-exports", new { format = "csv" });
        string status;

        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            using var current = await api.ReadAsync($"/product-exports/{job}");
            status = current.RootElement.GetProperty("status").GetString() ?? "";
        }
        while (status is "Queued" or "Running");

        var elapsed = Stopwatch.GetElapsedTime(started);
        var downloadStarted = Stopwatch.GetTimestamp();
        using var content = await api.GetAsync($"/product-exports/{job}/content");
        var bytes = (await content.Content.ReadAsByteArrayAsync()).LongLength;
        var download = Stopwatch.GetElapsedTime(downloadStarted);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Export of {total} products as CSV: job {status} in {elapsed.TotalSeconds:F2} s, {bytes / 1024.0 / 1024.0:F2} MiB, downloaded in {download.TotalMilliseconds:F0} ms (HTTP {(int)content.StatusCode}).");
    }
}
