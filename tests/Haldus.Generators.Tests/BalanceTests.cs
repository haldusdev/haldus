namespace Haldus.Generators.Tests;

/// <summary>Bakiyeler (<c>MapBalance</c>).</summary>
public sealed class BalanceTests
{
    private const string Entities = """
        namespace Shop
        {
            public partial class Inventory;

            public partial class Product
            {
                public string Name { get; set; } = "";
            }

            public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
        }
        """;

    private static GeneratorRun Run(string body) =>
        GeneratorHarness.Run($$"""
            {{Entities}}

            namespace Shop
            {
                public static class Startup
                {
                    public static void Configure(IServiceCollection services) => services.AddHaldus<AppDbContext>();

                    public static void Map(IEndpointRouteBuilder app)
                    {
                        {{body}}
                    }
                }
            }
            """);

    [Fact]
    public void MapBalance_GeneratesAQuantityThatChangesOnlyThroughCommands()
    {
        var run = Run("app.MapBalance<Inventory>();").AssertCompiles();

        // Anahtarı istemci verir; tek kovanın alanı Quantity'dir ve son değişim zamanı tutulur.
        var entity = run.Sources["Shop.Inventory.Entity.g.cs"];
        Assert.Contains("public string Key { get; set; } = \"\";", entity);
        Assert.Contains("public decimal Quantity { get; set; }", entity);
        Assert.Contains("global::Haldus.IAuditable", entity);

        // Bakiyenin oluşturma, güncelleme ve silme isteği yoktur.
        var dtos = run.Sources["Shop.Inventory.Dtos.g.cs"];
        Assert.Contains("public required decimal Quantity { get; init; }", dtos);
        Assert.DoesNotContain("Request", dtos);

        var endpoints = run.Sources["Shop.Inventory.Endpoints.g.cs"];
        Assert.Contains("""MapGet("/", ListAsync)""", endpoints);
        Assert.Contains("""MapGet("/{key}", GetAsync)""", endpoints);
        Assert.Contains("""MapPost("/{key}/adjust", AdjustAsync)""", endpoints);
        Assert.Contains("""MapPost("/{key}/set", SetAsync)""", endpoints);
        Assert.Contains("""MapGet("/{key}/entries", EntriesAsync)""", endpoints);
        Assert.DoesNotContain("MapPut", endpoints);
        Assert.DoesNotContain("MapDelete", endpoints);

        // Tek kovalı bakiyede taşıma yoktur.
        Assert.DoesNotContain("MoveAsync", endpoints);

        // Düzeltme bakiyeyi kilidin altında okur, sıfırın altına düşürmez ve değişimi aynı kaydetmede deftere ekler.
        var adjust = Handler(endpoints, "AdjustAsync");
        var locked = adjust.IndexOf("global::Haldus.HaldusBalances.BeginAsync(context, Ledger, key, cancellationToken)", StringComparison.Ordinal);
        var read = adjust.IndexOf("await FindOrStartAsync(context, key, cancellationToken)", StringComparison.Ordinal);
        var recorded = adjust.IndexOf("global::Haldus.HaldusBalances.Record(context, Ledger, false, key, bucket, request.Delta, balance, request.Reason);", StringComparison.Ordinal);
        var saved = adjust.IndexOf("await context.SaveChangesAsync(cancellationToken);", StringComparison.Ordinal);
        Assert.True(locked >= 0 && locked < read && read < recorded && recorded < saved);
        Assert.Contains("if (balance < 0 && request.Delta < 0)", adjust);
        Assert.Contains("BalanceInsufficient(messages, bucket, Read(entity, bucket))", adjust);
        Assert.DoesNotContain("IgnoreQueryFilters", endpoints);

        // Mutlak yazma, istemcinin gördüğü bakiye değiştiyse uygulanmaz.
        Assert.Contains("if (request.Expected is { } expected && expected != current)", Handler(endpoints, "SetAsync"));

        Assert.Contains("global::Haldus.HaldusBalances.Configure(modelBuilder);", run.Sources["HaldusModule.g.cs"]);
        Assert.DoesNotContain("HaldusBalances", Run("app.MapCrud<Product>();").AssertCompiles().Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Buckets_SplitTheBalanceAndAddMoving()
    {
        var run = Run("""
            app.MapBalance<Inventory>().MultiTenant().Buckets("available", "on-hold").Idempotent().Sortable(inventory => inventory.OnHold);
            """).AssertCompiles();

        var entity = run.Sources["Shop.Inventory.Entity.g.cs"];
        Assert.Contains("public decimal Available { get; set; }", entity);
        Assert.Contains("public decimal OnHold { get; set; }", entity);
        Assert.DoesNotContain("Quantity", entity);

        var endpoints = run.Sources["Shop.Inventory.Endpoints.g.cs"];
        Assert.Contains("private const string BucketNames = \"available, on-hold\";", endpoints);
        Assert.Contains("""MapPost("/{key}/move", MoveAsync)""", endpoints);

        // Verilmeyen kova ilk kovadır; tanınmayan kova reddedilir.
        Assert.Contains("null or \"\" => \"available\",", endpoints);
        Assert.Contains("\"on-hold\" => entity.OnHold,", endpoints);

        // Taşıma iki kovayı tek kaydetmede değiştirir ve iki defter kaydı yazar.
        var move = Handler(endpoints, "MoveAsync");
        Assert.Contains("if (source < 0 && true)", move);
        Assert.Contains("Record(context, Ledger, true, key, from, -request.Quantity, source, request.Reason);", move);
        Assert.Contains("Record(context, Ledger, true, key, to, request.Quantity, target, request.Reason);", move);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(move, "SaveChangesAsync"));

        // Kiracıya bağlı bakiyenin anahtarı kiracıyla birliktedir ve yeni kayıt kiracısıyla doğar.
        Assert.Contains("entity.HasKey(item => new { item.TenantId, item.Key });", run.Sources["HaldusModule.g.cs"]);
        Assert.Contains("TenantId = global::Haldus.HaldusScope.TenantId(context).GetValueOrDefault()", endpoints);

        // Komutlar tekrara karşı korunur.
        Assert.Contains("new global::Haldus.HaldusIdempotencyFilter<Inventory>(typeof(global::Haldus.BalanceAdjustment), required: false)", endpoints);
        Assert.Contains("new global::Haldus.HaldusIdempotencyFilter<Inventory>(typeof(global::Haldus.BalanceMove), required: false)", endpoints);
    }

    [Fact]
    public void Holds_ReserveAQuantityThroughTheRuntimeUnderTheBalanceLock()
    {
        var run = Run("app.MapBalance<Inventory>().MultiTenant().Buckets(\"available\", \"reserved\").Holds(to: \"reserved\", from: \"available\").Idempotent();").AssertCompiles();
        var endpoints = run.Sources["Shop.Inventory.Endpoints.g.cs"];

        Assert.Contains("""MapPost("/{key}/holds", HoldAsync)""", endpoints);
        Assert.Contains("""MapGet("/{key}/holds", HoldsAsync)""", endpoints);
        Assert.Contains("""MapPost("/{key}/holds/{holdId:guid}/capture", CaptureHoldAsync)""", endpoints);
        Assert.Contains("""MapPost("/{key}/holds/{holdId:guid}/release", ReleaseHoldAsync)""", endpoints);

        // Kurallar çalışma zamanındadır; üretilen kod kovaları (adlı argümanlar yer değiştirse de doğru sırayla) ve
        // bakiyeyi okuyup yazan parçalarını verir.
        var hold = Handler(endpoints, "HoldAsync");
        Assert.Contains("global::Haldus.HaldusBalanceHolds.PlaceAsync<Inventory>(", hold);
        Assert.Contains("\"available\",\n            \"reserved\",", hold);
        Assert.Contains("allowNegative: false,\n            FindOrStartAsync,\n            Read,\n            Write,", hold);
        Assert.Contains("BalanceInsufficient(messages, \"available\", result.Balance)", hold);

        var close = Handler(endpoints, "CloseHoldAsync");
        Assert.Contains("global::Haldus.HaldusBalanceHolds.CloseAsync<Inventory>(", close);
        Assert.Contains("NotFound(messages, \"Hold\", holdId)", close);
        Assert.Contains("BalanceHoldClosed(messages)", close);

        // Süresi dolan tutmaları bakım, her birini kendi kiracısı adına bırakır; kapatılamayanlar sırayı tutamaz.
        var expired = Handler(endpoints, "ReleaseExpiredHoldsAsync");
        Assert.Contains("global::Haldus.HaldusAmbient.Use(hold.TenantId, null)", expired);
        Assert.Contains("skip: failed,", expired);
        Assert.Contains("global::Haldus.HaldusBalanceHolds.Expired,", expired);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusBalanceHolds.Configure(modelBuilder);", module);
        Assert.Contains("global::Haldus.HaldusMaintenanceRegistry.RegisterBalanceHolds<global::Shop.Inventory>(global::Shop.InventoryEndpoints.ReleaseExpiredHoldsAsync);", module);

        // Tutma bildirilmediyse hiçbiri üretilmez.
        var plain = Run("app.MapBalance<Inventory>().Buckets(\"available\", \"reserved\");").AssertCompiles();
        Assert.DoesNotContain("Hold", plain.Sources["Shop.Inventory.Endpoints.g.cs"]);
        Assert.DoesNotContain("HaldusBalanceHolds", plain.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void AllowNegative_DropsTheFloor()
    {
        var endpoints = Run("app.MapBalance<Inventory>().AllowNegative();").AssertCompiles().Sources["Shop.Inventory.Endpoints.g.cs"];

        Assert.DoesNotContain("BalanceInsufficient", endpoints);
        Assert.DoesNotContain("if (request.Quantity < 0)", Handler(endpoints, "SetAsync"));
    }

    [Theory]
    [InlineData("app.MapBalance<Inventory>().SoftDeletable();", "HALDUS007", "SoftDeletable cannot be combined with MapBalance")]
    [InlineData("app.MapBalance<Inventory>().Workflow<System.DayOfWeek>();", "HALDUS007", "Workflow cannot be combined with MapBalance")]
    [InlineData("app.MapCrud<Product>(); app.MapBalance<Inventory>().References<Product>();", "HALDUS007", "References cannot be combined with MapBalance")]
    [InlineData("app.MapBalance<Inventory>().Buckets(\"Available\");", "HALDUS010", "A bucket is named with a constant")]
    [InlineData("app.MapBalance<Inventory>().Buckets(\"available\", \"available\");", "HALDUS010", "each name is used once")]
    [InlineData("var name = \"available\"; app.MapBalance<Inventory>().Buckets(name);", "HALDUS010", "A bucket is named with a constant")]
    [InlineData("app.MapBalance<Inventory>().Buckets(\"key\");", "HALDUS010", "would collide with the 'Key' field")]
    [InlineData("app.MapCrud<Product>().Buckets(\"available\");", "HALDUS007", "declare them on a MapBalance call")]
    [InlineData("app.MapCrud<Product>().AllowNegative();", "HALDUS007", "declare them on a MapBalance call")]
    [InlineData("app.MapCrud<Product>().Holds(\"available\", \"reserved\");", "HALDUS007", "declare them on a MapBalance call")]
    [InlineData("app.MapBalance<Inventory>().Buckets(\"available\", \"reserved\").Holds(\"available\", \"held\");", "HALDUS010", "Holds names the buckets 'available' and 'held'")]
    [InlineData("app.MapBalance<Inventory>().Buckets(\"available\", \"reserved\").Holds(\"available\", \"available\");", "HALDUS010", "Holds needs two different bucket names")]
    [InlineData("var bucket = \"reserved\"; app.MapBalance<Inventory>().Buckets(\"available\", \"reserved\").Holds(\"available\", bucket);", "HALDUS010", "Holds needs two different bucket names")]
    public void MapBalance_RejectsDeclarationsThatCannotWork(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void BalanceSnapshot()
    {
        var run = Run("app.MapBalance<Inventory>().Buckets(\"available\", \"reserved\");").AssertCompiles();

        Snapshot.Verify(run);
    }

    /// <summary>Bir handler metodunun gövdesi. Adın çağrıldığı yerler değil tanımlandığı yer aranır.</summary>
    private static string Handler(string endpoints, string name)
    {
        var from = endpoints.IndexOf($" {name}(\n", StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{name}' is not declared");

        var to = endpoints.IndexOf("\n    }\n", from, StringComparison.Ordinal);
        return endpoints.Substring(from, to - from);
    }
}
