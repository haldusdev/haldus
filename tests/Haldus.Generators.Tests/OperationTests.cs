namespace Haldus.Generators.Tests;

public sealed class OperationTests
{
    private const string Types = """
        namespace Shop
        {
            public sealed class QuoteRequest
            {
                [Required]
                public string? Country { get; set; }
                public decimal Weight { get; set; } = 1m;
                public int[] Sizes { get; set; } = [];
                public string ReadOnly => "";
                public object? NotBindable { get; set; }
            }

            public sealed record Quote(decimal Price);

            public sealed record Recalculate;

            public sealed record RecalculateResult(int Changed);
        }
        """;

    private static GeneratorRun Run(string body, string extraTypes = "") =>
        GeneratorHarness.Run($$"""
            using System.Threading;
            using System.Threading.Tasks;

            {{Types}}

            {{extraTypes}}

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    {{body}}
                }
            }
            """);

    [Fact]
    public void MapQuery_GeneratesAnEndpointThatBindsEachPropertyFromTheQueryString()
    {
        var run = Run("app.MapQuery<Shop.QuoteRequest, Shop.Quote>();").AssertCompiles();

        var endpoint = run.Sources["Shop.QuoteRequest.Query.g.cs"];
        Assert.Contains("internal static class QuoteRequestQueryEndpoint", endpoint);
        Assert.Contains("""[global::Microsoft.AspNetCore.Mvc.FromQuery(Name = "country")] string? queryCountry,""", endpoint);
        Assert.Contains("""[global::Microsoft.AspNetCore.Mvc.FromQuery(Name = "weight")] decimal? queryWeight,""", endpoint);
        Assert.Contains("""[global::Microsoft.AspNetCore.Mvc.FromQuery(Name = "sizes")] int[]? querySizes,""", endpoint);
        Assert.DoesNotContain("ReadOnly", endpoint);
        Assert.DoesNotContain("NotBindable", endpoint);
        Assert.Contains("if (queryWeight is { } weightValue)", endpoint);
        Assert.Contains("if (querySizes is { Length: > 0 } sizesValue)", endpoint);
        Assert.Contains("global::Haldus.HaldusValidation.Validate(request, messages)", endpoint);

        Assert.Contains(
            "global::Haldus.HaldusQueryRegistry.Register<global::Shop.QuoteRequest, global::Shop.Quote>(global::Shop.QuoteRequestQueryEndpoint.Map);",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void MapQuery_WithLambda_GeneratesTheSameEndpoint()
    {
        var withHandlerClass = Run("app.MapQuery<Shop.QuoteRequest, Shop.Quote>();").AssertCompiles();
        var withLambda = Run(
            "app.MapQuery<Shop.QuoteRequest, Shop.Quote>((request, _) => Task.FromResult(new Shop.Quote(request.Weight)));")
            .AssertCompiles();

        Assert.Equal(
            withHandlerClass.Sources["Shop.QuoteRequest.Query.g.cs"],
            withLambda.Sources["Shop.QuoteRequest.Query.g.cs"]);
    }

    [Fact]
    public void MapQuery_MappedTwice_GeneratesTheEndpointOnce()
    {
        var run = Run("""
            app.MapQuery<Shop.QuoteRequest, Shop.Quote>("quotes/a");
                    app.MapQuery<Shop.QuoteRequest, Shop.Quote>("quotes/b");
            """).AssertCompiles();

        Assert.Single(run.Sources.Keys, key => key.EndsWith(".Query.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void MapQuery_WithoutParameterlessConstructor_ReportsHaldus008()
    {
        var run = Run(
            "app.MapQuery<Shop.PositionalRequest, Shop.Quote>();",
            """
            namespace Shop
            {
                public sealed record PositionalRequest(string Country);
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS008", diagnostic.Id);
        Assert.Contains("PositionalRequest", diagnostic.GetMessage());
        Assert.DoesNotContain(run.Sources.Keys, key => key.EndsWith(".Query.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void HandlerClasses_AreRegisteredForEveryHandlerInterfaceTheyImplement()
    {
        var run = Run(
            "app.MapAction<Shop.Recalculate, Shop.RecalculateResult>();",
            """
            namespace Shop
            {
                public sealed class QuoteHandler : IQueryHandler<QuoteRequest, Quote>
                {
                    public Task<Quote> HandleAsync(QuoteRequest request, CancellationToken cancellationToken) =>
                        Task.FromResult(new Quote(1m));
                }

                public sealed class RecalculateHandler :
                    IActionHandler<Recalculate, RecalculateResult>,
                    IActionHandler<Recalculate>
                {
                    Task<RecalculateResult> IActionHandler<Recalculate, RecalculateResult>.HandleAsync(
                        Recalculate request, CancellationToken cancellationToken) =>
                        Task.FromResult(new RecalculateResult(0));

                    Task IActionHandler<Recalculate>.HandleAsync(Recalculate request, CancellationToken cancellationToken) =>
                        Task.CompletedTask;
                }

                public abstract class BaseHandler : IActionHandler<Recalculate>
                {
                    public abstract Task HandleAsync(Recalculate request, CancellationToken cancellationToken);
                }

                public sealed class NotAHandler : System.IDisposable
                {
                    public void Dispose() { }
                }
            }
            """).AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        const string TryAddScoped = "ServiceCollectionDescriptorExtensions.TryAddScoped";

        Assert.Contains(
            $"{TryAddScoped}<global::Haldus.IQueryHandler<global::Shop.QuoteRequest, global::Shop.Quote>, global::Shop.QuoteHandler>(services);",
            module);
        Assert.Contains(
            $"{TryAddScoped}<global::Haldus.IActionHandler<global::Shop.Recalculate, global::Shop.RecalculateResult>, global::Shop.RecalculateHandler>(services);",
            module);
        Assert.Contains(
            $"{TryAddScoped}<global::Haldus.IActionHandler<global::Shop.Recalculate>, global::Shop.RecalculateHandler>(services);",
            module);
        Assert.DoesNotContain("BaseHandler", module);
        Assert.DoesNotContain("NotAHandler", module);
    }

    [Fact]
    public void PartialHandlerClass_IsRegisteredOnce()
    {
        var run = Run(
            string.Empty,
            """
            namespace Shop
            {
                public sealed partial class QuoteHandler : IQueryHandler<QuoteRequest, Quote>
                {
                    public Task<Quote> HandleAsync(QuoteRequest request, CancellationToken cancellationToken) =>
                        Task.FromResult(new Quote(1m));
                }

                public sealed partial class QuoteHandler : System.IDisposable
                {
                    public void Dispose() { }
                }
            }
            """).AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        var first = module.IndexOf("global::Shop.QuoteHandler>(services);", StringComparison.Ordinal);

        Assert.True(first >= 0);
        Assert.Equal(-1, module.IndexOf("global::Shop.QuoteHandler>(services);", first + 1, StringComparison.Ordinal));
    }

    [Fact]
    public void MapAction_NeedsNoGeneratedEndpoint()
    {
        // Komut gövdeden bağlandığı için endpoint çalışma zamanında kurulur; üretilen tek şey handler kaydıdır.
        var run = Run("app.MapAction<Shop.Recalculate>((_, _) => Task.CompletedTask);").AssertCompiles();

        Assert.Empty(run.Sources);
    }

    [Fact]
    public void OperationsSnapshot()
    {
        var run = Run(
            """
            app.MapQuery<Shop.QuoteRequest, Shop.Quote>();
                    app.MapAction<Shop.Recalculate, Shop.RecalculateResult>();
            """,
            """
            namespace Shop
            {
                public sealed class QuoteHandler : IQueryHandler<QuoteRequest, Quote>
                {
                    public Task<Quote> HandleAsync(QuoteRequest request, CancellationToken cancellationToken) =>
                        Task.FromResult(new Quote(request.Weight));
                }

                public sealed class RecalculateHandler : IActionHandler<Recalculate, RecalculateResult>
                {
                    public Task<RecalculateResult> HandleAsync(Recalculate request, CancellationToken cancellationToken) =>
                        Task.FromResult(new RecalculateResult(0));
                }
            }
            """).AssertCompiles();

        Snapshot.Verify(run);
    }
}
