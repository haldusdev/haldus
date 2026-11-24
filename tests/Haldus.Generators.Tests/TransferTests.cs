namespace Haldus.Generators.Tests;

/// <summary>İçe ve dışa aktarma işleri (<c>Exports</c>, <c>Imports</c>).</summary>
public sealed class TransferTests
{
    private const string Entities = """
        namespace Shop
        {
            public partial class Category
            {
                public string Name { get; set; } = "";
            }

            public partial class Product
            {
                public string Name { get; set; } = "";
                public decimal Price { get; set; }
            }

            public partial class ProductExport;

            public partial class ProductImport;

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
    public void Exports_TurnsAJobIntoAnExportWhoseLogicIsGenerated()
    {
        var run = Run("""
            app.MapCrud<Product>().MultiTenant();
            app.MapJob<ProductExport>().Exports<Product>().MultiTenant();
            """).AssertCompiles();

        var job = run.Sources["Shop.ProductExport.Entity.g.cs"];
        Assert.Contains("global::Haldus.IHaldusJob, global::Haldus.IHaldusExportJob", job);
        Assert.Contains("public string? Format { get; set; }", job);
        Assert.Contains("public string? StorageKey { get; set; }", job);

        // Biçim işin girdisidir; satır sayısı, boyut ve dosya adı çıktısıdır; deponun anahtarı gösterilmez.
        var dtos = run.Sources["Shop.ProductExport.Dtos.g.cs"];
        Assert.Contains("Format", dtos.Substring(dtos.IndexOf("record CreateProductExportRequest", StringComparison.Ordinal)));
        Assert.Contains("public required int Rows { get; init; }", dtos);
        Assert.DoesNotContain("StorageKey", dtos);

        // Mantık yazılmış bir handler gibi bulunur ve kaydedilir.
        var handler = run.Sources["Shop.ProductExport.Transfer.g.cs"];
        Assert.Contains("internal sealed class ProductExportTransferHandler : global::Haldus.IJobHandler<ProductExport>", handler);
        Assert.Contains("global::Shop.ProductEndpoints.ExportAsync(context.Services, cancellationToken)", handler);
        Assert.Contains("global::Shop.ProductExportTransferHandler", run.Sources["HaldusModule.g.cs"]);

        // Kayıtlar kaynağın kendi filtrelerinden geçerek, kaynağın yanıt biçiminde okunur.
        var source = Handler(run.Sources["Shop.Product.Endpoints.g.cs"], "ExportAsync");
        Assert.DoesNotContain("IgnoreQueryFilters", source);
        Assert.Contains("yield return ToResponse(item);", source);

        // Dosya işten indirilir ve içerik deposunda durur.
        var endpoints = run.Sources["Shop.ProductExport.Endpoints.g.cs"];
        Assert.Contains("""MapGet("/{id:guid}/content", DownloadAsync)""", endpoints);
        Assert.Contains("HaldusTransfers.SendAsync(httpContext, context, entity, messages, \"ProductExport\", id, cancellationToken)", endpoints);
        Assert.Contains("global::Haldus.HaldusFileStore.Configure(modelBuilder);", run.Sources["HaldusModule.g.cs"]);

        // Aktarılmayan bir kaynağa bu metotlar eklenmez.
        Assert.DoesNotContain("ExportAsync", Run("app.MapCrud<Product>();").AssertCompiles().Sources["Shop.Product.Endpoints.g.cs"]);
    }

    [Fact]
    public void Imports_CreatesEachRowThroughTheBatchItemOfTheResource()
    {
        var run = Run("""
            app.MapCrud<Category>();
            app.MapCrud<Product>().References<Category>(required: false).Batch().Expiring(TimeSpan.FromDays(30));
            app.MapJob<ProductImport>().Imports<Product>();
            """).AssertCompiles();

        var job = run.Sources["Shop.ProductImport.Entity.g.cs"];
        Assert.Contains("global::Haldus.IHaldusImportJob", job);
        Assert.Contains("public string Content { get; set; } = \"\";", job);

        // İçerik yalnızca istekte yer alır; yanıtta gösterilmez.
        var dtos = run.Sources["Shop.ProductImport.Dtos.g.cs"];
        var response = dtos.Substring(0, dtos.IndexOf("record CreateProductImportRequest", StringComparison.Ordinal));
        Assert.DoesNotContain("Content", response);
        Assert.Contains("public required int Imported { get; init; }", response);
        Assert.Contains("public string Content { get; init; }", dtos);
        Assert.Contains("MaxLengthAttribute(5000000)", dtos);
        Assert.DoesNotContain("Content = item.Content", run.Sources["Shop.ProductImport.Endpoints.g.cs"]);

        var handler = run.Sources["Shop.ProductImport.Transfer.g.cs"];
        Assert.Contains(
            "ImportAsync<ProductImport, global::Shop.Product, global::Shop.CreateProductRequest>(job, global::Shop.ProductEndpoints.ImportItemAsync, context, cancellationToken)",
            handler);

        // Her satır kendi sınırı içinde doğrulanır ve toplu oluşturmanın öğe yoluyla (yani tekil oluşturmanın
        // bütün kurallarıyla) oluşturulur.
        var item = Handler(run.Sources["Shop.Product.Endpoints.g.cs"], "ImportItemAsync");
        Assert.Contains("await using var item = await global::Haldus.HaldusBatch.BeginAsync(context, cancellationToken);", item);
        Assert.Contains("global::Haldus.HaldusBatch.Validate(request, messages)", item);
        Assert.Contains("?? await BatchCreateItemAsync(request, context, saved, ", item);
        Assert.Contains("GetRequiredService<global::System.TimeProvider>(services), contexts, messages, cancellationToken)", item);
    }

    [Theory]
    [InlineData("app.MapCrud<Product>(); app.MapCrud<ProductExport>().Exports<Product>();", "HALDUS007", "declare it on a MapJob call")]
    [InlineData("app.MapCrud<Product>().Batch(); app.MapJob<ProductExport>().Exports<Product>().Imports<Product>();", "HALDUS007", "can export or import one resource, not both")]
    [InlineData("app.MapJob<ProductExport>().Exports<Product>();", "HALDUS009", "Product")]
    [InlineData("app.MapStore<Product>(); app.MapJob<ProductExport>().Exports<Product>();", "HALDUS007", "Exports<Product>() needs a resource mapped with MapCrud, MapReadOnly or MapAppendOnly")]
    [InlineData("app.MapCrud<Product>(); app.MapJob<ProductImport>().Imports<Product>();", "HALDUS007", "to declare Batch() with the Create operation")]
    [InlineData("app.MapCrud<Category>(); app.MapCrud<Product>().Under<Category>().Batch(); app.MapJob<ProductImport>().Imports<Product>();", "HALDUS007", "to live at its own address")]
    public void Transfers_RejectDeclarationsThatCannotWork(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void TransfersSnapshot()
    {
        var run = Run("""
            app.MapCrud<Product>().Batch();
            app.MapJob<ProductExport>().Exports<Product>();
            app.MapJob<ProductImport>().Imports<Product>();
            """).AssertCompiles();

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
