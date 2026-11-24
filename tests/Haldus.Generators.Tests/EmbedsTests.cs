namespace Haldus.Generators.Tests;

public sealed class EmbedsTests
{
    private const string Types = """
        namespace Shop
        {
            public sealed record Money(decimal Amount, string Currency);

            public readonly record struct Dimensions(int Width, int Height);

            public partial class Plan
            {
                public string Name { get; set; } = "";
                public Money Price { get; set; } = new(0m, "TRY");
                public Dimensions Size { get; set; }
            }
        }
        """;

    private static GeneratorRun Run(string chain, string types = Types) =>
        GeneratorHarness.Run($$"""
            {{types}}

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Shop.Plan>(){{chain}};
            }
            """);

    [Fact]
    public void WithoutEmbeds_ValueObjectPropertiesStayOutOfTheDtos()
    {
        var dtos = Run(string.Empty).AssertCompiles().Sources["Shop.Plan.Dtos.g.cs"];

        Assert.DoesNotContain("Price", dtos);
        Assert.DoesNotContain("Size", dtos);
    }

    [Fact]
    public void Embeds_PutsTheValueObjectInEveryDtoAndMapsItAsAComplexProperty()
    {
        var run = Run(".Embeds<Shop.Money>().Embeds<Shop.Dimensions>()").AssertCompiles();

        var dtos = run.Sources["Shop.Plan.Dtos.g.cs"];
        Assert.Contains("public required global::Shop.Money Price { get; init; }", dtos);
        Assert.Contains("public required global::Shop.Dimensions Size { get; init; }", dtos);

        // Referans tipi null olamayacağı için istekte zorunludur; struct'ın varsayılan değeri vardır.
        Assert.Contains(
            "[global::System.ComponentModel.DataAnnotations.RequiredAttribute(AllowEmptyStrings = true)]\n"
            + "    public global::Shop.Money Price { get; init; } = default!;",
            dtos);
        Assert.Contains("    public global::Shop.Dimensions Size { get; init; }\n", dtos);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.ComplexProperty(item => item.Price);", module);
        Assert.Contains("entity.ComplexProperty(item => item.Size);", module);
    }

    [Fact]
    public void Embeds_PatchReplacesTheValueAsAWhole()
    {
        var run = Run(".Embeds<Shop.Money>().Embeds<Shop.Dimensions>()").AssertCompiles();

        var dtos = run.Sources["Shop.Plan.Dtos.g.cs"];
        Assert.Contains("public global::Shop.Money? Price", dtos);
        Assert.Contains("public global::Shop.Dimensions? Size", dtos);

        var endpoints = run.Sources["Shop.Plan.Endpoints.g.cs"];
        Assert.Contains("entity.Price = request.Price!;", endpoints);
        Assert.Contains("entity.Size = request.Size.GetValueOrDefault();", endpoints);
    }

    [Fact]
    public void Embeds_OnlyAffectsPropertiesOfTheDeclaredType()
    {
        var dtos = Run(".Embeds<Shop.Money>()").AssertCompiles().Sources["Shop.Plan.Dtos.g.cs"];

        Assert.Contains("Price", dtos);
        Assert.DoesNotContain("Size", dtos);
    }

    [Theory]
    [InlineData(".Embeds<Shop.Unused>()", "Embeds<Unused> has no effect")]
    [InlineData(".Embeds<Shop.Money>().Embeds<Shop.Unused>()", "Embeds<Unused> has no effect")]
    public void Embeds_WithoutAMatchingProperty_IsReported(string chain, string expectedMessage)
    {
        var run = Run(chain, Types + "\nnamespace Shop { public sealed record Unused(int Value); }");

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS007", diagnostic.Id);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void Embeds_OnANullableProperty_IsReported()
    {
        var run = Run(
            ".Embeds<Shop.Money>()",
            """
            namespace Shop
            {
                public sealed record Money(decimal Amount, string Currency);

                public partial class Plan
                {
                    public Money? Price { get; set; }
                }
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS007", diagnostic.Id);
        Assert.Contains("'Plan.Price' is an embedded value and cannot be nullable", diagnostic.GetMessage());
    }

    [Fact]
    public void EmbeddedValue_CannotBeSearched()
    {
        var run = Run(".Embeds<Shop.Money>().Searchable(plan => plan.Price)");

        Assert.Equal("HALDUS005", Assert.Single(run.GeneratorDiagnostics).Id);
    }

    [Fact]
    public void EmbedsOnAVariable_ReportsHaldus004()
    {
        var run = GeneratorHarness.Run($$"""
            {{Types}}

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    var plans = app.MapCrud<Shop.Plan>();
                    plans.Embeds<Shop.Money>();
                }
            }
            """);

        Assert.Equal("HALDUS004", Assert.Single(run.GeneratorDiagnostics).Id);
    }

    [Fact]
    public void EmbedsSnapshot()
    {
        var run = Run(".Embeds<Shop.Money>()").AssertCompiles();

        Snapshot.Verify(run);
    }
}
