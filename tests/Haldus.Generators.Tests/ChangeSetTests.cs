namespace Haldus.Generators.Tests;

/// <summary>Değişiklik kümeleri (<c>ChangeSets</c>).</summary>
public sealed class ChangeSetTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum OrderStatus { Draft, Placed, Cancelled }

            public partial class Order : IComputed
            {
                [System.ComponentModel.DataAnnotations.MaxLength(200)]
                public string CustomerName { get; set; } = "";

                public decimal Total { get; private set; }

                // Satırlara başvurmaz: satırlar her senaryoda eşlenmez.
                public void Compute() => Total = CustomerName.Length;
            }

            public partial class OrderItem
            {
                public int Quantity { get; set; }
            }

            public partial class Note
            {
                public string Text { get; set; } = "";
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

    private const string Orders = """
        var orders = app.MapCrud<Order>().ConcurrencyChecked()
            .Workflow<OrderStatus>().EditableIn(OrderStatus.Draft)
            .ChangeSets(OrderStatus.Placed);
        orders.MapDetail<OrderItem>();
        orders.MapTransition("place", OrderStatus.Draft, OrderStatus.Placed);
        """;

    [Fact]
    public void ChangeSets_AddTheEditEndpoints()
    {
        var run = Run(Orders).AssertCompiles();
        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];

        Assert.Contains("""MapPost("/{id:guid}/edits", BeginEditAsync)""", endpoints);
        Assert.Contains("""MapGet("/{id:guid}/edits", ListEditsAsync)""", endpoints);
        Assert.Contains("""MapGet("/{id:guid}/edits/{editId:guid}", GetEditAsync)""", endpoints);
        Assert.Contains("""MapPatch("/{id:guid}/edits/{editId:guid}", StageEditAsync)""", endpoints);
        Assert.Contains("""MapGet("/{id:guid}/edits/{editId:guid}/preview", PreviewEditAsync)""", endpoints);
        Assert.Contains("""MapPost("/{id:guid}/edits/{editId:guid}/commit", CommitEditAsync)""", endpoints);
        Assert.Contains("""MapDelete("/{id:guid}/edits/{editId:guid}", DiscardEditAsync)""", endpoints);

        // Sahnelenen değişiklik kısmi güncelleme gövdesiyle aynı biçimdedir.
        Assert.Contains(".Accepts<PatchOrderRequest>(\"application/json\")", endpoints);
        Assert.Contains("HaldusChangeSetRequests.Stage<PatchOrderRequest>(edit, changes, httpContext, messages)", Handler(endpoints, "StageEditAsync"));

        Assert.Contains("global::Haldus.HaldusChangeSets.Configure(modelBuilder);", run.Sources["HaldusModule.g.cs"]);
        Assert.DoesNotContain("HaldusChangeSets", Run("app.MapCrud<Note>();").AssertCompiles().Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Commit_GoesThroughTheRulesOfAPartialUpdate()
    {
        var endpoints = Run(Orders).AssertCompiles().Sources["Shop.Order.Endpoints.g.cs"];
        var commit = Handler(endpoints, "CommitEditAsync");
        var patch = Handler(endpoints, "PatchAsync");

        // Sürüm, kümenin açık olması ve içeriğin yeniden doğrulanması.
        Assert.Contains("global::Haldus.HaldusETag.ReadIfMatch(httpContext)", commit);
        Assert.Contains("if (edit.Status != global::Haldus.ChangeSetStatus.Open)", commit);
        Assert.Contains("HaldusChangeSetRequests.Read<PatchOrderRequest>(edit, httpContext, messages, out var request)", commit);

        // Düzenlenebilir durum kuralının yerini kümenin kendi durum listesi alır.
        Assert.Contains("if (entity.Status is not (global::Shop.OrderStatus.Placed))", commit);
        Assert.Contains("if (entity.Status is not (global::Shop.OrderStatus.Draft))", patch);
        Assert.DoesNotContain("OrderStatus.Draft", commit);

        // Atamalar, satırların eşitlenmesi ve hesaplama kısmi güncellemeyle aynı parçalardır.
        foreach (var shared in new[]
        {
            "if (((global::Haldus.IHaldusPatch)request).IsSet(\"CustomerName\"))",
            "SyncOrderItems(entity, request.OrderItems!, context, messages)",
            "((global::Haldus.IComputed)entity).Compute();",
            "Property(item => item.Version).OriginalValue = expectedVersion;",
        })
        {
            Assert.Contains(shared, patch);
            Assert.Contains(shared, commit);
        }

        // Küme, kayıtla aynı kaydetmede kapanır.
        var closed = commit.IndexOf("global::Haldus.HaldusChangeSets.Close(context, edit, committed: true);", StringComparison.Ordinal);
        var saved = commit.IndexOf("await context.SaveChangesAsync(cancellationToken);", StringComparison.Ordinal);
        Assert.True(closed >= 0 && closed < saved);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(commit, "SaveChangesAsync"));

        // Önizleme aynı kurallardan geçer ama kaydetmez ve sürüm istemez.
        var preview = Handler(endpoints, "PreviewEditAsync");
        Assert.Contains("if (entity.Status is not (global::Shop.OrderStatus.Placed))", preview);
        Assert.Contains("context.ChangeTracker.Clear();", preview);
        Assert.DoesNotContain("SaveChangesAsync", preview);
        Assert.DoesNotContain("ReadIfMatch", preview);

        // Küme yalnızca sayılan durumlarda açılır.
        Assert.Contains("if (entity.Status is not (global::Shop.OrderStatus.Placed))", Handler(endpoints, "BeginEditAsync"));
    }

    [Fact]
    public void ChangeSets_OfARecordWithoutStates_AreAlwaysAllowed()
    {
        var endpoints = Run("app.MapCrud<Note>().ChangeSets();").AssertCompiles().Sources["Shop.Note.Endpoints.g.cs"];
        var commit = Handler(endpoints, "CommitEditAsync");

        Assert.DoesNotContain("StateConflict", commit);

        // Kaynağın kendi belirteci olmasa da kümenin durumu çakışmayı yakalar: aynı küme iki kez uygulanamaz.
        Assert.Contains("catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)", commit);
        Assert.Contains("StateChanged(messages)", commit);
    }

    [Theory]
    [InlineData("app.MapCrud<Order>().Workflow<OrderStatus>().EditableIn(OrderStatus.Draft).ChangeSets();", "HALDUS010", "must list the states in which a record may be edited through a change set")]
    [InlineData("app.MapCrud<Note>().ChangeSets(OrderStatus.Placed);", "HALDUS010", "ChangeSets can list states only on a resource with a state machine")]
    [InlineData("app.MapCrud<Note>().Without(Operations.Patch).ChangeSets();", "HALDUS007", "ChangeSets requires the Patch operation")]
    [InlineData("app.MapAppendOnly<Note>().ChangeSets();", "HALDUS007", "ChangeSets")]
    [InlineData("var notes = app.MapCrud<Note>().ChangeSets(); notes.MapAction(\"edits\", note => { });", "HALDUS007", "edits")]
    public void ChangeSets_RejectDeclarationsThatCannotWork(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void ChangeSetSnapshot()
    {
        var run = Run("app.MapCrud<Note>().ChangeSets();").AssertCompiles();

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
