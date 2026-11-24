namespace Haldus.Generators.Tests;

/// <summary>Bir kayıt üzerinde çalışan ama durumunu değiştirmeyen aksiyonlar (<c>orders.MapAction(...)</c>).</summary>
public sealed class EntityActionTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum OrderStatus { Draft, Placed }

            public partial class Coupon
            {
                public string Code { get; set; } = "";

                public int TimesRedeemed { get; set; }
            }

            public partial class Settings
            {
                public string Theme { get; set; } = "";
            }

            public sealed record Rename([property: MaxLength(50)] string Code) : IEntityAction<Coupon>
            {
                public void Apply(Coupon entity) => entity.Code = Code;
            }

            public sealed record ExtendCoupon
            {
                [Range(1, 30)]
                public int Days { get; init; }
            }

            public sealed class ExtendCouponHandler : IEntityActionHandler<Coupon, ExtendCoupon>
            {
                public System.Threading.Tasks.Task HandleAsync(Coupon entity, ExtendCoupon request, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }

            public sealed record Estimate(int Quantity);

            public sealed record EstimateResult(decimal Total);

            public sealed class EstimateHandler : IEntityActionHandler<Coupon, Estimate, EstimateResult>
            {
                public System.Threading.Tasks.Task<EstimateResult> HandleAsync(Coupon entity, Estimate request, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.Task.FromResult(new EstimateResult(0));
            }
        }
        """;

    private static GeneratorRun Run(string body) =>
        GeneratorHarness.Run($$"""
            {{Entities}}

            namespace Shop
            {
                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app)
                    {
                        {{body}}
                    }
                }
            }
            """);

    [Fact]
    public void Action_NeedsNoStateMachineAndReturnsTheRecord()
    {
        var run = Run("""
            var coupons = app.MapCrud<Coupon>();
            coupons.MapAction("redeem", coupon => coupon.TimesRedeemed++);
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Coupon.Endpoints.g.cs"];
        var action = endpoints[endpoints.LastIndexOf("> ActionRedeemAsync(", StringComparison.Ordinal)..];

        Assert.Contains("public static global::Microsoft.AspNetCore.Builder.RouteHandlerBuilder MapRedeemAction(", endpoints);
        Assert.Contains("return group.MapPost(\"/{id:guid}/redeem\", (", endpoints);
        Assert.Contains(".WithName(\"RedeemCoupon\")", endpoints);

        // Durum kontrolü ve durum ataması yoktur; kayıt bulunur, mantık çalışır, kaydedilir ve döner.
        Assert.DoesNotContain("StateConflict", action);
        Assert.DoesNotContain("entity.Status", action);
        Assert.Contains("NotFound(messages, \"Coupon\", id)", action);
        Assert.Contains("await apply(entity, httpContext.RequestServices, cancellationToken);", action);
        Assert.Contains("TransitionRejected(messages, rejection.Message)", action);
        Assert.Contains("await context.SaveChangesAsync(cancellationToken);", action);
        Assert.Contains("TypedResults.Ok(ToResponse(entity))", action);

        Assert.Contains(
            "global::Haldus.HaldusTransitionRegistry.Register<global::Shop.Coupon>(\"redeem\", global::Shop.CouponEndpoints.MapRedeemAction);",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Action_TakesItsLogicFromTheRequestALambdaOrAHandler()
    {
        var run = Run("""
            var coupons = app.MapCrud<Coupon>();
            coupons.MapAction<Rename>();
            coupons.MapAction<ExtendCoupon>();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Coupon.Endpoints.g.cs"];
        var rename = Section(endpoints, "> ActionRenameAsync(", "> ActionExtendAsync(");
        var extend = endpoints[endpoints.LastIndexOf("> ActionExtendAsync(", StringComparison.Ordinal)..];

        // Adres istek tipinin adından türetilir; entity adı atılır.
        Assert.Contains("group.MapPost(\"/{id:guid}/rename\", (", endpoints);
        Assert.Contains("group.MapPost(\"/{id:guid}/extend\", (", endpoints);

        Assert.Contains("((global::Haldus.IEntityAction<Coupon>)request).Apply(entity);", rename);
        Assert.DoesNotContain("RequireActionHandler<Coupon, global::Shop.Rename>", endpoints);

        Assert.Contains(".GetService<global::Haldus.IEntityActionHandler<Coupon, global::Shop.ExtendCoupon>>(httpContext.RequestServices) is { } handler)", extend);
        Assert.Contains("global::Haldus.HaldusTransitionRegistry.RequireActionHandler<Coupon, global::Shop.ExtendCoupon>(group);", endpoints);

        // İstek gövdesi doğrulanır.
        Assert.Contains(".AddEndpointFilter<global::Haldus.HaldusValidationFilter<global::Shop.ExtendCoupon>>()", endpoints);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains(
            "TryAddScoped<global::Haldus.IEntityActionHandler<global::Shop.Coupon, global::Shop.ExtendCoupon>, global::Shop.ExtendCouponHandler>(services);",
            module);
        Assert.Contains(
            "global::Haldus.HaldusTransitionRegistry.Register<global::Shop.Coupon, global::Shop.Rename>(null, global::Shop.CouponEndpoints.MapRenameAction);",
            module);
    }

    [Fact]
    public void Action_CanReturnItsOwnResultAndStillSavesTheRecord()
    {
        var run = Run("""
            var coupons = app.MapCrud<Coupon>();
            coupons.MapAction<Estimate, EstimateResult>();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Coupon.Endpoints.g.cs"];
        var action = endpoints[endpoints.LastIndexOf("> ActionEstimateAsync(", StringComparison.Ordinal)..];

        Assert.Contains(
            "Results<global::Microsoft.AspNetCore.Http.HttpResults.Ok<global::Shop.EstimateResult>, global::Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>> ActionEstimateAsync(",
            endpoints);
        Assert.Contains("global::Shop.EstimateResult result;", action);
        Assert.Contains(
            ".GetRequiredService<global::Haldus.IEntityActionHandler<Coupon, global::Shop.Estimate, global::Shop.EstimateResult>>(httpContext.RequestServices)",
            action);
        Assert.Contains("await context.SaveChangesAsync(cancellationToken);", action);
        Assert.Contains("TypedResults.Ok(result)", action);

        // Sonuç döndüren aksiyonda mantık zorunludur.
        Assert.Contains("RequireActionHandler<Coupon, global::Shop.Estimate, global::Shop.EstimateResult>(group);", endpoints);
        Assert.Contains(
            "Register<global::Shop.Coupon, global::Shop.Estimate, global::Shop.EstimateResult>(null, global::Shop.CouponEndpoints.MapEstimateAction);",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Action_WithALambdaThatReturnsAResult_Compiles()
    {
        var run = Run("""
            var coupons = app.MapCrud<Coupon>();
            coupons.MapAction<Estimate, EstimateResult>((coupon, request) => new EstimateResult(request.Quantity), name: "quote");
            """).AssertCompiles();

        Assert.Contains("group.MapPost(\"/{id:guid}/quote\", (", run.Sources["Shop.Coupon.Endpoints.g.cs"]);
        Assert.Contains(">(\"quote\", global::Shop.CouponEndpoints.MapQuoteAction);", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Action_FollowsTheWriteRulesOfItsResource()
    {
        var versioned = Run("""
            var coupons = app.MapCrud<Coupon>().ConcurrencyChecked().Idempotent();
            coupons.MapAction("redeem", coupon => { });
            """).AssertCompiles();
        var stateful = Run("""
            var coupons = app.MapCrud<Coupon>().Workflow<OrderStatus>();
            coupons.MapAction("redeem", coupon => { });
            """).AssertCompiles();

        var endpoints = versioned.Sources["Shop.Coupon.Endpoints.g.cs"];
        var action = endpoints[endpoints.LastIndexOf("> ActionRedeemAsync(", StringComparison.Ordinal)..];

        // Kaydı değiştiren her uç gibi If-Match ister ve tekrara karşı korunur.
        Assert.Contains("global::Haldus.HaldusETag.ReadIfMatch(httpContext)", action);
        Assert.Contains("PreconditionFailed(messages)", action);
        Assert.Contains("global::Haldus.HaldusETag.Write(httpContext, entity.Version);", action);
        Assert.Contains(".AddEndpointFilter(new global::Haldus.HaldusIdempotencyFilter<Coupon>(null, required: false))", endpoints);

        // Durum makinesi olan kaynakta aksiyon durumu değiştirmez, ama kaydı yazarken durumun değişmediğini doğrular.
        var statefulEndpoints = stateful.Sources["Shop.Coupon.Endpoints.g.cs"];
        var statefulAction = statefulEndpoints[statefulEndpoints.LastIndexOf("> ActionRedeemAsync(", StringComparison.Ordinal)..];
        Assert.DoesNotContain("entity.Status =", statefulAction);
        Assert.Contains("StateChanged(messages)", statefulAction);
    }

    [Fact]
    public void Action_WorksOnResourcesAddressedByAClientKey()
    {
        var run = Run("""
            var flags = app.MapStore<Settings>();
            flags.MapAction("reset", settings => settings.Theme = "");
            """).AssertCompiles();

        Assert.Contains("group.MapPost(\"/{key}/reset\", (", run.Sources["Shop.Settings.Endpoints.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapSingleton<Settings>().MapAction(\"reset\", settings => { });", "HALDUS007", "MapAction needs a resource that is addressed by its key")]
    [InlineData("app.MapCrud<Coupon>().MapAction(\"Redeem Now\", coupon => { });", "HALDUS010", "not a valid action name")]
    [InlineData("var name = \"redeem\"; app.MapCrud<Coupon>().MapAction(name, coupon => { });", "HALDUS010", "name of an action must be a constant")]
    [InlineData("app.MapCrud<Coupon>().Archivable().MapAction(\"archive\", coupon => { });", "HALDUS007", "The action 'archive' collides")]
    [InlineData("var coupons = app.MapCrud<Coupon>().Workflow<OrderStatus>(); coupons.MapTransition(\"place\", OrderStatus.Draft, OrderStatus.Placed); coupons.MapAction(\"place\", coupon => { });", "HALDUS007", "already has a transition or an action named 'place'")]
    public void InvalidActions_AreReported(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void UnboundAction_OnTheApplicationIsLeftAlone()
    {
        // app.MapAction<TRequest, TResponse>() entity'ye bağlı değildir; üretici onu bir kaynağa bağlamaz.
        var run = Run("""
            app.MapCrud<Coupon>();
            app.MapAction<Estimate, EstimateResult>((request, _) => System.Threading.Tasks.Task.FromResult(new EstimateResult(0)));
            """).AssertCompiles();

        Assert.DoesNotContain("ActionEstimateAsync", run.Sources["Shop.Coupon.Endpoints.g.cs"]);
        Assert.DoesNotContain("HaldusTransitionRegistry", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void EntityActionSnapshot()
    {
        var run = Run("""
            var coupons = app.MapCrud<Coupon>().ConcurrencyChecked();
            coupons.MapAction("redeem", coupon => coupon.TimesRedeemed++);
            coupons.MapAction<Rename>();
            coupons.MapAction<Estimate, EstimateResult>();
            """).AssertCompiles();

        Snapshot.Verify(run);
    }

    private static string Section(string source, string start, string end)
    {
        var from = source.LastIndexOf(start, StringComparison.Ordinal);
        var to = from < 0 ? -1 : source.IndexOf(end, from, StringComparison.Ordinal);

        Assert.True(from >= 0 && to > from, $"Could not locate the section between '{start}' and '{end}'.");
        return source[from..to];
    }
}
