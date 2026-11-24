namespace Haldus.Generators.Tests;

/// <summary>Hassas alanlar, ölçüte göre silme ve işin yeniden çalıştırılması.</summary>
public sealed class HousekeepingTests
{
    private const string Entities = """
        namespace Shop
        {
            public partial class Contact
            {
                public string Name { get; set; } = "";

                [System.ComponentModel.DataAnnotations.MaxLength(20)]
                public string? NationalId { get; set; }

                public string Code { get; private set; } = "";
            }

            public sealed class ContactFilter : IFilter<Contact>
            {
                public string? Name { get; set; }

                public string[] Tags { get; set; } = [];

                public System.Linq.IQueryable<Contact> Apply(System.Linq.IQueryable<Contact> query) => query;
            }

            public partial class Report
            {
                public int Year { get; set; }

                public int Rows { get; private set; }
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
    public void Sensitive_FieldsAreInRequestsButNotInResponses()
    {
        var run = Run("app.MapCrud<Contact>().Revisioned().Cloneable().Sensitive(contact => contact.NationalId);").AssertCompiles();

        var dtos = run.Sources["Shop.Contact.Dtos.g.cs"];
        var response = dtos.Substring(0, dtos.IndexOf("record CreateContactRequest", StringComparison.Ordinal));
        Assert.DoesNotContain("NationalId", response);
        Assert.Contains("MaxLengthAttribute(20)]\n    public string? NationalId { get; init; }", dtos.Substring(response.Length));

        // Yanıtın tek izdüşümü alanı taşımaz; yazma yolları ise taşır (kopya dahil).
        var endpoints = run.Sources["Shop.Contact.Endpoints.g.cs"];
        Assert.DoesNotContain("NationalId = item.NationalId", endpoints);
        Assert.Contains("NationalId = request.NationalId", Handler(endpoints, "CreateAsync"));
        Assert.Contains("NationalId = source.NationalId", Handler(endpoints, "CloneAsync"));

        // Saklanan sürümlere girmez ve geri yükleme ona dokunmaz.
        Assert.Contains("global::Haldus.HaldusRevisions.Hide<global::Shop.Contact>(\"NationalId\");", run.Sources["HaldusModule.g.cs"]);
        var rollback = Handler(endpoints, "RollbackAsync");
        Assert.Contains("entity.Name = revision.Name;", rollback);
        Assert.DoesNotContain("NationalId", rollback);
    }

    [Fact]
    public void Purgeable_DeletesMatchingRecordsThroughTheRulesOfASingleDelete()
    {
        var run = Run("app.MapCrud<Contact>().Filterable<ContactFilter>().Batch().Purgeable();").AssertCompiles();
        var endpoints = run.Sources["Shop.Contact.Endpoints.g.cs"];

        Assert.Contains("""MapPost("/purge", PurgeMatchingAsync)""", endpoints);

        var purge = Handler(endpoints, "PurgeMatchingAsync");

        // Ölçütsüz istek reddedilir; boş dizi de ölçüt sayılmaz.
        Assert.Contains("if (filterName is null && filterTags is null or { Length: 0 })", purge);
        Assert.Contains("PurgeCriteriaRequired(messages)", purge);

        // Ölçüt, liste ucunun filtresidir ve kaynağın kendi filtrelerinden geçer.
        Assert.Contains("query = filter.Apply(query);", purge);
        Assert.DoesNotContain("IgnoreQueryFilters", purge);

        // force verilmedikçe yalnızca sayar; verilirse her kayıt toplu silmenin öğe yolundan, tek sınır içinde silinir.
        var counted = purge.IndexOf("if (force != true)", StringComparison.Ordinal);
        var deleted = purge.IndexOf("BatchDeleteItemAsync(ids[index], context, messages, cancellationToken)", StringComparison.Ordinal);
        Assert.True(counted >= 0 && counted < deleted);
        Assert.Contains("await using var batch = await global::Haldus.HaldusBatch.BeginAsync(context, cancellationToken);", purge);
        Assert.Contains(".Take(MaxPurge)", purge);
        Assert.DoesNotContain("ExecuteDelete", purge);
    }

    [Fact]
    public void Rerunnable_CopiesTheInputsOfAFinishedJob()
    {
        var run = Run("app.MapJob<Report>().Rerunnable().Idempotent();").AssertCompiles();

        Assert.Contains("public global::System.Guid? RerunOfId { get; set; }", run.Sources["Shop.Report.Entity.g.cs"]);

        var endpoints = run.Sources["Shop.Report.Endpoints.g.cs"];
        Assert.Contains("""MapPost("/{id:guid}/rerun", RerunAsync)""", endpoints);

        var rerun = Handler(endpoints, "RerunAsync");
        Assert.Contains("if (source.Status is global::Haldus.JobStatus.Queued or global::Haldus.JobStatus.Running)", rerun);

        // Girdiler kopyalanır; çıktılar ve yaşam döngüsü alanları yeni işte baştan başlar.
        Assert.Contains("Year = source.Year, RerunOfId = source.Id", rerun);
        Assert.DoesNotContain("Rows = source.Rows", rerun);
        Assert.DoesNotContain("Status = source.Status", rerun);

        // Yeni iş, oluşturma ucundaki gibi kuyruğuyla birlikte kaydedilir ve sonra teslim edilir.
        var prepared = rerun.IndexOf("jobs.Prepare(context, ticket);", StringComparison.Ordinal);
        var saved = rerun.IndexOf("await context.SaveChangesAsync(cancellationToken);", StringComparison.Ordinal);
        var dispatched = rerun.IndexOf("await jobs.DispatchAsync(ticket, cancellationToken);", StringComparison.Ordinal);
        Assert.True(prepared >= 0 && prepared < saved && saved < dispatched);

        Assert.DoesNotContain("RerunAsync", Run("app.MapJob<Report>();").AssertCompiles().Sources["Shop.Report.Endpoints.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Contact>().Sensitive(contact => contact.Code);", "HALDUS010", "cannot be sensitive")]
    [InlineData("app.MapCrud<Contact>().Sensitive(contact => contact.NationalId).Sortable(contact => contact.NationalId);", "HALDUS010", "cannot be sorted")]
    [InlineData("app.MapCrud<Contact>().Sensitive(contact => contact.NationalId).Searchable(contact => contact.NationalId);", "HALDUS005", "NationalId")]
    [InlineData("app.MapCrud<Contact>().Sensitive(contact => contact.Name).NaturalKey(contact => contact.Name);", "HALDUS010", "Name")]
    [InlineData("app.MapCrud<Contact>().Sensitive(contact => contact.Name).Translatable(contact => contact.Name);", "HALDUS010", "cannot be translated")]
    [InlineData("app.MapCrud<Contact>().Batch().Purgeable();", "HALDUS007", "Purgeable needs Filterable<TFilter>() for its criteria and Batch()")]
    [InlineData("app.MapCrud<Contact>().Filterable<ContactFilter>().Purgeable();", "HALDUS007", "Purgeable needs Filterable<TFilter>() for its criteria and Batch()")]
    [InlineData("app.MapCrud<Contact>().Filterable<ContactFilter>().Batch().Without(Operations.Delete).Purgeable();", "HALDUS007", "Purgeable requires the Delete operation")]
    [InlineData("app.MapReadOnly<Contact>().Filterable<ContactFilter>().Batch().Purgeable();", "HALDUS007", "Purgeable")]
    [InlineData("app.MapCrud<Contact>().Rerunnable();", "HALDUS007", "Rerunnable")]
    [InlineData("app.MapCrud<Contact>().Batch(); app.MapJob<Report>().Imports<Contact>().Rerunnable();", "HALDUS007", "Rerunnable cannot be combined with Imports")]
    public void Declarations_ThatCannotWork_AreRejected(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void HousekeepingSnapshot()
    {
        var run = Run("""
            app.MapCrud<Contact>().Sensitive(contact => contact.NationalId).Filterable<ContactFilter>().Batch().Purgeable();
            app.MapJob<Report>().Rerunnable();
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
}
