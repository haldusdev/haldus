namespace Haldus.Generators.Tests;

public sealed class FilterAndSearchTests
{
    private const string Product = """
        namespace Shop
        {
            public enum Kind { Physical, Digital }

            public partial class Product
            {
                public string Name { get; set; } = "";
                public string? Description { get; set; }
                public decimal Price { get; set; }
                public int? Stock { get; set; }
                public bool Active { get; set; }
                public Kind Kind { get; set; }
                public Guid? SupplierId { get; set; }
                public System.Collections.Generic.List<string> Tags { get; set; } = [];
            }
        }
        """;

    private static GeneratorRun RunWithChain(string chain, string extraSource = "") =>
        GeneratorHarness.Run($$"""
            {{Product}}

            {{extraSource}}

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Shop.Product>(){{chain}};
            }
            """);

    [Fact]
    public void Filterable_BindsEachScalarPropertyAsAnOptionalCamelCaseQueryParameter()
    {
        var run = RunWithChain(
            ".Filterable<Shop.ProductFilter>()",
            """
            namespace Shop
            {
                public class ProductFilter : IFilter<Product>
                {
                    public string? Search { get; set; }
                    public decimal MinPrice { get; set; }
                    public int[] Stocks { get; set; } = [];
                    public Product? NotBindable { get; set; }
                    public string ReadOnly => "";

                    public System.Linq.IQueryable<Product> Apply(System.Linq.IQueryable<Product> query) => query;
                }
            }
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];

        Assert.Contains("""[global::Microsoft.AspNetCore.Mvc.FromQuery(Name = "search")] string? filterSearch,""", endpoints);
        Assert.Contains("""[global::Microsoft.AspNetCore.Mvc.FromQuery(Name = "minPrice")] decimal? filterMinPrice,""", endpoints);
        Assert.Contains("""[global::Microsoft.AspNetCore.Mvc.FromQuery(Name = "stocks")] int[]? filterStocks,""", endpoints);
        Assert.DoesNotContain("NotBindable", endpoints);
        Assert.DoesNotContain("filterReadOnly", endpoints);

        Assert.Contains("var filter = new global::Shop.ProductFilter();", endpoints);
        Assert.Contains("if (filterMinPrice is { } minPriceValue)", endpoints);
        Assert.Contains("if (filterStocks is { Length: > 0 } stocksValue)", endpoints);
        Assert.Contains("query = filter.Apply(query);", endpoints);
    }

    [Fact]
    public void WithoutFilterable_ListHasNoFilterCode()
    {
        var endpoints = RunWithChain(string.Empty).AssertCompiles().Sources["Shop.Product.Endpoints.g.cs"];

        Assert.DoesNotContain("filter", endpoints);
        Assert.DoesNotContain("SearchAsync", endpoints);
    }

    [Fact]
    public void Searchable_PicksTheConditionTypeFromTheFieldType()
    {
        var run = RunWithChain(
            ".Searchable(p => p.Id, p => p.Name, p => p.Description, p => p.Price, p => p.Stock, p => p.Active, p => p.Kind, p => p.SupplierId)")
            .AssertCompiles();

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        Assert.Contains("public global::Haldus.EqualityCondition<global::System.Guid>? Id { get; init; }", dtos);
        Assert.Contains("public global::Haldus.StringCondition? Name { get; init; }", dtos);
        Assert.Contains("public global::Haldus.RangeCondition<decimal>? Price { get; init; }", dtos);
        Assert.Contains("public global::Haldus.RangeCondition<int>? Stock { get; init; }", dtos);
        Assert.Contains("public global::Haldus.EqualityCondition<bool>? Active { get; init; }", dtos);
        Assert.Contains("public global::Haldus.EqualityCondition<global::Shop.Kind>? Kind { get; init; }", dtos);
        Assert.Contains("public global::Haldus.EqualityCondition<global::System.Guid>? SupplierId { get; init; }", dtos);

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("HaldusSearch.Equality<Product, global::System.Guid>(item => item.Id, where.Id, budget)", endpoints);
        Assert.Contains("HaldusSearch.String<Product>(item => item.Description, where.Description, budget)", endpoints);
        Assert.Contains("HaldusSearch.Range<Product, decimal>(item => item.Price, where.Price, budget)", endpoints);
        Assert.Contains("HaldusSearch.NullableRange<Product, int>(item => item.Stock, where.Stock, budget)", endpoints);
        Assert.Contains("HaldusSearch.NullableEquality<Product, global::System.Guid>(item => item.SupplierId, where.SupplierId, budget)", endpoints);
    }

    [Fact]
    public void Searchable_ExposesOnlyTheListedFields()
    {
        var run = RunWithChain(".Searchable(p => p.Name)").AssertCompiles();

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        var where = dtos[dtos.IndexOf("record ProductWhere", StringComparison.Ordinal)..];

        Assert.Contains("StringCondition? Name", where);
        Assert.DoesNotContain("Price", where);
        Assert.DoesNotContain("Description", where);
        Assert.Contains("""[global::System.Text.Json.Serialization.JsonStringEnumMemberName("name")]""", where);
        Assert.Contains("JsonUnmappedMemberHandling.Disallow", where);
    }

    [Fact]
    public void Searchable_CanTargetTraitFieldsButNotTenantOrVersion()
    {
        var allowed = RunWithChain(".Auditable(true).Searchable(p => p.CreatedAt, p => p.UpdatedBy)").AssertCompiles();
        var tenant = RunWithChain(".MultiTenant().Searchable(p => p.TenantId)");
        var version = RunWithChain(".ConcurrencyChecked().Searchable(p => p.Version)");

        var dtos = allowed.Sources["Shop.Product.Dtos.g.cs"];
        Assert.Contains("global::Haldus.RangeCondition<global::System.DateTimeOffset>? CreatedAt", dtos);
        Assert.Contains("global::Haldus.StringCondition? UpdatedBy", dtos);

        Assert.Equal("HALDUS005", Assert.Single(tenant.GeneratorDiagnostics).Id);
        Assert.Equal("HALDUS005", Assert.Single(version.GeneratorDiagnostics).Id);
    }

    [Fact]
    public void Searchable_AddsIncludeFlagsForSoftDeleteAndArchive()
    {
        var run = RunWithChain(".SoftDeletable().Archivable().Searchable(p => p.Name)").AssertCompiles();

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        Assert.Contains("public bool? IncludeDeleted { get; init; }", dtos);
        Assert.Contains("public bool? IncludeArchived { get; init; }", dtos);

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("if (request.IncludeDeleted == true)", endpoints);
        Assert.Contains("if (request.IncludeArchived != true)", endpoints);
    }

    [Theory]
    [InlineData(".Searchable(p => p.Tags)", "p => p.Tags")]
    [InlineData(".Searchable(p => p.Missing)", "p => p.Missing")]
    [InlineData(".Searchable(p => p.Name.Length)", "p => p.Name.Length")]
    [InlineData(".Searchable(p => (object)p.Price)", "p => (object)p.Price")]
    public void Searchable_WithUnsearchableField_ReportsHaldus005(string chain, string expectedArgument)
    {
        var run = RunWithChain(chain);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS005", diagnostic.Id);
        Assert.Contains(expectedArgument, diagnostic.GetMessage());
        Assert.Empty(run.Sources);
    }

    [Fact]
    public void Searchable_WithoutFields_ReportsHaldus006()
    {
        var run = RunWithChain(".Searchable()");

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS006", diagnostic.Id);
        Assert.Empty(run.Sources);
    }

    [Fact]
    public void FilterableAndSearchableOnAVariable_ReportHaldus004()
    {
        var run = GeneratorHarness.Run($$"""
            {{Product}}

            namespace Shop
            {
                public class ProductFilter : IFilter<Product>
                {
                    public System.Linq.IQueryable<Product> Apply(System.Linq.IQueryable<Product> query) => query;
                }
            }

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    var products = app.MapCrud<Shop.Product>();
                    products.Filterable<Shop.ProductFilter>();
                    products.Searchable(p => p.Name);
                }
            }
            """);

        Assert.Equal(["HALDUS004", "HALDUS004"], run.GeneratorDiagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public void FilterAndSearchSnapshot()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop.Catalog
            {
                public partial class Product
                {
                    public string Name { get; set; } = "";
                    public decimal Price { get; set; }
                    public int? Stock { get; set; }
                }

                public sealed class ProductFilter : IFilter<Product>
                {
                    public string? Search { get; set; }
                    public decimal? MinPrice { get; set; }

                    public System.Linq.IQueryable<Product> Apply(System.Linq.IQueryable<Product> query) => query
                        .WhereIf(Search is not null, product => product.Name.Contains(Search!))
                        .WhereIf(MinPrice is not null, product => product.Price >= MinPrice);
                }

                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app) =>
                        app.MapCrud<Product>()
                            .SoftDeletable()
                            .Filterable<ProductFilter>()
                            .Searchable(product => product.Name, product => product.Price, product => product.Stock);
                }
            }
            """).AssertCompiles();

        Snapshot.Verify(run);
    }
}
