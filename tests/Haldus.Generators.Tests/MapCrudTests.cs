namespace Haldus.Generators.Tests;

public sealed class MapCrudTests
{
    private const string MapProduct = """
        public static class Endpoints
        {
            public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Shop.Catalog.Product>();
        }
        """;

    [Fact]
    public void DeclaredPartialEntity_GeneratesIdDtosAndEndpoints()
    {
        var run = GeneratorHarness.Run($$"""
            namespace Shop.Catalog
            {
                public partial class Product
                {
                    [MaxLength(200)]
                    public string Name { get; set; } = "";
                    public decimal Price { get; set; }
                }
            }

            {{MapProduct}}
            """).AssertCompiles();

        Assert.Equal(
            [
                "HaldusModule.g.cs",
                "Shop.Catalog.Product.Dtos.g.cs",
                "Shop.Catalog.Product.Endpoints.g.cs",
                "Shop.Catalog.Product.Entity.g.cs",
            ],
            run.Sources.Keys.Order());

        var entity = run.Sources["Shop.Catalog.Product.Entity.g.cs"];
        Assert.Contains("partial class Product", entity);
        Assert.DoesNotContain("public partial class Product", entity);
        Assert.Contains("public global::System.Guid Id { get; set; }", entity);

        var endpoints = run.Sources["Shop.Catalog.Product.Endpoints.g.cs"];
        Assert.Contains("""MapGroup("/products")""", endpoints);
        Assert.Contains("""MapGet("/{id:guid}", GetAsync)""", endpoints);
        Assert.Contains("Id = global::System.Guid.CreateVersion7()", endpoints);
    }

