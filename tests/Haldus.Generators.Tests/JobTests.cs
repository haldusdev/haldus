namespace Haldus.Generators.Tests;

public sealed class JobTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum Stage { One, Two }

            public partial class ReportExport
            {
                [Range(2000, 2100)]
                public int Year { get; set; }

                public string? DownloadUrl { get; private set; }
            }

            public sealed class ReportExportHandler : IJobHandler<ReportExport>
            {
                public System.Threading.Tasks.Task HandleAsync(ReportExport job, JobContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }

            public partial class Recount
            {
            }

            public partial class NumberedJob
            {
                public int Id { get; set; }
            }

            public partial class Project
            {
                public string Name { get; set; } = "";
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
    public void Job_IsARecordWithHaldusOwnedStateFields()
    {
        var run = Run("app.MapJob<ReportExport>();").AssertCompiles();

        var entity = run.Sources["Shop.ReportExport.Entity.g.cs"];
        Assert.Contains("partial class ReportExport : global::Haldus.IHaldusJob", entity);
        Assert.Contains("public global::Haldus.JobStatus Status { get; set; }", entity);
        Assert.Contains("public int Attempts { get; set; }", entity);
        Assert.Contains("public bool CancelRequested { get; set; }", entity);

        var dtos = run.Sources["Shop.ReportExport.Dtos.g.cs"];
        var response = dtos[..dtos.IndexOf("record CreateReportExportRequest", StringComparison.Ordinal)];
        var request = dtos[response.Length..];

        // Girdi yazılabilir alanlardır; çıktı ve durum yalnızca yanıtta görünür.
        Assert.Contains("public int Year { get; init; }", request);
        Assert.DoesNotContain("DownloadUrl", request);
        Assert.DoesNotContain("Status", request);
        Assert.Contains("public required string? DownloadUrl { get; init; }", response);
        Assert.Contains(
            "    [global::System.Text.Json.Serialization.JsonConverter(typeof(global::System.Text.Json.Serialization.JsonStringEnumConverter<global::Haldus.JobStatus>))]\n"
            + "    public required global::Haldus.JobStatus Status { get; init; }",
            response);
        Assert.Contains("public required int Progress { get; init; }", response);
        Assert.Contains("public required string? Error { get; init; }", response);

        // Yürütücünün iç alanları istemciye gösterilmez.
        Assert.DoesNotContain("Attempts", response);
        Assert.DoesNotContain("CancelRequested", response);
        Assert.DoesNotContain("Culture", response);

        // İş güncellenemez ve silinemez.
        Assert.DoesNotContain("UpdateReportExportRequest", dtos);
        Assert.DoesNotContain("PatchReportExportRequest", dtos);
    }

    [Fact]
    public void Create_SavesTheJobWithItsQueueEntryAndAnswersAccepted()
    {
        var run = Run("app.MapJob<ReportExport>();").AssertCompiles();

        var endpoints = run.Sources["Shop.ReportExport.Endpoints.g.cs"];

        Assert.Contains("global::Haldus.IHaldusJobDispatcher jobs,", endpoints);
        Assert.Contains(
            "CreatedAt = global::Haldus.HaldusTime.UtcNow(timeProvider), Culture = global::System.Globalization.CultureInfo.CurrentUICulture.Name, Year = request.Year",
            endpoints);

        // Altyapı kuyruk kaydını aynı bağlama ekleyebilsin diye kaydetmeden önce haberdar edilir.
        Assert.Contains(
            "        var ticket = new global::Haldus.HaldusJobTicket(global::Haldus.HaldusJobRegistry.Name<ReportExport>(), entity.Id);\n"
            + "        jobs.Prepare(context, ticket);\n"
            + "        context.Set<ReportExport>().Add(entity);\n"
            + "        await context.SaveChangesAsync(cancellationToken);\n"
            + "        await jobs.DispatchAsync(ticket, cancellationToken);\n",
            endpoints);
        Assert.Contains("return global::Microsoft.AspNetCore.Http.TypedResults.Accepted(location, ToResponse(entity));", endpoints);

        Assert.DoesNotContain("MapPut", endpoints);
        Assert.DoesNotContain("MapPatch", endpoints);
        Assert.DoesNotContain("MapDelete", endpoints);
    }

    [Fact]
    public void Cancel_IsDelegatedToTheRuntimeAndReportsFinishedJobsAsAConflict()
    {
        var run = Run("app.MapJob<ReportExport>();").AssertCompiles();

        var endpoints = run.Sources["Shop.ReportExport.Endpoints.g.cs"];
        Assert.Contains("group.MapPost(\"/{id:guid}/cancel\", CancelAsync)", endpoints);
        Assert.Contains("if (!await global::Haldus.HaldusJobs.CancelAsync(context, entity, timeProvider, cancellationToken))", endpoints);
        Assert.Contains("StateConflict(messages, \"cancel\", entity.Status)", endpoints);
    }

    [Fact]
    public void List_CanBeFilteredByStatus()
    {
        var run = Run("app.MapJob<ReportExport>();").AssertCompiles();

        var endpoints = run.Sources["Shop.ReportExport.Endpoints.g.cs"];
        Assert.Contains("        global::Haldus.JobStatus? status,\n", endpoints);
        Assert.Contains("query = query.Where(item => item.Status == statusValue);", endpoints);
    }

    [Fact]
    public void Module_RegistersTheJobItsHandlerAndItsModel()
    {
        var run = Run("app.MapJob<ReportExport>();").AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusJobRegistry.Register<global::Shop.ReportExport>();", module);
        Assert.Contains(
            "TryAddScoped<global::Haldus.IJobHandler<global::Shop.ReportExport>, global::Shop.ReportExportHandler>(services);",
            module);

        // Durum eşzamanlılık belirtecidir: iki yürütücü aynı işi sahiplenemez.
        Assert.Contains(
            "entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(64).IsConcurrencyToken();",
            module);
    }

    [Fact]
    public void Job_WithALambda_NeedsNoHandlerClass()
    {
        var run = Run("app.MapJob<Recount>((job, context, cancellationToken) => System.Threading.Tasks.Task.CompletedTask);").AssertCompiles();

        Assert.Contains("global::Haldus.HaldusJobRegistry.Register<global::Shop.Recount>();", run.Sources["HaldusModule.g.cs"]);
        Assert.Contains("TypedResults.Accepted(", run.Sources["Shop.Recount.Endpoints.g.cs"]);
    }

    [Fact]
    public void Job_ComposesWithTenancyAuditingAndIdempotency()
    {
        var run = Run("app.MapJob<ReportExport>().MultiTenant().Auditable(trackUser: true).Idempotent();").AssertCompiles();

        var entity = run.Sources["Shop.ReportExport.Entity.g.cs"];
        Assert.Contains("global::Haldus.IMultiTenant, global::Haldus.IAuditable, global::Haldus.IUserAuditable, global::Haldus.IHaldusJob", entity);

        // Oluşturulma zamanı hem denetim hem iş alanıdır; bir kez üretilir.
        Assert.Equal(1, CountOccurrences(entity, " CreatedAt "));
        Assert.Equal(1, CountOccurrences(run.Sources["Shop.ReportExport.Dtos.g.cs"], " CreatedAt "));

        var endpoints = run.Sources["Shop.ReportExport.Endpoints.g.cs"];
        Assert.Contains("group.AddEndpointFilter<global::Haldus.HaldusTenantRequiredFilter>();", endpoints);
        Assert.Contains("new global::Haldus.HaldusIdempotencyFilter<ReportExport>(typeof(CreateReportExportRequest), required: false)", endpoints);
    }

    [Theory]
    [InlineData("app.MapJob<ReportExport>().SoftDeletable();", "HALDUS007", "SoftDeletable cannot be combined with MapJob")]
    [InlineData("app.MapJob<ReportExport>().ConcurrencyChecked();", "HALDUS007", "ConcurrencyChecked cannot be combined with MapJob")]
    [InlineData("app.MapJob<ReportExport>().Archivable();", "HALDUS007", "Archivable cannot be combined with MapJob")]
    [InlineData("app.MapJob<ReportExport>().Workflow<Stage>();", "HALDUS007", "Workflow cannot be combined with MapJob")]
    [InlineData("app.MapJob<ReportExport>().Numbered();", "HALDUS007", "Numbered cannot be combined with MapJob")]
    [InlineData("app.MapJob<ReportExport>().Expiring();", "HALDUS007", "Expiring cannot be combined with MapJob")]
    [InlineData("app.MapCrud<Project>(); app.MapJob<ReportExport>().Under<Project>();", "HALDUS007", "Under cannot be combined with MapJob")]
    [InlineData("app.MapJob<ReportExport>().Without(CrudOperation.Delete);", "HALDUS007", "MapJob does not have the operation(s) 'Delete'")]
    [InlineData("app.MapJob<NumberedJob>();", "HALDUS003", "supported key types for this archetype are Guid")]
    public void InvalidJobs_AreReported(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void JobSnapshot()
    {
        var run = Run("app.MapJob<ReportExport>().MultiTenant();").AssertCompiles();

        Snapshot.Verify(run);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
