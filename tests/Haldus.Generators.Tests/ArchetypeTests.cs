namespace Haldus.Generators.Tests;

public sealed class ArchetypeTests
{
    private const string Product = """
        namespace Shop
        {
            public partial class Product
            {
                public string Name { get; set; } = "";
            }

            public class ProductFilter : IFilter<Product>
            {
                public System.Linq.IQueryable<Product> Apply(System.Linq.IQueryable<Product> query) => query;
            }
        }
        """;

    private static GeneratorRun Run(string mapCall, string entity = Product) =>
        GeneratorHarness.Run($$"""
            {{entity}}

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) => app.{{mapCall}};
            }
            """);

    [Fact]
    public void ReadOnly_HasOnlyListAndGet()
    {
        var run = Run("MapReadOnly<Shop.Product>()").AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("ListAsync", endpoints);
        Assert.Contains("GetAsync", endpoints);
        Assert.DoesNotContain("MapPost", endpoints);
        Assert.DoesNotContain("MapPut", endpoints);
        Assert.DoesNotContain("MapPatch", endpoints);
        Assert.DoesNotContain("MapDelete", endpoints);
        Assert.DoesNotContain("Request", run.Sources["Shop.Product.Dtos.g.cs"]);
    }

    [Fact]
    public void ReadOnly_WithSoftDeleteAndArchive_FiltersButAddsNoWriteEndpoints()
    {
        var run = Run("MapReadOnly<Shop.Product>().SoftDeletable().Archivable()").AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("bool? includeDeleted,", endpoints);
        Assert.Contains("bool? includeArchived,", endpoints);
        Assert.DoesNotContain("MapPost", endpoints);
    }

    [Fact]
    public void AppendOnly_HasListGetAndCreate()
    {
        var run = Run("MapAppendOnly<Shop.Product>()").AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("""MapPost("/", CreateAsync)""", endpoints);
        Assert.DoesNotContain("MapPut", endpoints);
        Assert.DoesNotContain("MapPatch", endpoints);
        Assert.DoesNotContain("MapDelete", endpoints);

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        Assert.Contains("CreateProductRequest", dtos);
        Assert.DoesNotContain("UpdateProductRequest", dtos);
        Assert.DoesNotContain("PatchProductRequest", dtos);
    }

    [Fact]
    public void Store_UsesAClientSuppliedStringKeyAndUpsert()
    {
        var run = Run("MapStore<Shop.Product>()").AssertCompiles();

        var entity = run.Sources["Shop.Product.Entity.g.cs"];
        Assert.Contains("public string Key { get; set; } = \"\";", entity);
        Assert.DoesNotContain(" Id ", entity);

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("""MapPut("/{key}", PutAsync)""", endpoints);
        Assert.Contains("""MapGet("/{key}", GetAsync)""", endpoints);
        Assert.Contains("entity = new Product { Key = key, Name = request.Name };", endpoints);
        Assert.DoesNotContain("MapPost", endpoints);
        Assert.DoesNotContain("MapPatch", endpoints);

        Assert.Contains("PutProductRequest", run.Sources["Shop.Product.Dtos.g.cs"]);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.HasKey(item => item.Key);", module);
        Assert.Contains("entity.Property(item => item.Key).ValueGeneratedNever();", module);
    }

    [Fact]
    public void Store_WithDeclaredGuidKey_UsesItsType()
    {
        var run = Run(
            "MapStore<Shop.Product>()",
            """
            namespace Shop
            {
                public partial class Product
                {
                    public Guid Key { get; set; }
                    public string Name { get; set; } = "";
                }
            }
            """).AssertCompiles();

        Assert.DoesNotContain("Shop.Product.Entity.g.cs", run.Sources.Keys);
        Assert.Contains("""MapPut("/{key:guid}", PutAsync)""", run.Sources["Shop.Product.Endpoints.g.cs"]);
    }

    [Fact]
    public void Store_WithMultiTenant_MakesTheTenantPartOfTheKey()
    {
        var run = Run("MapStore<Shop.Product>().MultiTenant()").AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.HasKey(item => new { item.TenantId, item.Key });", module);
        Assert.DoesNotContain("HasIndex", module);

        Assert.Contains(
            "entity = new Product { Key = key, TenantId = tenants.TenantId.GetValueOrDefault(), Name = request.Name };",
            run.Sources["Shop.Product.Endpoints.g.cs"]);
    }

