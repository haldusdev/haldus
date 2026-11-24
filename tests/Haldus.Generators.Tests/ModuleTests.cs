namespace Haldus.Generators.Tests;

/// <summary>Bir modülün, başka bir assembly'de derlenmiş bir modülün entity'lerine bağlanması.</summary>
public sealed class ModuleTests
{
    /// <summary>İlk modül: kendi başına derlenir ve ikincisi ona derlenmiş assembly olarak başvurur.</summary>
    private static GeneratorRun Catalog(string body = """
        app.MapCrud<Category>();
        app.MapCrud<Product>().MultiTenant();
        app.MapLookup<Country>();
        app.MapSingleton<CatalogSettings>();
        app.MapStore<Preference>().MultiTenant();
        """) =>
        GeneratorHarness.Run($$"""
            namespace Catalog
            {
                public partial class Category
                {
                    public int Id { get; set; }

                    public string Name { get; set; } = "";
                }

                public partial class Product
                {
                    public string Name { get; set; } = "";
                }

                public partial class CatalogSettings
                {
                    public string Theme { get; set; } = "";
                }

                public partial class Preference
                {
                    public string Value { get; set; } = "";
                }

                public static class CatalogEndpoints
                {
                    public static void MapCatalog(this IEndpointRouteBuilder app)
                    {
                        {{body}}
                    }
                }
            }
            """, "Catalog").AssertCompiles();

    private static GeneratorRun Sales(string body) =>
        GeneratorHarness.Run($$"""
            namespace Sales
            {
                public partial class Order
                {
                    public string CustomerName { get; set; } = "";
                }

                public partial class OrderNote
                {
                    public string Text { get; set; } = "";
                }

                public partial class SalesDbContext(DbContextOptions<SalesDbContext> options) : DbContext(options);

                public static class SalesModule
                {
                    public static void AddSales(this IServiceCollection services) => services.AddHaldusModule<SalesDbContext>();

                    public static void MapSales(this IEndpointRouteBuilder app)
                    {
                        {{body}}
                    }
                }
            }
            """, "Sales", Catalog());

    [Fact]
    public void References_ToAnEntityOfAnotherModule_ReadsItsKeyFromTheCompiledType()
    {
        var run = Sales("app.MapCrud<Order>().References<Catalog.Category>();").AssertCompiles();

        Assert.Contains("public int CategoryId { get; set; }", run.Sources["Sales.Order.Entity.g.cs"]);

        // Varlık, hedefin kendi bağlamında doğrulanır; iki modül ayrı bağlamlarda da olsa çalışır.
        Assert.Contains(
            "if (!await contexts.For<global::Catalog.Category>().Set<global::Catalog.Category>()\n"
            + "            .AnyAsync(target => target.Id == request.CategoryId, cancellationToken))",
            run.Sources["Sales.Order.Endpoints.g.cs"]);
    }

