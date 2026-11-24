namespace Haldus.Generators.Tests;

/// <summary>Tekrar serisi (<c>Recurring</c>) ve mantığın eklediği gömülü satırlar.</summary>
public sealed class RecurrenceTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum MeetingStatus { Planned, Held }

            public partial class Meeting
            {
                public string Title { get; set; } = "";
            }

            public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
        }
        """;

    // Aksiyon satır koleksiyonuna başvurur; koleksiyon yalnızca satırları eşleyen senaryoda üretilir.
    private const string Garments = """
        namespace Shop
        {
            public partial class Garment
            {
                public string Name { get; set; } = "";
            }

            public partial class Variant
            {
                public string Sku { get; set; } = "";
            }

            public sealed class Generate : IEntityAction<Garment>
            {
                public void Apply(Garment entity) => entity.Variants.Add(new Variant { Sku = entity.Name });
            }
        }
        """;

    private static GeneratorRun Run(string body, string extra = "") =>
        GeneratorHarness.Run($$"""
            {{Entities}}

            {{extra}}

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
    public void Recurring_AddsTheRuleAsWritableFieldsAndComputesOccurrences()
    {
        var run = Run("app.MapCrud<Meeting>().Workflow<MeetingStatus>().EditableIn(MeetingStatus.Planned).Recurring();").AssertCompiles();

        var entity = run.Sources["Shop.Meeting.Entity.g.cs"];
        Assert.Contains("global::Haldus.IRecurring", entity);
        Assert.Contains("public global::System.DateTimeOffset StartsAt { get; set; }", entity);
        Assert.Contains("public global::Haldus.RecurrenceFrequency RecurrenceFrequency { get; set; }", entity);
        Assert.Contains("public int? RecurrenceInterval { get; set; }", entity);

        // İlk oluş ve kural istemcinin yazdığı alanlardır: isteklerde de yanıtta da yer alırlar.
        var dtos = run.Sources["Shop.Meeting.Dtos.g.cs"];
        var create = dtos.Substring(dtos.IndexOf("record CreateMeetingRequest", StringComparison.Ordinal));
        Assert.Contains("public global::System.DateTimeOffset StartsAt { get; init; }", create);
        Assert.Contains("RangeAttribute(1, 1000)]\n    public int? RecurrenceCount { get; init; }", create);
        Assert.Contains("public required global::System.DateTimeOffset? RecurrenceUntil { get; init; }", dtos);

        var endpoints = run.Sources["Shop.Meeting.Endpoints.g.cs"];
        Assert.Contains("""MapGet("/{id:guid}/occurrences", OccurrencesAsync)""", endpoints);
        Assert.Contains("""MapPut("/{id:guid}/occurrences/{start}", ChangeOccurrenceAsync)""", endpoints);
        Assert.Contains("""MapDelete("/{id:guid}/occurrences/{start}", RestoreOccurrenceAsync)""", endpoints);

        // Oluşlar saklanmaz; kayıt kendi filtrelerinden geçerek okunur ve oluşları kuraldan hesaplanır.
        var list = Handler(endpoints, "OccurrencesAsync");
        Assert.Contains("global::Haldus.HaldusRecurrence.OccurrencesAsync(context, entity,", list);
        Assert.DoesNotContain("IgnoreQueryFilters", list);

        // Tek bir oluşun değiştirilmesi kaydın düzenlenebildiği durumlarda yapılır.
        foreach (var name in new[] { "ChangeOccurrenceAsync", "RestoreOccurrenceAsync" })
        {
            var change = Handler(endpoints, name);
            Assert.Contains("if (entity.Status is not (global::Shop.MeetingStatus.Planned))", change);
            Assert.Contains("global::Haldus.HaldusRecurrence.ChangeAsync(context, entity,", change);
            Assert.Contains("NotFound(messages, \"Occurrence\", start)", change);

            // İstisna seriyle aynı kaydetmede kalıcı olur ve seri değişmiş sayılır.
            var marked = change.IndexOf("context.Entry(entity).State = global::Microsoft.EntityFrameworkCore.EntityState.Modified;", StringComparison.Ordinal);
            Assert.True(marked >= 0 && marked < change.IndexOf("SaveChangesAsync", StringComparison.Ordinal));
        }

        Assert.Contains("global::Haldus.HaldusRecurrence.Configure(modelBuilder);", run.Sources["HaldusModule.g.cs"]);
        Assert.DoesNotContain("HaldusRecurrence", Run("app.MapCrud<Meeting>();").AssertCompiles().Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void ChangingAnOccurrence_GoesThroughTheGuardsOfAnUpdate()
    {
        var run = Run("app.MapCrud<Meeting>().ConcurrencyChecked().Approvable().Reversible().Recurring();").AssertCompiles();
        var endpoints = run.Sources["Shop.Meeting.Endpoints.g.cs"];
        var update = Handler(endpoints, "UpdateAsync");

        foreach (var name in new[] { "ChangeOccurrenceAsync", "RestoreOccurrenceAsync" })
        {
            var change = Handler(endpoints, name);

            // Güncelleme ucunun her koruması burada da vardır: sürüm, kapanmış kayıt, onaylanmış kayıt.
            foreach (var guard in new[]
            {
                "global::Haldus.HaldusETag.ReadIfMatch(httpContext)",
                "ApprovalStatus.Approved",
                "ReversedById is not null",
                "Property(item => item.Version).OriginalValue = expectedVersion;",
                "PreconditionFailed(messages)",
            })
            {
                Assert.Contains(guard, update);
                Assert.Contains(guard, change);
            }

            Assert.Contains("global::Haldus.HaldusETag.Write(httpContext, entity.Version);", change);
        }

        // Okuma bir yazma değildir.
        Assert.DoesNotContain("ReadIfMatch", Handler(endpoints, "OccurrencesAsync"));
    }

    [Theory]
    [InlineData("app.MapReadOnly<Meeting>().Recurring();", "HALDUS007", "Recurring")]
    [InlineData("app.MapStore<Meeting>().Recurring();", "HALDUS007", "Recurring")]
    [InlineData("var meetings = app.MapCrud<Meeting>().Recurring(); meetings.MapAction(\"occurrences\", meeting => { });", "HALDUS007", "occurrences")]
    public void Recurring_RejectsDeclarationsThatCannotWork(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void Logic_ThatAddsEmbeddedRows_GetsThemKeyedAndOrdered()
    {
        var run = Run("""
            var garments = app.MapCrud<Garment>();
            garments.MapDetail<Variant>();
            garments.MapAction<Generate>();
            """, Garments).AssertCompiles();

        var action = Handler(run.Sources["Shop.Garment.Endpoints.g.cs"], "ActionGenerateAsync");

        // Mantığa satırlar kendi sıralarıyla verilir; sonrasında yeni satırlara kimlik, hepsine sıra verilir. Böylece
        // alan mantığı satırın kimliğini ve sırasını bilmek zorunda kalmaz.
        var sorted = action.IndexOf("entity.Variants.Sort(", StringComparison.Ordinal);
        var applied = action.IndexOf(".Apply(entity);", StringComparison.Ordinal);
        var keyed = action.IndexOf("row.Id = global::System.Guid.CreateVersion7();", StringComparison.Ordinal);
        var saved = action.IndexOf("SaveChangesAsync", StringComparison.Ordinal);
        Assert.True(sorted >= 0 && sorted < applied && applied < keyed && keyed < saved);
        Assert.Contains("row.Position = variantsIndex++;", action);
    }

    [Fact]
    public void RecurrenceSnapshot()
    {
        var run = Run("app.MapCrud<Meeting>().Recurring();").AssertCompiles();

        Snapshot.Verify(run);
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