    [Fact]
    public void Lookup_GeneratesCodeAndNameAndReturnsAnUnpagedList()
    {
        var run = Run("MapLookup<Country>()", entity: string.Empty).AssertCompiles();

        var entity = run.Sources["Country.Entity.g.cs"];
        Assert.Contains("public string Code { get; set; } = \"\";", entity);
        Assert.Contains("public string Name { get; set; } = \"\";", entity);

        var endpoints = run.Sources["Country.Endpoints.g.cs"];
        Assert.Contains("global::System.Collections.Generic.List<CountryResponse>", endpoints);
        Assert.DoesNotContain("PageRequest", endpoints);
        Assert.Contains("""MapPut("/{code}", PutAsync)""", endpoints);
        Assert.Contains(".OrderBy(item => item.Code)", endpoints);

        var dtos = run.Sources["Country.Dtos.g.cs"];
        Assert.Contains("public required string Code { get; init; }", dtos);
        Assert.Contains("public required string Name { get; init; }", dtos);
    }

    [Fact]
    public void Lookup_WithDeclaredName_DoesNotGenerateItAgain()
    {
        var run = Run(
            "MapLookup<Shop.Currency>()",
            """
            namespace Shop
            {
                public partial class Currency
                {
                    [MaxLength(50)]
                    public string Name { get; set; } = "";
                    public int Decimals { get; set; }
                }
            }
            """).AssertCompiles();

        var entity = run.Sources["Shop.Currency.Entity.g.cs"];
        Assert.Contains("public string Code", entity);
        Assert.DoesNotContain("Name", entity);
        Assert.Contains("MaxLengthAttribute(50)", run.Sources["Shop.Currency.Dtos.g.cs"]);
    }

    [Fact]
    public void Singleton_HasNoVisibleKeyAndASingularRoute()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop
            {
                public partial class StoreSettings
                {
                    public string StoreName { get; set; } = "";
                }

                public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);

                public static class Startup
                {
                    public static void Configure(IServiceCollection services) => services.AddHaldus<AppDbContext>();

