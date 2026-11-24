namespace Haldus.Generators.Tests;

/// <summary>Kiracının tanımladığı özel alanlar (<c>CustomFields</c>).</summary>
public sealed class CustomFieldTests
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
    public void CustomFields_AddABagThatIsValidatedOnEveryWrite()
    {
        var run = Run("app.MapCrud<Product>().CustomFields();").AssertCompiles();

        var entity = run.Sources["Shop.Product.Entity.g.cs"];
        Assert.Contains("global::Haldus.IHasCustomFields", entity);
        Assert.Contains("public global::System.Collections.Generic.Dictionary<string, global::System.Text.Json.JsonElement> CustomFields { get; set; } = new();", entity);

        // Çanta yanıtta ve her yazma isteğinde yer alır.
        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        foreach (var record in new[] { "record ProductResponse", "record CreateProductRequest", "record UpdateProductRequest", "record PatchProductRequest" })
        {
            Assert.Contains("CustomFields", dtos.Substring(dtos.IndexOf(record, StringComparison.Ordinal)).Split("\n}\n")[0]);
        }

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];

        // Oluşturmada değerler kayıt kurulmadan önce, güncellemede atanırken doğrulanır; PATCH birleştirir.
        var create = Handler(endpoints, "CreateAsync");
        Assert.Contains("contexts.For<Product>(), null, request.CustomFields, merge: false, messages, cancellationToken);", create);
        Assert.Contains("return invalidFields;", create);
        Assert.Contains("CustomFields = customFields.Values", create);

        Assert.Contains("context, entity.CustomFields, request.CustomFields, merge: false, messages, cancellationToken);", Handler(endpoints, "UpdateAsync"));
        Assert.Contains("context, entity.CustomFields, request.CustomFields, merge: true, messages, cancellationToken);", Handler(endpoints, "PatchAsync"));
        Assert.Contains("IsSet(\"CustomFields\")", Handler(endpoints, "PatchAsync"));

        // Tanımlar kaynağın adresinde yönetilir.
        Assert.Contains("""MapGet("/custom-fields", ListCustomFieldsAsync)""", endpoints);
        Assert.Contains("""MapPut("/custom-fields/{key}", DefineCustomFieldAsync)""", endpoints);
        Assert.Contains("""MapDelete("/custom-fields/{key}", RemoveCustomFieldAsync)""", endpoints);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusCustomFieldStore.Configure(modelBuilder);", module);
        Assert.Contains("global::Haldus.HaldusCustomFieldStore.ConfigureValues(entity.Property(item => item.CustomFields));", module);
        Assert.DoesNotContain("HaldusCustomFieldStore", Run("app.MapCrud<Product>();").AssertCompiles().Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void CustomFields_TravelThroughEveryPathThatWritesTheRecord()
    {
        // Toplu yollar, önizleme, kopya, geri yükleme, yayın ve onay: hepsi derlenmeli ve değerleri taşımalıdır.
        var run = Run("""
            app.MapCrud<Product>().CustomFields().Metadata().Batch().Previewable().Cloneable().Revisioned().Publishable();
            app.MapCrud<Category>().CustomFields().Approvable();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];

        Assert.Contains("request.CustomFields, merge: false", Handler(endpoints, "BatchCreateItemAsync"));
        Assert.Contains("entity.CustomFields, request.CustomFields, merge: false", Handler(endpoints, "BatchUpdateItemAsync"));
        Assert.Contains("request.CustomFields, merge: false", Handler(endpoints, "PreviewAsync"));
        Assert.Contains("entity.CustomFields = new(revision.CustomFields);", Handler(endpoints, "RollbackAsync"));

        // Başka bir kayıttan doğan kayıt değerleri olduğu gibi taşır; kopyanın isteği onları değiştirebilir.
        Assert.Contains("CustomFields = new(source.CustomFields)", Handler(endpoints, "CloneAsync"));
        Assert.Contains("entity.CustomFields, request.CustomFields, merge: true", Handler(endpoints, "CloneAsync"));
        Assert.Contains("CustomFields = new(source.CustomFields)", Handler(endpoints, "PublishAsync"));

        // Özel alanlar içeriktir: değişince toplanan onaylar düşer.
        Assert.Contains("HaldusApprovals.Register<global::Shop.Category>(\"Name\", \"CustomFields\");", run.Sources["HaldusModule.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Category>(); app.MapCrud<Product>().Under<Category>().CustomFields();", "CustomFields cannot be combined with Under<T>()")]
    [InlineData("app.MapCrud<Category>(); app.MapCrud<Product>().AttachedTo<Category>().CustomFields();", "it has CustomFields")]
    [InlineData("app.MapAppendOnly<Product>().CustomFields();", "CustomFields cannot be combined with MapAppendOnly")]
    public void CustomFields_RejectsDeclarationsThatCannotWork(string body, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == "HALDUS007" && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
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