    [Fact]
    public void UndeclaredEntity_IsGeneratedInTheRootNamespace()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop.Features
            {
                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Tag>();
                }
            }
            """).AssertCompiles();

        var entity = run.Sources["Shop.Tag.Entity.g.cs"];
        Assert.Contains("namespace Shop;", entity);
        Assert.Contains("public partial class Tag", entity);
    }

    [Fact]
    public void DeclaredIntId_IsUsedInsteadOfGeneratingOne()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop.Catalog
            {
                public partial class Product
                {
                    public int Id { get; set; }
                    public string Name { get; set; } = "";
                }
            }

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Shop.Catalog.Product>();
            }
            """).AssertCompiles();

        Assert.DoesNotContain("Shop.Catalog.Product.Entity.g.cs", run.Sources.Keys);

        var endpoints = run.Sources["Shop.Catalog.Product.Endpoints.g.cs"];
        Assert.Contains("""MapGet("/{id:int}", GetAsync)""", endpoints);
        Assert.DoesNotContain("CreateVersion7", endpoints);
        Assert.Contains("public required int Id { get; init; }", run.Sources["Shop.Catalog.Product.Dtos.g.cs"]);
    }

    [Fact]
    public void RequestDtos_CopyValidationRulesAndRequireNonNullableReferences()
    {
        var run = GeneratorHarness.Run($$"""
            namespace Shop.Catalog
            {
                public partial class Product
                {
                    [MaxLength(200)]
                    public string Name { get; set; } = "";

                    [Range(0.0, 10.5)]
                    public decimal Price { get; set; }

                    [Required, EmailAddress(ErrorMessage = "Bad email")]
                    public string Contact { get; set; } = "";

                    public string? Description { get; set; }
                }
            }

            {{MapProduct}}
            """).AssertCompiles();

        var dtos = run.Sources["Shop.Catalog.Product.Dtos.g.cs"];

        Assert.Contains(
            """
                [global::System.ComponentModel.DataAnnotations.RequiredAttribute(AllowEmptyStrings = true)]
                [global::System.ComponentModel.DataAnnotations.MaxLengthAttribute(200)]
                public string Name { get; init; } = default!;
            """.ReplaceLineEndings("\n"),
            dtos);

        // double argümanlar sonekiyle yazılmalı; aksi halde kopyalanan attribute int overload'ına bağlanır.
        Assert.Contains("RangeAttribute(0D, 10.5D)", dtos);

        // Kullanıcı Required yazdıysa ikincisi eklenmez.
        Assert.Contains(
            """
                [global::System.ComponentModel.DataAnnotations.RequiredAttribute]
                [global::System.ComponentModel.DataAnnotations.EmailAddressAttribute(ErrorMessage = "Bad email")]
                public string Contact { get; init; } = default!;
            """.ReplaceLineEndings("\n"),
            dtos);

        Assert.Contains("    public string? Description { get; init; }\n", dtos);
    }

    [Fact]
    public void NonScalarAndReadOnlyProperties_AreKeptOutOfRequestDtos()
    {
        var run = GeneratorHarness.Run($$"""
            namespace Shop.Catalog
            {
                public class Supplier;

                public partial class Product
                {
                    public string Name { get; set; } = "";
                    public decimal Total { get; private set; }
                    public Supplier? Supplier { get; set; }
                    public System.Collections.Generic.List<string> Tags { get; set; } = [];
                    public static int Counter { get; set; }
                }
            }

            {{MapProduct}}
            """).AssertCompiles();

        var dtos = run.Sources["Shop.Catalog.Product.Dtos.g.cs"];
        var response = dtos[..dtos.IndexOf("CreateProductRequest", StringComparison.Ordinal)];
        var requests = dtos[response.Length..];

        Assert.Contains("public required decimal Total { get; init; }", response);
        Assert.DoesNotContain("Total", requests);
        Assert.DoesNotContain("Supplier", dtos);
        Assert.DoesNotContain("Tags", dtos);
        Assert.DoesNotContain("Counter", dtos);
    }

    [Fact]
    public void InheritedProperties_AreIncluded()
    {
        var run = GeneratorHarness.Run($$"""
            namespace Shop.Catalog
            {
                public abstract class Named
                {
                    public string Name { get; set; } = "";
                }

                public partial class Product : Named
                {
                    public decimal Price { get; set; }
                }
            }

            {{MapProduct}}
            """).AssertCompiles();

        var dtos = run.Sources["Shop.Catalog.Product.Dtos.g.cs"];
        Assert.Contains("public required string Name { get; init; }", dtos);
        Assert.Contains("public required decimal Price { get; init; }", dtos);
    }

    [Fact]
    public void RouteAndNames_ArePluralizedAndKebabCased()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop
            {
                public partial class ProductCategory
                {
                    public string Name { get; set; } = "";
                }

                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app) => app.MapCrud<ProductCategory>();
                }
            }
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.ProductCategory.Endpoints.g.cs"];
        Assert.Contains("""MapGroup("/product-categories").WithTags("ProductCategories")""", endpoints);
        Assert.Contains("""WithName("ListProductCategories")""", endpoints);
        Assert.Contains("""WithName("GetProductCategory")""", endpoints);
    }

    [Fact]
    public void PartialDbContext_ReceivesDbSetsExceptThoseAlreadyDeclared()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop
            {
                public partial class Product
                {
                    public string Name { get; set; } = "";
                }

                public partial class Category
                {
                    public string Name { get; set; } = "";
                }

                public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
                {
                    public DbSet<Category> Categories => Set<Category>();
                }

                public static class Startup
                {
                    public static void Configure(IServiceCollection services) => services.AddHaldus<AppDbContext>();

                    public static void Map(IEndpointRouteBuilder app)
                    {
                        app.MapCrud<Product>();
                        app.MapCrud<Category>();
                    }
                }
            }
            """).AssertCompiles();

        var dbSets = run.Sources["AppDbContext.DbSets.g.cs"];
        Assert.Contains("DbSet<global::Shop.Product> Products => Set<global::Shop.Product>();", dbSets);
        Assert.DoesNotContain("Categories", dbSets);
    }

    [Fact]
    public void NonPartialDbContext_IsLeftAlone()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop
            {
                public partial class Product
                {
                    public string Name { get; set; } = "";
                }

                public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);

                public static class Startup
                {
                    public static void Configure(IServiceCollection services) => services.AddHaldus<AppDbContext>();

                    public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Product>();
                }
            }
            """).AssertCompiles();

        Assert.DoesNotContain("AppDbContext.DbSets.g.cs", run.Sources.Keys);
    }

    [Fact]
    public void ChainedAspNetCalls_DoNotBreakGeneration()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop
            {
                public partial class Product
                {
                    public string Name { get; set; } = "";
                }

                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app)
                    {
                        var products = app.MapCrud<Product>().RequireAuthorization();
                        products.MapGet("/featured", () => "featured");
                    }
                }
            }
            """).AssertCompiles();

        Assert.Contains("Shop.Product.Endpoints.g.cs", run.Sources.Keys);
    }

    [Fact]
    public void MapCrudFromAnotherLibrary_IsIgnored()
    {
        var run = GeneratorHarness.Run("""
            namespace Other
            {
                public static class OtherExtensions
                {
                    public static void MapCrud<T>(this string value) { }
                }

                public class Product;

                public static class Endpoints
                {
                    public static void Map() => "x".MapCrud<Product>();
                }
            }
            """).AssertCompiles();

        Assert.Empty(run.Sources);
    }

    [Fact]
    public void NonPartialEntity_ReportsHaldus001AndGeneratesNothing()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop
            {
                public class Product
                {
                    public string Name { get; set; } = "";
                }

                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Product>();
                }
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS001", diagnostic.Id);
        Assert.Equal(9, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Empty(run.Sources);
    }

    [Fact]
    public void EntityMappedTwice_ReportsHaldus002()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop
            {
                public partial class Product
                {
                    public string Name { get; set; } = "";
                }

                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app)
                    {
                        app.MapCrud<Product>();
                        app.MapCrud<Product>();
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS002", diagnostic.Id);
        Assert.True(run.CompileErrors.IsEmpty, string.Join(Environment.NewLine, run.CompileErrors));
    }

    [Fact]
    public void UnsupportedIdType_ReportsHaldus003()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop
            {
                public partial class Product
                {
                    public string Id { get; set; } = "";
                }

                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Product>();
                }
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS003", diagnostic.Id);
        Assert.Contains("string", diagnostic.GetMessage());
        Assert.Empty(run.Sources);
    }
}