                    public static void Map(IEndpointRouteBuilder app) => app.MapSingleton<StoreSettings>();
                }
            }
            """).AssertCompiles();

        Assert.DoesNotContain("Shop.StoreSettings.Entity.g.cs", run.Sources.Keys);

        var endpoints = run.Sources["Shop.StoreSettings.Endpoints.g.cs"];
        Assert.Contains("""MapGroup("/store-settings").WithTags("StoreSettings")""", endpoints);
        Assert.Contains("""MapGet("/", GetAsync)""", endpoints);
        Assert.Contains("""MapPut("/", UpdateAsync)""", endpoints);
        Assert.Contains("""MapPatch("/", PatchAsync)""", endpoints);
        Assert.DoesNotContain("MapPost", endpoints);
        Assert.DoesNotContain("MapDelete", endpoints);
        Assert.Contains("ToResponse(entity ?? new StoreSettings())", endpoints);

        Assert.DoesNotContain(" Id ", run.Sources["Shop.StoreSettings.Dtos.g.cs"]);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.Property<int>(global::Haldus.HaldusModel.SingletonKey).ValueGeneratedNever();", module);
        Assert.Contains("entity.HasKey(global::Haldus.HaldusModel.SingletonKey);", module);

        // Tek kayıtlı kaynağın DbSet adı çoğullanmaz.
        Assert.Contains("> StoreSettings => Set<", run.Sources["AppDbContext.DbSets.g.cs"]);
    }

    [Fact]
    public void Singleton_WithMultiTenant_IsKeyedByTheTenant()
    {
        var run = Run("MapSingleton<Shop.Product>().MultiTenant()").AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.HasKey(item => item.TenantId);", module);
        Assert.DoesNotContain("SingletonKey", module);
        Assert.Contains("entity.TenantId = tenants.TenantId.GetValueOrDefault();", run.Sources["Shop.Product.Endpoints.g.cs"]);
    }

    [Theory]
    [InlineData("MapReadOnly<Shop.Product>().ConcurrencyChecked()", "ConcurrencyChecked cannot be combined with MapReadOnly")]
    [InlineData("MapAppendOnly<Shop.Product>().SoftDeletable()", "SoftDeletable cannot be combined with MapAppendOnly")]
    [InlineData("MapAppendOnly<Shop.Product>().ConcurrencyChecked()", "ConcurrencyChecked cannot be combined with MapAppendOnly")]
    [InlineData("MapStore<Shop.Product>().ConcurrencyChecked()", "ConcurrencyChecked cannot be combined with MapStore")]
    [InlineData("MapStore<Shop.Product>().SoftDeletable()", "SoftDeletable cannot be combined with MapStore")]
    [InlineData("MapLookup<Shop.Product>().SoftDeletable()", "SoftDeletable cannot be combined with MapLookup")]
    [InlineData("MapSingleton<Shop.Product>().Archivable()", "Archivable cannot be combined with MapSingleton")]
    [InlineData("MapSingleton<Shop.Product>().Searchable(p => p.Name)", "Searchable cannot be combined with MapSingleton")]
    [InlineData("MapLookup<Shop.Product>().Filterable<Shop.ProductFilter>()", "Filterable cannot be combined with MapLookup")]
    [InlineData("MapCrud<Shop.Product>().Without(CrudOperation.List).Searchable(p => p.Name)", "Searchable requires the List operation")]
    [InlineData("MapCrud<Shop.Product>().Only(CrudOperation.Get).Searchable(p => p.Name)", "Searchable requires the List operation")]
    [InlineData("MapCrud<Shop.Product>().Only(CrudOperation.Get).Filterable<Shop.ProductFilter>()", "Filterable requires the List operation")]
    [InlineData("MapReadOnly<Shop.Product>().Without(CrudOperation.Delete)", "MapReadOnly does not have the operation(s) 'Delete'")]
    [InlineData("MapAppendOnly<Shop.Product>().Only(CrudOperation.List, CrudOperation.Update)", "MapAppendOnly does not have the operation(s) 'Update'")]
    [InlineData("MapSingleton<Shop.Product>().Without(CrudOperation.List)", "MapSingleton does not have the operation(s) 'List'")]
    public void ConflictingDeclarations_ReportHaldus007AndGenerateNothing(string mapCall, string expectedMessage)
    {
        var run = Run(mapCall);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS007", diagnostic.Id);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
        Assert.Empty(run.Sources);
    }

    [Fact]
    public void SearchableWithListRemoved_NeverEmitsASearchEndpoint()
    {
        // Liste işlemini kaldıran geliştirici, kayıtların toplu dökülmesini kapatmış olur; arama ucu bunu
        // arka kapıdan yeniden açmamalıdır.
        var run = Run("MapCrud<Shop.Product>().Searchable(p => p.Name).Without(CrudOperation.List)");

        Assert.Equal("HALDUS007", Assert.Single(run.GeneratorDiagnostics).Id);
        Assert.DoesNotContain(run.Sources.Values, source => source.Contains("SearchAsync", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MapLookup<Shop.Product>()", "public int Code { get; set; }", "string")]
    [InlineData("MapStore<Shop.Product>()", "public decimal Key { get; set; }", "string, Guid, int and long")]
    [InlineData("MapReadOnly<Shop.Product>()", "public string Id { get; set; } = \"\";", "Guid, int and long")]
    public void UnsupportedKeyType_ReportsHaldus003WithTheTypesThatAreSupported(string mapCall, string key, string supported)
    {
        var run = Run(
            mapCall,
            $$"""
            namespace Shop
            {
                public partial class Product
                {
                    {{key}}
                    public string Name { get; set; } = "";
                }
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS003", diagnostic.Id);
        Assert.Contains($"are {supported}", diagnostic.GetMessage());
    }

    [Fact]
    public void ArchetypesSnapshot()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop
            {
                public partial class StockMovement
                {
                    public int Quantity { get; set; }
                }

                public partial class StockLevel
                {
                    public int Quantity { get; set; }
                }

                public partial class Preference
                {
                    public string Value { get; set; } = "";
                }

                public partial class StoreSettings
                {
                    public string StoreName { get; set; } = "";
                    public decimal TaxRate { get; set; } = 20m;
                }

                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app)
                    {
                        app.MapAppendOnly<StockMovement>().Auditable();
                        app.MapReadOnly<StockLevel>();
                        app.MapStore<Preference>().MultiTenant();
                        app.MapLookup<Country>();
                        app.MapSingleton<StoreSettings>();
                    }
                }
            }
            """).AssertCompiles();

        Snapshot.Verify(run);
    }
}