    [Fact]
    public void References_AcrossModules_BecomeAConstraintOnlyWhenBothShareAContext()
    {
        var run = Sales("app.MapCrud<Order>().References<Catalog.Category>();").AssertCompiles();

        // Ayrı bağlamlarda hedefin tablosu bu modele alınmaz; referans yalnızca kimlikle tutulur.
        Assert.Contains(
            "if (global::Haldus.HaldusModel.Shares<global::Catalog.Category>(context)) { "
            + "entity.HasOne<global::Catalog.Category>().WithMany().HasForeignKey(item => item.CategoryId)"
            + ".OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict); } "
            + "else { entity.HasIndex(item => item.CategoryId); }",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void References_ToALookupOfAnotherModule_UseItsCode()
    {
        var run = Sales("app.MapCrud<Order>().References<Catalog.Country>(required: false);").AssertCompiles();

        Assert.Contains("public string? CountryCode { get; set; }", run.Sources["Sales.Order.Entity.g.cs"]);
        Assert.Contains("AnyAsync(target => target.Code == countryCodeReference, cancellationToken)", run.Sources["Sales.Order.Endpoints.g.cs"]);
    }

    [Fact]
    public void Under_AParentOfAnotherModule_NestsTheRoutesAndRequiresASharedContext()
    {
        var run = Sales("app.MapCrud<OrderNote>().Under<Catalog.Product>();").AssertCompiles();

        var endpoints = run.Sources["Sales.OrderNote.Endpoints.g.cs"];
        Assert.Contains("""MapGroup("/products/{productId:guid}/order-notes")""", endpoints);
        Assert.Contains("NotFound(messages, \"Product\", productId)", endpoints);

        // Görünürlük ve zincirleme silme ebeveynin tablosuna dayanır; ayrı bağlamlarda hata model kurulurken verilir.
        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusModel.RequireShared<global::Catalog.Product>(context, \"OrderNote\");", module);
        Assert.Contains(
            "entity.HasQueryFilter(global::Haldus.HaldusFilters.Parent, item => context.Set<global::Catalog.Product>()"
            + ".Any(parent => parent.Id == item.ProductId));",
            module);
    }

    [Fact]
    public void Under_TenancyOfAParentInAnotherModuleIsReadFromItsType()
    {
        const string Cascade = "HasForeignKey(item => item.ProductId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)";
        const string Restrict = "HasForeignKey(item => item.CategoryId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict)";

        // Ürün kiracıya bağlıdır: altındaki kiracıya ait notlar onunla birlikte silinebilir.
        var belowTenant = Sales("app.MapCrud<OrderNote>().MultiTenant().Under<Catalog.Product>();").AssertCompiles();

        // Kategori geneldir: onu silen kişi başka kiracıların notlarını silmemelidir.
        var belowGlobal = Sales("app.MapCrud<OrderNote>().MultiTenant().Under<Catalog.Category>();").AssertCompiles();

        Assert.Contains(Cascade, belowTenant.Sources["HaldusModule.g.cs"]);
        Assert.Contains(Restrict, belowGlobal.Sources["HaldusModule.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Order>().References<Catalog.CatalogSettings>();", "'CatalogSettings' cannot be referenced")]
    [InlineData("app.MapCrud<Order>().References<Catalog.Preference>();", "'Preference' cannot be referenced")]
    public void ResourcesWithoutASingleColumnKey_CannotBeReferencedAcrossModulesEither(string body, string expectedMessage)
    {
        var run = Sales(body);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == "HALDUS007");
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void Association_EndsMustBeMappedInTheSameModule()
    {
        var run = Sales("app.MapCrud<Order>(); app.MapAssociation<Order, Catalog.Product>();");

        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "HALDUS009");
    }

    [Fact]
    public void AddHaldusModule_AddsTheModulesOwnDbSetsToItsContext()
    {
        var run = Sales("app.MapCrud<Order>().References<Catalog.Category>();").AssertCompiles();

        var sets = run.Sources["SalesDbContext.DbSets.g.cs"];
        Assert.Contains("public global::Microsoft.EntityFrameworkCore.DbSet<global::Sales.Order> Orders => Set<global::Sales.Order>();", sets);

        // Başka modülün entity'si bu bağlama eklenmez.
        Assert.DoesNotContain("Category", sets);
    }

    [Fact]
    public void EachModule_RegistersOnlyItsOwnResources()
    {
        var run = Sales("app.MapCrud<Order>().References<Catalog.Category>();").AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusEndpointRegistry.Register<global::Sales.Order>(global::Sales.OrderEndpoints.Map);", module);
        Assert.DoesNotContain("Register<global::Catalog.", module);
        Assert.DoesNotContain("modelBuilder.Entity<global::Catalog.", module);
    }

    [Fact]
    public void ModuleSnapshot()
    {
        var run = Sales("""
            app.MapCrud<Order>().References<Catalog.Category>().References<Catalog.Country>(required: false);
            app.MapCrud<OrderNote>().Under<Catalog.Product>();
            """).AssertCompiles();

        Snapshot.Verify(run);
    }
}