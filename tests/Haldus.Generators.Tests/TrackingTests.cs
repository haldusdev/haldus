namespace Haldus.Generators.Tests;

/// <summary>Saklama süresi, okundu takibi ve başka kaynaklardan toplanan alanlar.</summary>
public sealed class TrackingTests
{
    private const string Entities = """
        namespace Shop
        {
            public partial class Notification
            {
                public string Title { get; set; } = "";
            }

            public partial class Discussion
            {
                public string Title { get; set; } = "";
            }

            public partial class Reply
            {
                public string Text { get; set; } = "";
                public int Score { get; set; }
                public decimal? Weight { get; set; }
            }

            public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
        }
        """;

    // Hesaplama, toplanan alanı okur; alan yalnızca onu bildiren senaryolarda üretilir.
    private const string Bills = """
        namespace Shop
        {
            public partial class Bill : IComputed
            {
                public decimal Amount { get; set; }
                public bool Paid { get; private set; }
                public void Compute() => Paid = PaidAmount >= Amount;
            }

            public partial class BillPayment
            {
                public decimal Amount { get; set; }
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
    public void Retention_RegistersThePeriodAndKeepsTheCreationTime()
    {
        var run = Run("app.MapAppendOnly<Notification>().Retention(TimeSpan.FromDays(90));").AssertCompiles();

        // Kaydın yaşı oluşturulma zamanından ölçülür.
        Assert.Contains("global::Haldus.IAuditable", run.Sources["Shop.Notification.Entity.g.cs"]);
        Assert.Contains(
            "global::Haldus.HaldusMaintenanceRegistry.RegisterRetention<global::Shop.Notification>(global::System.TimeSpan.FromTicks(77760000000000L));",
            run.Sources["HaldusModule.g.cs"]);

        Assert.DoesNotContain("RegisterRetention", Run("app.MapAppendOnly<Notification>();").AssertCompiles().Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void ReadTracking_AddsTheReadEndpointsToAnOwnedResource()
    {
        var run = Run("app.MapReadOnly<Notification>().OwnedByCurrentUser().ReadTracking();").AssertCompiles();

        Assert.Contains("global::Haldus.IReadTracked", run.Sources["Shop.Notification.Entity.g.cs"]);
        Assert.Contains("public global::System.DateTimeOffset? ReadAt { get; set; }", run.Sources["Shop.Notification.Entity.g.cs"]);
        Assert.Contains("public required global::System.DateTimeOffset? ReadAt { get; init; }", run.Sources["Shop.Notification.Dtos.g.cs"]);

        var endpoints = run.Sources["Shop.Notification.Endpoints.g.cs"];
        Assert.Contains("""MapPost("/{id:guid}/read", MarkReadAsync)""", endpoints);
        Assert.Contains("""MapPost("/{id:guid}/unread", MarkUnreadAsync)""", endpoints);
        Assert.Contains("""MapPost("/read-all", ReadAllAsync)""", endpoints);
        Assert.Contains("""MapGet("/unread-count", UnreadCountAsync)""", endpoints);

        // İlk okunma zamanı korunur; okunmamış sayılan kaydın zamanı silinir.
        Assert.Contains("entity.ReadAt ??= global::Haldus.HaldusTime.UtcNow(timeProvider);", Handler(endpoints, "MarkReadAsync"));
        Assert.Contains("entity.ReadAt = null;", Handler(endpoints, "MarkUnreadAsync"));

        // Toplu işaretleme ve sayım kaynağın filtrelerinden geçer: yalnızca isteği yapanın kayıtları.
        var readAll = Handler(endpoints, "ReadAllAsync");
        Assert.DoesNotContain("IgnoreQueryFilters", readAll);
        Assert.Contains(".Where(item => item.ReadAt == null)", readAll);
        Assert.DoesNotContain("IgnoreQueryFilters", Handler(endpoints, "UnreadCountAsync"));

        // Liste okunmuşluğa göre süzülür.
        var list = Handler(endpoints, "ListAsync");
        Assert.Contains("bool? unread,", list);
        Assert.Contains("query = query.Where(item => (item.ReadAt == null) == unreadOnly);", list);
    }

    [Fact]
    public void ReadReceipts_KeepTheReadMarkOutsideTheSharedRecord()
    {
        var run = Run("app.MapCrud<Discussion>().ConcurrencyChecked().Archivable().ReadReceipts();").AssertCompiles();

        // Okundu bilgisi kaydın bir alanı değildir; işaretler kendi tablosundadır.
        Assert.DoesNotContain("ReadAt", run.Sources["Shop.Discussion.Entity.g.cs"]);
        Assert.DoesNotContain("ReadAt", run.Sources["Shop.Discussion.Dtos.g.cs"]);
        Assert.Contains("global::Haldus.HaldusReadReceipts.Configure(modelBuilder);", run.Sources["HaldusModule.g.cs"]);
        Assert.DoesNotContain("HaldusReadReceipts", Run("app.MapCrud<Discussion>();").AssertCompiles().Sources["HaldusModule.g.cs"]);

        var endpoints = run.Sources["Shop.Discussion.Endpoints.g.cs"];

        // Bütün uçlar kullanıcısı çözülemeyen isteği reddeder.
        foreach (var map in new[]
        {
            """MapPost("/{id:guid}/read", MarkReadAsync)""",
            """MapPost("/{id:guid}/unread", MarkUnreadAsync)""",
            """MapGet("/{id:guid}/read-state", ReadStateAsync)""",
            """MapPost("/read-all", ReadAllAsync)""",
            """MapGet("/unread-count", UnreadCountAsync)""",
        })
        {
            var mapped = endpoints.Substring(endpoints.IndexOf(map, StringComparison.Ordinal));
            Assert.Contains(".AddEndpointFilter<global::Haldus.HaldusUserRequiredFilter>()", mapped.Substring(0, mapped.IndexOf(";", StringComparison.Ordinal)));
        }

        // Kayıt, isteği yapana görünür olarak aranır; kendisi yazılmadığı için sürümü sorulmaz.
        foreach (var name in new[] { "MarkReadAsync", "MarkUnreadAsync", "ReadStateAsync" })
        {
            var handler = Handler(endpoints, name);
            Assert.Contains("AnyAsync(item => item.Id == id, cancellationToken)", handler);
            Assert.Contains("NotFound(messages, \"Discussion\", id)", handler);
            Assert.DoesNotContain("IgnoreQueryFilters", handler);
            Assert.DoesNotContain("ReadIfMatch", handler);
            Assert.DoesNotContain("SaveChangesAsync", handler);
        }

        Assert.Contains("global::Haldus.HaldusReadReceipts.MarkAsync<Discussion>(context,", Handler(endpoints, "MarkReadAsync"));
        Assert.Contains("global::Haldus.HaldusReadReceipts.UnmarkAsync<Discussion>(context,", Handler(endpoints, "MarkUnreadAsync"));

        // Liste ve sayım isteği yapanın işaretlerine bakar; arşivlenen kayıt okunmamışlar arasında sayılmaz.
        Assert.Contains("global::Haldus.HaldusReadReceipts.Filter(receipts, query, item => item.Id.ToString(), global::Haldus.HaldusScope.UserId(receipts), unreadOnly);", Handler(endpoints, "ListAsync"));
        Assert.Contains(".Where(item => item.ArchivedAt == null)", Handler(endpoints, "UnreadCountAsync"));
        Assert.Contains("global::Haldus.HaldusReadReceipts.MarkAllAsync(", Handler(endpoints, "ReadAllAsync"));
    }

    [Fact]
    public void ReadTracking_OfAVersionedRecord_ChecksTheVersion()
    {
        var run = Run("app.MapCrud<Notification>().OwnedByCurrentUser().ConcurrencyChecked().ReadTracking();").AssertCompiles();
        var endpoints = run.Sources["Shop.Notification.Endpoints.g.cs"];

        Assert.Contains("global::Haldus.HaldusETag.ReadIfMatch(httpContext)", Handler(endpoints, "MarkReadAsync"));

        // Okundu zamanı istekle yazılamaz.
        var dtos = run.Sources["Shop.Notification.Dtos.g.cs"];
        Assert.DoesNotContain("ReadAt", dtos.Substring(dtos.IndexOf("record CreateNotificationRequest", StringComparison.Ordinal)));
    }

    [Fact]
    public void Rollups_AddFieldsThatAreRecomputedFromTheChildren()
    {
        var run = Run("""
            app.MapCrud<Discussion>().Counts<Reply>().Sums<Reply>(reply => reply.Score, into: "Score").Sortable(discussion => discussion.ReplyCount);
            app.MapCrud<Reply>().Under<Discussion>().SoftDeletable();
            """).AssertCompiles();

        var entity = run.Sources["Shop.Discussion.Entity.g.cs"];
        Assert.Contains("public int ReplyCount { get; set; }", entity);
        Assert.Contains("public int Score { get; set; }", entity);

        // Alanlar yanıtta görünür, isteklerde yer almaz.
        var dtos = run.Sources["Shop.Discussion.Dtos.g.cs"];
        Assert.Contains("public required int ReplyCount { get; init; }", dtos);
        var requests = dtos.Substring(dtos.IndexOf("record CreateDiscussionRequest", StringComparison.Ordinal));
        Assert.DoesNotContain("ReplyCount", requests);
        Assert.DoesNotContain("Score", requests);

        // Bir alt kaynağın bütün alanları tek bir kayıtla, tek bir güncellemede yazılır.
        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusRollups.Register<global::Shop.Discussion, global::Shop.Reply>(", module);
        Assert.Contains("\"DiscussionId\",\n            [\"Score\"],", module);
        Assert.Contains("global::Haldus.HaldusRollups.Children<global::Shop.Reply>(context).Where(child => child.DiscussionId == id);", module);
        Assert.Contains("var value0 = await children.CountAsync(cancellationToken);", module);
        Assert.Contains("var value1 = await children.SumAsync(child => child.Score, cancellationToken);", module);
        Assert.Contains("setters => setters.SetProperty(parent => parent.ReplyCount, value0).SetProperty(parent => parent.Score, value1),", module);
        Assert.Contains("FinishAsync<global::Shop.Discussion>(context, key, null, cancellationToken);", module);

        // Üretilen uç kodunda sayım yoktur: kural, alt kaydı hangi kod yazarsa yazsın kaydetmeye bağlıdır.
        Assert.DoesNotContain("HaldusRollups", run.Sources["Shop.Reply.Endpoints.g.cs"]);
        Assert.DoesNotContain("HaldusRollups", run.Sources["Shop.Discussion.Endpoints.g.cs"]);

        // Toplanan alana göre sıralanabilir.
        Assert.Contains("\"replyCount\"", run.Sources["Shop.Discussion.Endpoints.g.cs"]);
    }

    [Fact]
    public void Rollups_OfAComputedParent_RecomputeItAfterwards()
    {
        var run = Run("""
            app.MapCrud<Bill>().Sums<BillPayment>(payment => payment.Amount, into: "PaidAmount");
            app.MapAppendOnly<BillPayment>().References<Bill>();
            """, Bills).AssertCompiles();

        Assert.Contains("public decimal PaidAmount { get; set; }", run.Sources["Shop.Bill.Entity.g.cs"]);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusRollups.Register<global::Shop.Bill, global::Shop.BillPayment>(", module);
        Assert.Contains(
            "FinishAsync<global::Shop.Bill>(context, key, global::Haldus.HaldusRollups.Parents<global::Shop.Bill>(context).Where(parent => parent.Id == id), cancellationToken);",
            module);
    }

    [Fact]
    public void Rollups_UseTheDefaultNames()
    {
        var run = Run("""
            app.MapCrud<Discussion>().Sums<Reply>(reply => reply.Score);
            app.MapCrud<Reply>().References<Discussion>(required: false);
            """).AssertCompiles();

        Assert.Contains("public int ReplyScoreSum { get; set; }", run.Sources["Shop.Discussion.Entity.g.cs"]);

        // İsteğe bağlı bir referansın boş olduğu kayıt hiçbir üst kayda sayılmaz; karşılaştırma boş değeri eşleştirmez.
        Assert.Contains("Where(child => child.DiscussionId == id)", run.Sources["HaldusModule.g.cs"]);
    }

    [Theory]
    [InlineData("var period = TimeSpan.FromDays(1); app.MapAppendOnly<Notification>().Retention(period);", "HALDUS010", "must be readable at compile time")]
    [InlineData("app.MapAppendOnly<Notification>().Retention(TimeSpan.Zero);", "HALDUS010", "longer than zero")]
    [InlineData("app.MapStore<Notification>().Retention(TimeSpan.FromDays(1));", "HALDUS007", "Retention")]
    [InlineData("app.MapFile<Notification>().Retention(TimeSpan.FromDays(1));", "HALDUS007", "MapFile cannot be combined with Retention")]
    [InlineData("app.MapCrud<Notification>().Publishable().Retention(TimeSpan.FromDays(1));", "HALDUS007", "Publishable cannot be combined with Retention")]
    [InlineData("app.MapCrud<Notification>().ReadTracking();", "HALDUS007", "ReadTracking needs OwnedByCurrentUser")]
    [InlineData("app.MapSingleton<Notification>().ReadTracking();", "HALDUS007", "ReadTracking")]
    [InlineData("app.MapCrud<Notification>().OwnedByCurrentUser().ReadTracking().ReadReceipts();", "HALDUS007", "ReadReceipts cannot be combined with ReadTracking")]
    [InlineData("app.MapStore<Notification>().ReadReceipts();", "HALDUS007", "ReadReceipts")]
    [InlineData("var discussions = app.MapCrud<Discussion>().ReadReceipts(); discussions.MapAction(\"read-state\", discussion => { });", "HALDUS007", "read-state")]
    [InlineData("var notifications = app.MapCrud<Notification>().OwnedByCurrentUser().ReadTracking(); notifications.MapAction(\"read\", notification => { });", "HALDUS007", "read")]
    [InlineData("app.MapCrud<Discussion>().Counts<Reply>();", "HALDUS009", "Reply")]
    [InlineData("app.MapCrud<Discussion>().Counts<Reply>(); app.MapCrud<Reply>();", "HALDUS007", "needs 'Reply' to refer to 'Discussion'")]
    [InlineData("app.MapCrud<Discussion>().Counts<Reply>(); app.MapCrud<Reply>().References<Discussion>().MultiTenant();", "HALDUS007", "would add up the records of every tenant")]
    [InlineData("app.MapCrud<Discussion>().Counts<Reply>(); app.MapCrud<Reply>().Under<Discussion>().OwnedByCurrentUser(underMe: false);", "HALDUS007", "pass 'acrossOwners: true' if that is intended")]
    [InlineData("app.MapCrud<Discussion>().Sums<Reply>(reply => reply.Score); app.MapCrud<Reply>().References<Discussion>().OwnedByCurrentUser();", "HALDUS007", "pass 'acrossOwners: true' if that is intended")]
    [InlineData("app.MapCrud<Discussion>().OwnedByCurrentUser(); app.MapCrud<Notification>().Counts<Reply>(); app.MapCrud<Reply>().Under<Discussion>().References<Notification>();", "HALDUS007", "pass 'acrossOwners: true' if that is intended")]
    [InlineData("var open = true; app.MapCrud<Discussion>().Counts<Reply>(acrossOwners: open); app.MapCrud<Reply>().References<Discussion>();", "HALDUS010", "must be readable at compile time")]
    [InlineData("app.MapCrud<Discussion>().Sums<Reply>(reply => reply.Text); app.MapCrud<Reply>().References<Discussion>();", "HALDUS010", "needs a non-nullable decimal, int, long or double property")]
    [InlineData("app.MapCrud<Discussion>().Sums<Reply>(reply => reply.Weight); app.MapCrud<Reply>().References<Discussion>();", "HALDUS010", "needs a non-nullable decimal, int, long or double property")]
    [InlineData("app.MapCrud<Discussion>().Counts<Reply>(into: \"Total\").Sums<Reply>(reply => reply.Score, into: \"Total\"); app.MapCrud<Reply>().References<Discussion>();", "HALDUS010", "'Total' cannot name the field")]
    [InlineData("app.MapCrud<Discussion>().Publishable().Counts<Reply>(); app.MapCrud<Reply>().References<Discussion>();", "HALDUS007", "Publishable cannot be combined with Counts")]
    [InlineData("app.MapStore<Discussion>().Counts<Reply>(); app.MapCrud<Reply>();", "HALDUS007", "Counts")]
    public void Declarations_ThatCannotWork_AreRejected(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void Rollups_OfTenantRecords_CompileWhenTheParentBelongsToTheTenantToo()
    {
        Run("""
            app.MapCrud<Discussion>().MultiTenant().Counts<Reply>();
            app.MapCrud<Reply>().Under<Discussion>();
            """).AssertCompiles();

        // Kullanıcıya ait kayıtlar herkesin gördüğü bir kayıtta ancak açıkça istenirse sayılır (beğeni sayısı gibi).
        Run("""
            app.MapCrud<Discussion>().Counts<Reply>(acrossOwners: true).Sums<Reply>(reply => reply.Score, "Score", acrossOwners: true);
            app.MapCrud<Reply>().Under<Discussion>().OwnedByCurrentUser(underMe: false);
            """).AssertCompiles();

        // Üst kayıt da aynı kullanıcıya aitse açılan bir şey yoktur: kaydı yalnızca sahibi görür.
        Run("""
            app.MapCrud<Discussion>().OwnedByCurrentUser().Counts<Reply>();
            app.MapCrud<Reply>().Under<Discussion>();
            """).AssertCompiles();
    }

    [Fact]
    public void TrackingSnapshot()
    {
        var run = Run("""
            app.MapReadOnly<Notification>().OwnedByCurrentUser().ReadTracking().Retention(TimeSpan.FromDays(90));
            app.MapCrud<Bill>().Sums<BillPayment>(payment => payment.Amount, into: "PaidAmount").Counts<BillPayment>();
            app.MapAppendOnly<BillPayment>().References<Bill>();
            """, Bills).AssertCompiles();

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
