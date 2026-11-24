namespace Haldus.Generators.Tests;

/// <summary>Olay yayını (<c>PublishesEvents</c>).</summary>
public sealed class EventTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum TicketStatus { Open, Closed }

            public partial class SupportTicket
            {
                public string Subject { get; set; } = "";

                public string? Secret { get; set; }
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
    public void PublishesEvents_RegistersTheResourceWithItsResponseShape()
    {
        var run = Run("""
            var tickets = app.MapCrud<SupportTicket>().Workflow<TicketStatus>().Sensitive(ticket => ticket.Secret).PublishesEvents();
            tickets.MapTransition("close", TicketStatus.Open, TicketStatus.Closed);
            tickets.MapAction("touch", ticket => { });
            """).AssertCompiles();

        Assert.Contains("global::Haldus.IPublishesEvents", run.Sources["Shop.SupportTicket.Entity.g.cs"]);

        // Olayın türü kaynağın adından gelir; verisi kaydın yanıt biçimidir, yani yanıtta olmayan alan olayda da yoktur.
        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusEvents.Configure(modelBuilder);", module);
        Assert.Contains(
            "global::Haldus.HaldusEvents.Register<global::Shop.SupportTicket>(\"support-ticket\", static item => global::Shop.SupportTicketEndpoints.Describe(item));",
            module);

        var endpoints = run.Sources["Shop.SupportTicket.Endpoints.g.cs"];
        Assert.Contains("internal static object Describe(SupportTicket item) => ToResponse(item);", endpoints);
        Assert.DoesNotContain("Secret = item.Secret", endpoints);

        // Geçiş kendi adıyla da yayınlanır ve olay kaydetmeden önce istenir; aksiyon yalnızca güncelleme olayıdır.
        var close = Handler(endpoints, "TransitionCloseAsync");
        var raised = close.IndexOf("global::Haldus.HaldusEvents.Raise(context, entity, \"close\");", StringComparison.Ordinal);
        Assert.True(raised >= 0 && raised < close.IndexOf("SaveChangesAsync", StringComparison.Ordinal));
        Assert.DoesNotContain("HaldusEvents", Handler(endpoints, "ActionTouchAsync"));

        // Oluşturma, güncelleme ve silme uçlarında olay kodu yoktur: onları, kaydı hangi kod yazarsa yazsın interceptor yazar.
        Assert.DoesNotContain("HaldusEvents", Handler(endpoints, "CreateAsync"));
        Assert.DoesNotContain("HaldusEvents", Handler(endpoints, "DeleteAsync"));
    }

    [Fact]
    public void WithoutPublishesEvents_NothingIsGenerated()
    {
        var run = Run("app.MapCrud<SupportTicket>();").AssertCompiles();

        Assert.DoesNotContain("HaldusEvents", run.Sources["HaldusModule.g.cs"]);
        Assert.DoesNotContain("Describe", run.Sources["Shop.SupportTicket.Endpoints.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapStore<SupportTicket>().PublishesEvents();", "HALDUS007", "PublishesEvents")]
    [InlineData("app.MapSingleton<SupportTicket>().PublishesEvents();", "HALDUS007", "PublishesEvents")]
    public void PublishesEvents_IsRejectedWhereARecordHasNoKeyOfItsOwn(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
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
