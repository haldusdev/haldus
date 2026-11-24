namespace Haldus.Generators.Tests;

public sealed class AssociationTests
{
    private const string Entities = """
        namespace Shop
        {
            public partial class Product
            {
                public string Name { get; set; } = "";
            }

            public partial class Tag
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            public partial class Settings
            {
                public string Theme { get; set; } = "";
            }

            public class Plain
            {
                public int Id { get; set; }
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
    public void Association_GeneratesALinkEntityKeyedByBothEnds()
    {
        var run = Run("""
            app.MapCrud<Product>();
            app.MapCrud<Tag>();
            app.MapAssociation<Product, Tag>();
            """).AssertCompiles();

        var entity = run.Sources["Shop.ProductTag.Entity.g.cs"];
        Assert.Contains("public partial class ProductTag", entity);
        Assert.Contains("public global::System.Guid ProductId { get; set; }", entity);
        Assert.Contains("public int TagId { get; set; }", entity);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.HasKey(item => new { item.ProductId, item.TagId });", module);
        Assert.Contains(
            "global::Haldus.HaldusEndpointRegistry.RegisterAssociation<global::Shop.Product, global::Shop.Tag>(global::Shop.ProductTagEndpoints.Map);",
            module);

        Assert.Contains("DbSet<global::Shop.ProductTag> ProductTags", run.Sources["AppDbContext.DbSets.g.cs"]);
    }

    [Fact]
    public void Association_MapsBothDirectionsWithTypedKeys()
    {
        var run = Run("""
            app.MapCrud<Product>();
            app.MapCrud<Tag>();
            app.MapAssociation<Product, Tag>();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.ProductTag.Endpoints.g.cs"];
        Assert.Contains("""MapGet("/products/{productId:guid}/tags", ListTagsAsync)""", endpoints);
        Assert.Contains("""MapPut("/products/{productId:guid}/tags/{tagId:int}", LinkAsync)""", endpoints);
        Assert.Contains("""MapDelete("/products/{productId:guid}/tags/{tagId:int}", UnlinkAsync)""", endpoints);
        Assert.Contains("""MapGet("/products/{productId:guid}/tags/{tagId:int}", ExistsAsync)""", endpoints);
        Assert.Contains("""MapGet("/tags/{tagId:int}/products", ListProductsAsync)""", endpoints);

        // Listeler karşı ucun yanıt tipini döndürür.
        Assert.Contains("global::Haldus.PagedResult<global::Shop.TagResponse>", endpoints);
        Assert.Contains("global::Haldus.PagedResult<global::Shop.ProductResponse>", endpoints);
    }

    [Fact]
    public void Association_IsHiddenUnlessBothEndsAreVisible()
    {
        var run = Run("""
            app.MapCrud<Product>().MultiTenant();
            app.MapCrud<Tag>();
            app.MapAssociation<Product, Tag>();
            """).AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.HasQueryFilter(global::Haldus.HaldusFilters.Association, item =>", module);
        Assert.Contains("context.Set<global::Shop.Product>().Any(left => left.Id == item.ProductId)", module);
        Assert.Contains("context.Set<global::Shop.Tag>().Any(right => right.Id == item.TagId)", module);

        // Bağ kurulurken de iki uç kendi filtrelerinden geçerek aranır.
        var endpoints = run.Sources["Shop.ProductTag.Endpoints.g.cs"];
        Assert.Contains("NotFound(messages, \"Product\", productId)", endpoints);
        Assert.Contains("NotFound(messages, \"Tag\", tagId)", endpoints);
    }

    [Fact]
    public void Association_WithAStringKeyedEnd_UsesItsCode()
    {
        var run = Run("""
            app.MapCrud<Product>();
            app.MapLookup<Country>();
            app.MapAssociation<Product, Country>();
            """).AssertCompiles();

        Assert.Contains("public string CountryCode { get; set; } = \"\";", run.Sources["Shop.ProductCountry.Entity.g.cs"]);
        Assert.Contains(
            """MapPut("/products/{productId:guid}/countries/{countryCode}", LinkAsync)""",
            run.Sources["Shop.ProductCountry.Endpoints.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Product>(); app.MapAssociation<Product, Plain>();", "HALDUS009", "Plain")]
    [InlineData("app.MapCrud<Tag>(); app.MapAssociation<Missing, Tag>();", "HALDUS009", "Missing")]
    [InlineData("app.MapCrud<Product>(); app.MapAssociation<Product, Product>();", "HALDUS007", "cannot be associated with itself")]
    [InlineData("app.MapCrud<Product>(); app.MapSingleton<Settings>(); app.MapAssociation<Product, Settings>();", "HALDUS007", "'Settings' cannot take part in an association")]
    [InlineData("app.MapCrud<Product>(); app.MapStore<Settings>().MultiTenant(); app.MapAssociation<Settings, Product>();", "HALDUS007", "'Settings' cannot take part in an association")]
    public void InvalidAssociations_AreReported(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
        Assert.DoesNotContain(run.Sources.Keys, key => key.Contains("Endpoints", StringComparison.Ordinal) && key.Contains("ProductTag", StringComparison.Ordinal));
    }

    [Fact]
    public void Association_DeclaredTwice_IsGeneratedOnce()
    {
        var run = Run("""
            app.MapCrud<Product>();
            app.MapCrud<Tag>();
            app.MapAssociation<Product, Tag>();
            app.MapAssociation<Product, Tag>();
            """).AssertCompiles();

        Assert.Single(run.Sources.Keys, key => key == "Shop.ProductTag.Endpoints.g.cs");
    }

    [Fact]
    public void AssociationSnapshot()
    {
        var run = Run("""
            app.MapCrud<Product>().MultiTenant();
            app.MapCrud<Tag>();
            app.MapAssociation<Product, Tag>();
            """).AssertCompiles();

        Snapshot.Verify(run);
    }
}
