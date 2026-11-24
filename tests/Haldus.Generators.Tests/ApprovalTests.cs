namespace Haldus.Generators.Tests;

/// <summary>Onaya bağlı kayıtlar (<c>Approvable</c>) ve belgeler (<c>Document</c>).</summary>
public sealed class ApprovalTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum RequestStatus { Open, Ordered }

            public partial class Expense
            {
                public string Description { get; set; } = "";
                public decimal Amount { get; set; }
            }

            public partial class Contract : IAmendment<Contract>
            {
                public string Title { get; set; } = "";
                public int Revision { get; private set; } = 1;

                public void Amend(Contract cancelled) => Revision = cancelled.Revision + 1;
            }

            public partial class Counter
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
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

    // ── Onay ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Approvable_AddsTheDecisionFieldsAndEndpoints()
    {
        var run = Run("app.MapCrud<Expense>().Approvable();").AssertCompiles();

        var entity = run.Sources["Shop.Expense.Entity.g.cs"];
        Assert.Contains("partial class Expense : global::Haldus.IApprovable", entity);
        Assert.Contains("public global::Haldus.ApprovalStatus ApprovalStatus { get; set; }", entity);
        Assert.Contains("public string? RequestedBy { get; set; }", entity);

        // Alanlar yanıtta görünür, isteklerde yer almaz; durum adıyla yazılır.
        var dtos = run.Sources["Shop.Expense.Dtos.g.cs"];
        var response = Between(dtos, "record ExpenseResponse", "record CreateExpenseRequest");
        Assert.Contains("JsonStringEnumConverter<global::Haldus.ApprovalStatus>", response);
        Assert.Contains("public required int ApprovalLevel { get; init; }", response);
        Assert.Contains("DecisionNote", response);
        Assert.DoesNotContain("Approval", dtos.Substring(dtos.IndexOf("record CreateExpenseRequest", StringComparison.Ordinal)));
        Assert.DoesNotContain("RequestedBy", dtos.Substring(dtos.IndexOf("record CreateExpenseRequest", StringComparison.Ordinal)));

        var endpoints = run.Sources["Shop.Expense.Endpoints.g.cs"];
        Assert.Contains("""MapPost("/{id:guid}/approve", ApproveAsync)""", endpoints);
        Assert.Contains("""MapPost("/{id:guid}/reject", RejectAsync)""", endpoints);
        Assert.Contains("""MapGet("/{id:guid}/approvals", ListApprovalsAsync)""", endpoints);
        Assert.Contains("context, entity, global::System.Convert.ToString(id, global::System.Globalization.CultureInfo.InvariantCulture)!, approve: true, request?.Note, levels: 1, cancellationToken", endpoints);
        Assert.Contains("public global::System.Collections.Generic.List<string> Contributors { get; set; } = new();", entity);
        Assert.Contains("approve: false, request?.Note, levels: 1, cancellationToken", endpoints);
        Assert.Contains("ApprovalDenied(messages, outcome, \"approve\", entity.ApprovalStatus)", endpoints);
    }

    [Fact]
    public void Approvable_LocksApprovedContentOnEveryUpdatePath()
    {
        var run = Run("app.MapCrud<Expense>().Approvable().Batch().Previewable().Metadata();").AssertCompiles();
        var endpoints = run.Sources["Shop.Expense.Endpoints.g.cs"];

        // İçeriği değiştiren her güncelleme yolu (PUT, PATCH, toplu güncelleme, önizleme) kilide uyar.
        foreach (var method in new[] { "UpdateAsync", "PatchAsync", "BatchUpdateItemAsync", "PreviewUpdateAsync" })
        {
            var handler = Handler(endpoints, method);

            Assert.Contains("entity.ApprovalStatus == global::Haldus.ApprovalStatus.Approved", handler);
            Assert.Contains("StateConflict(messages, \"update\", entity.ApprovalStatus)", handler);
        }

        // Onayı düşürmek uçların değil interceptor'ın işidir: içerik hangi yoldan değişirse değişsin çalışır. Üretilen
        // kod yalnızca hangi alanların içerik olduğunu bildirir; önizleme, kaydetmediği için kuralı kendisi uygular.
        Assert.DoesNotContain("HaldusApprovals.Reset(", endpoints);
        Assert.Contains("global::Haldus.HaldusApprovals.ResetIfChanged(entity, context);", Handler(endpoints, "PreviewUpdateAsync"));
        Assert.Contains(
            "global::Haldus.HaldusApprovals.Register<global::Shop.Expense>(\"Description\", \"Amount\", \"Metadata\");",
            run.Sources["HaldusModule.g.cs"]);

        // Silme içeriği değiştirmez; onaya bağlı değildir.
        Assert.DoesNotContain("ApprovalStatus", Handler(endpoints, "DeleteAsync"));
    }

    [Fact]
    public void Approvable_TiesTheDecisionToTheContentTheApproverSaw()
    {
        var plain = Run("app.MapCrud<Expense>().Approvable();").AssertCompiles().Sources["HaldusModule.g.cs"];
        var versioned = Run("app.MapCrud<Expense>().Approvable().ConcurrencyChecked();").AssertCompiles();

        Assert.Contains("global::Haldus.HaldusApprovals.Configure(modelBuilder);", plain);
        Assert.Contains("entity.Property(item => item.ApprovalStatus).HasConversion<string>().HasMaxLength(16);", plain);
        Assert.Contains("entity.Property(item => item.ApprovalStatus).IsConcurrencyToken();", plain);
        Assert.Contains("entity.Property(item => item.ApprovalLevel).IsConcurrencyToken();", plain);
        Assert.Contains("entity.Property(item => item.RequestedAt).IsConcurrencyToken();", plain);

        // Sürüm kontrollü kaynakta aynı işi satır sürümü görür: karar If-Match ister.
        Assert.DoesNotContain("IsConcurrencyToken", versioned.Sources["HaldusModule.g.cs"]);
        var approve = Handler(versioned.Sources["Shop.Expense.Endpoints.g.cs"], "ApproveAsync");
        Assert.Contains("PreconditionRequired(messages)", approve);
        Assert.Contains("PreconditionFailed(messages)", approve);

        // Karar kayıtlarının tablosu yalnızca onaya bağlı bir kaynak varsa modele girer.
        Assert.DoesNotContain("HaldusApprovals", Run("app.MapCrud<Expense>();").AssertCompiles().Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Approvable_WithLevelsAndGates_HoldsTheNamedTransitionsBack()
    {
        var run = Run("""
            var expenses = app.MapCrud<Expense>().Workflow<RequestStatus>().Approvable(levels: 2, "order", "audit");
            expenses.MapTransition("order", RequestStatus.Open, RequestStatus.Ordered);
            expenses.MapTransition("reopen", RequestStatus.Ordered, RequestStatus.Open);
            expenses.MapAction("audit", expense => expense.Description = expense.Description.Trim());
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Expense.Endpoints.g.cs"];
        Assert.Contains("levels: 2, cancellationToken", endpoints);

        Assert.Contains("StateConflict(messages, \"order\", entity.ApprovalStatus)", Handler(endpoints, "TransitionOrderAsync"));
        Assert.Contains("StateConflict(messages, \"audit\", entity.ApprovalStatus)", Handler(endpoints, "ActionAuditAsync"));

        // Onayın izin verdiği işlemin yazdıkları onayı düşürmez; onay istemeyen işleminkiler düşürür.
        Assert.Contains("global::Haldus.HaldusApprovals.Exempt(entity);", Handler(endpoints, "TransitionOrderAsync"));
        Assert.Contains("global::Haldus.HaldusApprovals.Exempt(entity);", Handler(endpoints, "ActionAuditAsync"));
        Assert.DoesNotContain("Approval", Handler(endpoints, "TransitionReopenAsync"));
    }

    [Fact]
    public void ApprovalRoles_TieEachLevelToARole()
    {
        var run = Run("app.MapCrud<Expense>().ApprovalRoles(\"manager\", \"finance\").Approvable(levels: 2);").AssertCompiles();
        var endpoints = run.Sources["Shop.Expense.Endpoints.g.cs"];

        // Onay da ret de sıradaki düzeyin rolüne sorulur; rolü çalışma zamanındaki servis söyler.
        foreach (var name in new[] { "ApproveAsync", "RejectAsync" })
        {
            var handler = Handler(endpoints, name);
            Assert.Contains("levels: 2, roles: new[] { \"manager\", \"finance\" }, isInRole: role => global::Haldus.HaldusRoles.IsInRole(httpContext, role), cancellationToken);", handler);
        }

        // Rol bildirilmediyse karar role bağlı değildir.
        var plain = Run("app.MapCrud<Expense>().Approvable(levels: 2);").AssertCompiles().Sources["Shop.Expense.Endpoints.g.cs"];
        Assert.Contains("levels: 2, cancellationToken);", Handler(plain, "ApproveAsync"));
        Assert.DoesNotContain("isInRole", plain);
    }

    [Theory]
    [InlineData("app.MapCrud<Expense>().ApprovalRoles(\"manager\");", "HALDUS010", "declare Approvable(levels: ...) as well")]
    [InlineData("app.MapCrud<Expense>().Approvable(levels: 2).ApprovalRoles(\"manager\");", "HALDUS010", "lists 1 role(s), but Approvable declares 2 level(s)")]
    [InlineData("app.MapCrud<Expense>().Approvable().ApprovalRoles(\"\");", "HALDUS010", "non-empty string constants")]
    [InlineData("app.MapStore<Expense>().ApprovalRoles(\"manager\");", "HALDUS007", "ApprovalRoles cannot be combined with MapStore")]
    [InlineData("app.MapCrud<Expense>().Approvable(levels: 0);", "HALDUS010", "between 1 and 10 levels")]
    [InlineData("app.MapCrud<Expense>().Approvable(1, \"ship\");", "HALDUS010", "has no transition or action with that name")]
    [InlineData("app.MapStore<Expense>().Approvable();", "HALDUS007", "Approvable cannot be combined with MapStore")]
    [InlineData(
        "app.MapCrud<Expense>().Approvable().MapDetail<Contract>().AsSubresource();",
        "HALDUS007",
        "its details cannot have endpoints of their own")]
    [InlineData(
        """
        var expenses = app.MapCrud<Expense>().Approvable();
        expenses.MapAction("approve", expense => expense.Amount = 0);
        """,
        "HALDUS007",
        "collides with the 'approve' endpoint")]
    public void Approvable_RejectsDeclarationsThatCannotWork(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void Approvable_WorksOnRecordsThatAreNeverEdited()
    {
        var run = Run("app.MapAppendOnly<Expense>().Approvable();").AssertCompiles();

        var endpoints = run.Sources["Shop.Expense.Endpoints.g.cs"];
        Assert.Contains("""MapPost("/{id:guid}/approve", ApproveAsync)""", endpoints);
        Assert.DoesNotContain("HaldusApprovals.Reset", endpoints);
        Assert.Contains("HaldusApprovals.Register<global::Shop.Expense>(\"Description\", \"Amount\");", run.Sources["HaldusModule.g.cs"]);
    }

    // ── Belge ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Document_IsAFixedStateMachineWithItsOwnTransitions()
    {
        var run = Run("app.MapCrud<Contract>().Document();").AssertCompiles();

        var entity = run.Sources["Shop.Contract.Entity.g.cs"];
        Assert.Contains("public global::Haldus.DocumentStatus Status { get; set; }", entity);
        Assert.Contains("public global::System.Guid? AmendedFromId { get; set; }", entity);
        Assert.Contains("public global::System.Guid? AmendedById { get; set; }", entity);

        // Gönderme ve iptal kullanıcı yazmadan gelir ve standart uçlarla birlikte eşlenir.
        var endpoints = run.Sources["Shop.Contract.Endpoints.g.cs"];
        Assert.Contains("MapSubmitTransition(group, apply: null);", endpoints);
        Assert.Contains("MapCancelTransition(group, apply: null);", endpoints);
        Assert.Contains("""MapPost("/{id:guid}/submit", (""", endpoints);
        Assert.Contains("""MapPost("/{id:guid}/cancel", (""", endpoints);
        Assert.Contains("entity.Status = global::Haldus.DocumentStatus.Submitted;", endpoints);
        Assert.Contains("entity.Status = global::Haldus.DocumentStatus.Cancelled;", endpoints);
        Assert.DoesNotContain("HaldusTransitionRegistry.Register", run.Sources["HaldusModule.g.cs"]);

        // Yalnızca taslak düzenlenir ve silinir.
        Assert.Contains("entity.Status is not (global::Haldus.DocumentStatus.Draft)", Handler(endpoints, "UpdateAsync"));
        Assert.Contains("entity.Status is not (global::Haldus.DocumentStatus.Draft)", Handler(endpoints, "DeleteAsync"));
    }

    [Fact]
    public void Document_IsAmendedOnlyWhenCancelled()
    {
        var run = Run("app.MapCrud<Contract>().Document();").AssertCompiles();

        var endpoints = run.Sources["Shop.Contract.Endpoints.g.cs"];
        Assert.Contains("""MapPost("/{id:guid}/amend", AmendAsync)""", endpoints);

        var amend = Handler(endpoints, "AmendAsync");
        Assert.Contains("original.Status is not (global::Haldus.DocumentStatus.Cancelled)", amend);
        Assert.Contains("Status = global::Haldus.DocumentStatus.Draft", amend);
        Assert.Contains("AmendedFromId = original.Id", amend);
        Assert.Contains("((global::Haldus.IAmendment<Contract>)entity).Amend(original);", amend);
        Assert.Contains("original.AmendedById = entity.Id;", amend);
        Assert.Contains("global::Haldus.HaldusMessageKeys.AmendedDetail", amend);

        Assert.Contains("record AmendContractRequest", run.Sources["Shop.Contract.Dtos.g.cs"]);
        Assert.Contains("UX_Contracts_AmendedFromId", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Document_LetsAWrittenTransitionReplaceTheGeneratedOne()
    {
        var run = Run("""
            var contracts = app.MapCrud<Contract>().Document().Numbered("CTR-{seq:4}", assignOn: DocumentStatus.Submitted);
            contracts.MapTransition("submit", DocumentStatus.Draft, DocumentStatus.Submitted, contract => contract.Title = contract.Title.Trim());
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Contract.Endpoints.g.cs"];

        // Kullanıcının yazdığı geçiş çağrısıyla eşlenir; yalnızca yazılmayan iptal kendiliğinden eşlenir.
        Assert.DoesNotContain("MapSubmitTransition(group, apply: null);", endpoints);
        Assert.Contains("MapCancelTransition(group, apply: null);", endpoints);
        Assert.Contains("HaldusTransitionRegistry.Register<global::Shop.Contract>(\"submit\"", run.Sources["HaldusModule.g.cs"]);

        // Numara belge gönderilirken atanır.
        Assert.Contains("HaldusNumbering.AllocateAsync", Handler(endpoints, "TransitionSubmitAsync"));
    }

    [Theory]
    [InlineData("app.MapCrud<Contract>().Document().Workflow<RequestStatus>();", "HALDUS007", "Document() fixes the states")]
    [InlineData("app.MapCrud<Contract>().Document().EditableIn(DocumentStatus.Draft);", "HALDUS007", "Document() fixes the states")]
    [InlineData("app.MapAppendOnly<Contract>().Document();", "HALDUS007", "Document cannot be combined with MapAppendOnly")]
    [InlineData("app.MapCrud<Contract>().Document().Without(CrudOperation.Create);", "HALDUS007", "Document requires the Create operation")]
    [InlineData("app.MapCrud<Counter>().Document();", "HALDUS007", "generated by the database")]
    [InlineData(
        """
        var contracts = app.MapCrud<Contract>().Document();
        contracts.MapAction("amend", contract => contract.Title = "");
        """,
        "HALDUS007",
        "collides with the 'amend' endpoint")]
    public void Document_RejectsDeclarationsThatCannotWork(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void ApprovalSnapshot()
    {
        var run = Run("""
            var expenses = app.MapCrud<Expense>().Workflow<RequestStatus>().Approvable(levels: 2, "order");
            expenses.MapTransition("order", RequestStatus.Open, RequestStatus.Ordered);
            app.MapCrud<Contract>().Document();
            """).AssertCompiles();

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

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);

        Assert.True(from >= 0 && to > from, $"'{start}' … '{end}' not found");
        return text.Substring(from, to - from);
    }
}
