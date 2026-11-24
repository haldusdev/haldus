namespace Haldus.Generators.Tests;

/// <summary><c>Numbered</c>, <c>Expiring</c> ve <c>Idempotent</c>.</summary>
public sealed class LifecycleTraitTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum InvoiceStatus { Draft, Issued, Paid }

            public enum OtherStatus { One, Two }

            public partial class Invoice
            {
                public string Customer { get; set; } = "";
            }

            public partial class Reservation
            {
                public int Quantity { get; set; }
            }

            public partial class Settings
            {
                public string Theme { get; set; } = "";
            }
        }
        """;

    private const string Allocate = "await global::Haldus.HaldusNumbering.AllocateAsync(context, Numbering, cancellationToken)";

    private static GeneratorRun Run(string body) =>
        GeneratorHarness.Run($$"""
            {{Entities}}

            namespace Shop
            {
                public static class Endpoints
                {
                    public static readonly TimeSpan Configured = TimeSpan.FromMinutes(5);

                    public static void Map(IEndpointRouteBuilder app)
                    {
                        {{body}}
                    }
                }
            }
            """);

    [Fact]
    public void Numbered_AssignsTheNumberWithTheRecordWhenItIsCreated()
    {
        var run = Run("""app.MapCrud<Invoice>().Numbered("INV-{yyyy}-{seq:6}");""").AssertCompiles();

        Assert.Contains("public string Number { get; set; } = \"\";", run.Sources["Shop.Invoice.Entity.g.cs"]);

        var dtos = run.Sources["Shop.Invoice.Dtos.g.cs"];
        Assert.Contains("public required string Number { get; init; }", dtos);
        Assert.Equal(1, CountOccurrences(dtos, " Number "));

        var endpoints = run.Sources["Shop.Invoice.Endpoints.g.cs"];
        Assert.Contains(
            "private static readonly global::Haldus.NumberFormat Numbering = new(\"Shop.Invoice\", \"INV-{yyyy}-{seq:6}\", gapless: true, perTenant: false);",
            endpoints);

        // Numara, kayıtla aynı transaction'da ayrılır ve onunla birlikte kalıcı olur.
        Assert.Contains(
            $"        await using var numbering = {Allocate};\n"
            + "        entity.Number = numbering.Number;\n"
            + "        context.Set<Invoice>().Add(entity);\n"
            + "        await context.SaveChangesAsync(cancellationToken);\n"
            + "        await numbering.CommitAsync(cancellationToken);\n",
            endpoints);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.Property(item => item.Number).HasMaxLength(64);", module);
        Assert.Contains("entity.HasIndex(item => item.Number).IsUnique();", module);
        Assert.Contains("global::Haldus.HaldusNumbering.Configure(modelBuilder);", module);
        Assert.DoesNotContain("HasSequence", module);
    }

    [Fact]
    public void Numbered_CountsPerTenantOnATenantOwnedResource()
    {
        var run = Run("app.MapCrud<Invoice>().MultiTenant().Numbered();").AssertCompiles();

        Assert.Contains("\"{seq:6}\", gapless: true, perTenant: true);", run.Sources["Shop.Invoice.Endpoints.g.cs"]);
        Assert.Contains("entity.HasIndex(item => new { item.TenantId, item.Number }).IsUnique();", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Numbered_AssignOn_WaitsForTheTransitionIntoThatState()
    {
        var run = Run("""
            var invoices = app.MapCrud<Invoice>().Workflow<InvoiceStatus>().Numbered("INV-{seq}", assignOn: InvoiceStatus.Issued);
            invoices.MapTransition("issue", InvoiceStatus.Draft, InvoiceStatus.Issued);
            invoices.MapTransition("pay", InvoiceStatus.Issued, InvoiceStatus.Paid);
            """).AssertCompiles();

        // O ana kadar numara boştur.
        Assert.Contains("public string? Number { get; set; }", run.Sources["Shop.Invoice.Entity.g.cs"]);
        Assert.Contains("public required string? Number { get; init; }", run.Sources["Shop.Invoice.Dtos.g.cs"]);

        var endpoints = run.Sources["Shop.Invoice.Endpoints.g.cs"];
        var issue = Section(endpoints, "> TransitionIssueAsync(", "> TransitionPayAsync(");

        Assert.Equal(1, CountOccurrences(endpoints, Allocate));

        // Daha önce atanmış numara korunur.
        Assert.Contains($"await using var numbering = entity.Number is null\n            ? {Allocate}\n            : null;", issue);
        Assert.Contains("await numbering.CommitAsync(cancellationToken);", issue);
    }

    [Fact]
    public void Numbered_WithoutGaplessness_UsesASequence()
    {
        var run = Run("""app.MapAppendOnly<Invoice>().MultiTenant().Numbered("INV-{seq:8}", gapless: false);""").AssertCompiles();

        Assert.Contains(
            "new(\"Shop.Invoice\", \"INV-{seq:8}\", gapless: false, perTenant: true, sequence: \"InvoiceNumberSequence\");",
            run.Sources["Shop.Invoice.Endpoints.g.cs"]);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("modelBuilder.HasSequence<long>(\"InvoiceNumberSequence\");", module);

        // Sayaç tablosu yalnızca boşluksuz numaralama için gerekir.
        Assert.DoesNotContain("HaldusNumbering.Configure", module);
    }

    [Fact]
    public void Expiring_WithALifetime_SetsTheExpiryOnTheServerAndHidesExpiredRecords()
    {
        var run = Run("app.MapCrud<Reservation>().Expiring(TimeSpan.FromMinutes(15));").AssertCompiles();

        Assert.Contains("public global::System.DateTimeOffset? ExpiresAt { get; set; }", run.Sources["Shop.Reservation.Entity.g.cs"]);

        // İstek gövdesinden yazılamaz.
        var dtos = run.Sources["Shop.Reservation.Dtos.g.cs"];
        Assert.Equal(1, CountOccurrences(dtos, " ExpiresAt "));

        var endpoints = run.Sources["Shop.Reservation.Endpoints.g.cs"];
        Assert.Contains(
            "private static readonly global::System.TimeSpan Lifetime = global::System.TimeSpan.FromTicks(9000000000L);",
            endpoints);
        Assert.Contains("ExpiresAt = global::Haldus.HaldusTime.UtcNow(timeProvider) + Lifetime, Quantity = request.Quantity", endpoints);

        // Sabit ömür yazmalarla uzamaz.
        Assert.DoesNotContain("entity.ExpiresAt = global::Haldus.HaldusTime.UtcNow(timeProvider) + Lifetime;", endpoints);

        Assert.Contains(
            "entity.HasQueryFilter(global::Haldus.HaldusFilters.Expiry, item => item.ExpiresAt == null "
            + "|| item.ExpiresAt > global::Haldus.HaldusScope.UtcNow(context));",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Expiring_AddsAnEndpointThatEndsTheRecordImmediately()
    {
        var run = Run("app.MapCrud<Reservation>().ConcurrencyChecked().Expiring(TimeSpan.FromHours(1));").AssertCompiles();

        var endpoints = run.Sources["Shop.Reservation.Endpoints.g.cs"];
        var expire = endpoints[endpoints.LastIndexOf("> ExpireAsync(", StringComparison.Ordinal)..];

        Assert.Contains("group.MapPost(\"/{id:guid}/expire\", ExpireAsync)", endpoints);
        Assert.Contains("entity.ExpiresAt = global::Haldus.HaldusTime.UtcNow(timeProvider);", expire);
        Assert.Contains("global::Haldus.HaldusETag.ReadIfMatch(httpContext)", expire);
    }

    [Fact]
    public void Expiring_Sliding_RestartsTheLifetimeOnEveryWrite()
    {
        var run = Run("""
            var reservations = app.MapCrud<Reservation>().Workflow<InvoiceStatus>().Expiring(TimeSpan.FromDays(7), sliding: true);
            reservations.MapTransition("issue", InvoiceStatus.Draft, InvoiceStatus.Issued);
            """).AssertCompiles();

        // PUT, PATCH ve geçiş.
        Assert.Equal(
            3,
            CountOccurrences(
                run.Sources["Shop.Reservation.Endpoints.g.cs"],
                "entity.ExpiresAt = global::Haldus.HaldusTime.UtcNow(timeProvider) + Lifetime;"));
    }

    [Theory]
    [InlineData("TimeSpan.FromSeconds(90)", 900_000_000L)]
    [InlineData("TimeSpan.FromHours(1.5)", 54_000_000_000L)]
    [InlineData("TimeSpan.FromDays(1, hours: 12)", 1_296_000_000_000L)]
    [InlineData("System.TimeSpan.FromMilliseconds(250)", 2_500_000L)]
    public void Expiring_ReadsConstantLifetimesAtCompileTime(string lifetime, long ticks)
    {
        var run = Run($"app.MapCrud<Reservation>().Expiring({lifetime});").AssertCompiles();

        Assert.Contains($"global::System.TimeSpan.FromTicks({ticks}L);", run.Sources["Shop.Reservation.Endpoints.g.cs"]);
    }

    [Fact]
    public void Expiring_WithoutALifetime_TakesTheExpiryFromTheClient()
    {
        var run = Run("app.MapCrud<Reservation>().Expiring();").AssertCompiles();

        Assert.Contains("public global::System.DateTimeOffset? ExpiresAt { get; set; }", run.Sources["Shop.Reservation.Entity.g.cs"]);

        // Yanıt, oluşturma, tam değiştirme ve yama.
        var dtos = run.Sources["Shop.Reservation.Dtos.g.cs"];
        Assert.Equal(4, CountOccurrences(dtos, " ExpiresAt"));

        var endpoints = run.Sources["Shop.Reservation.Endpoints.g.cs"];
        Assert.Contains("ExpiresAt = request.ExpiresAt", endpoints);
        Assert.DoesNotContain("Lifetime", endpoints);
        Assert.Contains("group.MapPost(\"/{id:guid}/expire\", ExpireAsync)", endpoints);
        Assert.Contains("global::Haldus.HaldusFilters.Expiry", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Idempotent_ProtectsCreationAndTransitionsAfterValidation()
    {
        var run = Run("""
            var invoices = app.MapCrud<Invoice>().Workflow<InvoiceStatus>().Idempotent();
            invoices.MapTransition("issue", InvoiceStatus.Draft, InvoiceStatus.Issued);
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Invoice.Endpoints.g.cs"];

        // Filtre doğrulamadan sonra eklenir; geçersiz istek veritabanına dokunmaz.
        Assert.Contains(
            ".AddEndpointFilter<global::Haldus.HaldusValidationFilter<CreateInvoiceRequest>>()\n"
            + "            .ProducesValidationProblem()\n"
            + "            .AddEndpointFilter(new global::Haldus.HaldusIdempotencyFilter<Invoice>(typeof(CreateInvoiceRequest), required: false))",
            endpoints);
        Assert.Contains(".AddEndpointFilter(new global::Haldus.HaldusIdempotencyFilter<Invoice>(null, required: false))", endpoints);

        // Güncelleme ve silme zaten tekrara dayanıklıdır.
        Assert.Equal(2, CountOccurrences(endpoints, "HaldusIdempotencyFilter"));
        Assert.Contains("global::Haldus.HaldusIdempotency.Configure(modelBuilder);", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Idempotent_CanRequireTheKey()
    {
        var run = Run("app.MapAppendOnly<Invoice>().Idempotent(required: true);").AssertCompiles();

        Assert.Contains("typeof(CreateInvoiceRequest), required: true))", run.Sources["Shop.Invoice.Endpoints.g.cs"]);
    }

    [Fact]
    public void WithoutLifecycleTraits_NoneOfTheirTablesEnterTheModel()
    {
        var run = Run("app.MapCrud<Invoice>();").AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.DoesNotContain("HaldusNumbering", module);
        Assert.DoesNotContain("HaldusIdempotency", module);
        Assert.DoesNotContain("Expiry", module);
    }

    [Theory]
    [InlineData("app.MapCrud<Invoice>().Numbered(\"INV-{year}-{seq}\");", "HALDUS010", "unknown part '{year}'")]
    [InlineData("app.MapCrud<Invoice>().Numbered(\"INV-{yyyy}\");", "HALDUS010", "must contain {seq} exactly once")]
    [InlineData("app.MapCrud<Invoice>().Numbered(\"{seq}-{seq}\");", "HALDUS010", "must contain {seq} exactly once")]
    [InlineData("app.MapCrud<Invoice>().Numbered(\"INV-{seq\");", "HALDUS010", "unbalanced brace")]
    [InlineData("var pattern = \"{seq}\"; app.MapCrud<Invoice>().Numbered(pattern);", "HALDUS010", "arguments of Numbered must be constants")]
    [InlineData("app.MapCrud<Invoice>().Numbered(assignOn: InvoiceStatus.Issued);", "HALDUS007", "Numbered(assignOn: ...) needs a state machine")]
    [InlineData("app.MapCrud<Invoice>().Workflow<InvoiceStatus>().Numbered(assignOn: OtherStatus.Two);", "HALDUS010", "'Two' is not a state of 'Invoice'")]
    [InlineData("app.MapCrud<Invoice>().Workflow<InvoiceStatus>().Numbered(assignOn: InvoiceStatus.Issued);", "HALDUS007", "never takes effect on 'Invoice': no transition leads to 'Issued'")]
    [InlineData("app.MapReadOnly<Invoice>().Numbered();", "HALDUS007", "Numbered cannot be combined with MapReadOnly")]
    [InlineData("app.MapStore<Settings>().Numbered();", "HALDUS007", "Numbered cannot be combined with MapStore")]
    [InlineData("app.MapCrud<Reservation>().Expiring(Configured);", "HALDUS010", "must be readable at compile time")]
    [InlineData("app.MapCrud<Reservation>().Expiring(TimeSpan.Zero);", "HALDUS010", "must be readable at compile time")]
    [InlineData("app.MapCrud<Reservation>().Expiring(TimeSpan.FromMinutes(0));", "HALDUS010", "must be longer than zero")]
    [InlineData("app.MapSingleton<Settings>().Expiring();", "HALDUS007", "Expiring cannot be combined with MapSingleton")]
    [InlineData("app.MapStore<Settings>().Idempotent();", "HALDUS007", "Idempotent cannot be combined with MapStore")]
    [InlineData("app.MapReadOnly<Invoice>().Idempotent();", "HALDUS007", "Idempotent has no effect on 'Invoice'")]
    [InlineData("app.MapCrud<Reservation>().Workflow<InvoiceStatus>().Expiring().MapTransition(\"expire\", InvoiceStatus.Draft, InvoiceStatus.Paid);", "HALDUS007", "collides with the 'expire' endpoint that Expiring generates")]
    public void InvalidLifecycleDeclarations_AreReported(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void LifecycleDeclarationsOnAVariable_ReportHaldus004()
    {
        var run = Run("""
            var invoices = app.MapCrud<Invoice>();
            invoices.Numbered();
            invoices.Expiring();
            invoices.Idempotent();
            """);

        Assert.Equal(["HALDUS004", "HALDUS004", "HALDUS004"], run.GeneratorDiagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public void LifecycleTraitsSnapshot()
    {
        var run = Run("""
            var invoices = app.MapCrud<Invoice>()
                .MultiTenant()
                .Workflow<InvoiceStatus>()
                .Numbered("INV-{yyyy}-{seq:6}", assignOn: InvoiceStatus.Issued)
                .Idempotent();

            invoices.MapTransition("issue", InvoiceStatus.Draft, InvoiceStatus.Issued);

            app.MapAppendOnly<Reservation>()
                .Expiring(TimeSpan.FromMinutes(15), sliding: true)
                .Numbered("R{seq:8}", gapless: false)
                .Idempotent(required: true);
            """).AssertCompiles();

        Snapshot.Verify(run);
    }

    private static string Section(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = from < 0 ? -1 : source.IndexOf(end, from, StringComparison.Ordinal);

        Assert.True(from >= 0 && to > from, $"Could not locate the section between '{start}' and '{end}'.");
        return source[from..to];
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
