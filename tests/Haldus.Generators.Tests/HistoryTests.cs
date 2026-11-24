namespace Haldus.Generators.Tests;

/// <summary>
/// Zaman ve sürüm: saklanan sürümler (<c>Revisioned</c>), değişiklik akışı (<c>ChangeFeed</c>), dönemli kayıtlar
/// (<c>EffectiveDated</c>) ve taslak / yayın (<c>Publishable</c>).
/// </summary>
public sealed class HistoryTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum PostStatus { Draft, Live }

            public partial class Category
            {
                public string Name { get; set; } = "";
            }

            public partial class Post
            {
                public string Title { get; set; } = "";
                public string Slug { get; set; } = "";
            }

            public partial class Rate
            {
                public string Currency { get; set; } = "";
                public decimal Price { get; set; }
            }

            public partial class Counter
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
        }
        """;

    /// <summary>Satırları olan kaynak; yalnızca satırları eşleyen senaryolara verilir.</summary>
    private const string Articles = """
        namespace Shop
        {
            public partial class Article
            {
                public string Title { get; set; } = "";
            }

            public partial class Section
            {
                public string Heading { get; set; } = "";
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

    // ── Sürümler ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Revisioned_ListsReadsAndRestoresRevisions()
    {
        var run = Run("app.MapCrud<Post>().References<Category>(required: false).Revisioned(); app.MapCrud<Category>();").AssertCompiles();

        Assert.Contains("partial class Post : global::Haldus.IRevisioned", run.Sources["Shop.Post.Entity.g.cs"]);

        var endpoints = run.Sources["Shop.Post.Endpoints.g.cs"];
        Assert.Contains("""MapGet("/{id:guid}/revisions", ListRevisionsAsync)""", endpoints);
        Assert.Contains("""MapGet("/{id:guid}/revisions/{revisionId:long}", GetRevisionAsync)""", endpoints);
        Assert.Contains("""MapPost("/{id:guid}/revisions/{revisionId:long}/rollback", RollbackAsync)""", endpoints);

        // Sürüm, kaydın kendi yanıt biçiminde döner: görüntüden kurulan kayıt her yanıt gibi ToResponse'tan geçer.
        Assert.Contains("TypedResults.Ok(ToResponse(revision))", Handler(endpoints, "GetRevisionAsync"));

        // Geri yükleme bir güncellemedir: alanlar geri yazılır, geri gelen referanslar yeniden doğrulanır.
        var rollback = Handler(endpoints, "RollbackAsync");
        Assert.Contains("entity.Title = revision.Title;", rollback);
        Assert.Contains("entity.CategoryId = revision.CategoryId;", rollback);
        Assert.Contains("ReferenceNotFound(messages, \"categoryId\", \"Category\"", rollback);

        // Anlık görüntüyü uçlar almaz; kaydı hangi kod yazarsa yazsın interceptor alır.
        Assert.DoesNotContain("HaldusRevisions.Capture", endpoints);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusRevisions.Configure(modelBuilder);", module);
        Assert.Contains("global::Haldus.HaldusRevisions.Register<global::Shop.Post>();", module);
        Assert.DoesNotContain("HaldusRevisions", Run("app.MapCrud<Post>();").AssertCompiles().Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Revisioned_SnapshotsAndRestoresEmbeddedRows()
    {
        var run = Run(
            """
            var articles = app.MapCrud<Article>().Revisioned().ConcurrencyChecked();
            articles.MapDetail<Section>();
            """,
            Articles).AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusRevisions.Register<global::Shop.Article>(\"Sections\");", module);

        // Satırı değişen sahip de değişmiş sayılır; satırları hangi kod değiştirirse değiştirsin.
        Assert.Contains("global::Haldus.HaldusDetails.Register<global::Shop.Article, global::Shop.Section>(\"ArticleId\");", module);

        var rollback = Handler(run.Sources["Shop.Article.Endpoints.g.cs"], "RollbackAsync");
        Assert.Contains("foreach (var source in revision.Sections.OrderBy(row => row.Position).ThenBy(row => row.Id))", rollback);
        Assert.Contains("row = new global::Shop.Section { Id = source.Id, ArticleId = entity.Id };", rollback);
        Assert.Contains("row.Heading = source.Heading;", rollback);
        Assert.Contains("context.Remove(removed);", rollback);

        // Geri yükleme de sürüm ister.
        Assert.Contains("PreconditionRequired(messages)", rollback);
    }

    [Theory]
    [InlineData("app.MapCrud<Counter>().Revisioned();", "Revisioned needs a key that Haldus assigns before saving")]
    [InlineData("app.MapCrud<Post>().Revisioned().SecretOnce(prefix: \"sk\");", "Revisioned cannot be combined with SecretOnce or TokenAccess")]
    [InlineData("app.MapAppendOnly<Post>().Revisioned();", "Revisioned cannot be combined with MapAppendOnly")]
    [InlineData(
        """
        var posts = app.MapCrud<Post>().Revisioned();
        posts.MapAction("revisions", post => post.Title = "");
        """,
        "collides with the 'revisions' endpoint")]
    public void Revisioned_RejectsDeclarationsThatCannotWork(string body, string expectedMessage)
    {
        var diagnostic = Assert.Single(Run(body).GeneratorDiagnostics, candidate => candidate.Id == "HALDUS007");
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void Revisioned_WithoutUpdate_KeepsHistoryButCannotRestore()
    {
        var run = Run("app.MapCrud<Post>().Without(CrudOperation.Update).Revisioned();").AssertCompiles();

        var endpoints = run.Sources["Shop.Post.Endpoints.g.cs"];
        Assert.Contains("ListRevisionsAsync", endpoints);
        Assert.DoesNotContain("RollbackAsync", endpoints);
    }

    // ── Değişiklik akışı ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChangeFeed_ReadsTheLogAndLoadsWhatTheCallerCanSee()
    {
        var run = Run("app.MapCrud<Post>().MultiTenant().ChangeFeed();").AssertCompiles();

        Assert.Contains("global::Haldus.IChangeTracked", run.Sources["Shop.Post.Entity.g.cs"]);

        var endpoints = run.Sources["Shop.Post.Endpoints.g.cs"];
        Assert.Contains("""MapGet("/changes", ChangesAsync)""", endpoints);

        var changes = Handler(endpoints, "ChangesAsync");
        Assert.Contains("global::Haldus.HaldusChanges.ReadAsync<Post>(context, since, pageSize, null, cancellationToken)", changes);

        // Değişen kayıtlar kaynağın kendi filtrelerinden geçerek yüklenir; bulunamayan silinmiş sayılır.
        Assert.Contains(".Where(item => ids.Contains(item.Id))", changes);
        Assert.DoesNotContain("IgnoreQueryFilters", changes);
        Assert.Contains("Deleted: true, null, item.ChangedAt", changes);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusChanges.Configure(modelBuilder);", module);
        Assert.Contains("global::Haldus.HaldusChanges.Register<global::Shop.Post>(null);", module);
    }

    [Fact]
    public void ChangeFeed_UnderAParent_ChecksTheParentAndFiltersTheLogByIt()
    {
        var run = Run("app.MapCrud<Category>(); app.MapCrud<Post>().Under<Category>().ChangeFeed();").AssertCompiles();

        var changes = Handler(run.Sources["Shop.Post.Endpoints.g.cs"], "ChangesAsync");

        // Ebeveyn görünür değilse akış da yoktur; aksi halde silinen kayıtların kimlikleri sızardı.
        Assert.Contains("NotFound(messages, \"Category\", categoryId)", changes);
        Assert.Contains("global::System.Convert.ToString(categoryId, global::System.Globalization.CultureInfo.InvariantCulture)", changes);
        Assert.Contains("&& item.CategoryId == categoryId", changes);
        Assert.Contains("HaldusChanges.Register<global::Shop.Post>(\"CategoryId\");", run.Sources["HaldusModule.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Counter>().ChangeFeed();", "ChangeFeed needs a key that Haldus assigns before saving")]
    [InlineData("app.MapCrud<Category>(); app.MapCrud<Post>().Under<Category>(shallow: true).ChangeFeed();", "ChangeFeed cannot be combined with Under<T>(shallow: true)")]
    [InlineData("app.MapStore<Post>().ChangeFeed();", "ChangeFeed cannot be combined with MapStore")]
    public void ChangeFeed_RejectsDeclarationsThatCannotWork(string body, string expectedMessage)
    {
        var diagnostic = Assert.Single(Run(body).GeneratorDiagnostics, candidate => candidate.Id == "HALDUS007");
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    // ── Dönemli kayıtlar ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EffectiveDated_AddsThePeriodAndGuardsEverySave()
    {
        var run = Run("app.MapCrud<Category>(); app.MapCrud<Rate>().Under<Category>().EffectiveDated(rate => rate.Currency).Batch();").AssertCompiles();

        var entity = run.Sources["Shop.Rate.Entity.g.cs"];
        Assert.Contains("partial class Rate : global::Haldus.IEffectiveDated", entity);
        Assert.Contains("public global::System.DateOnly EffectiveFrom { get; set; }", entity);
        Assert.Contains("public global::System.DateOnly? EffectiveTo { get; set; }", entity);

        // Dönem istemcinin yazdığı sıradan bir alandır.
        var dtos = run.Sources["Shop.Rate.Dtos.g.cs"];
        Assert.Contains("EffectiveFrom", dtos.Substring(dtos.IndexOf("record CreateRateRequest", StringComparison.Ordinal)));

        var endpoints = run.Sources["Shop.Rate.Endpoints.g.cs"];

        // Kural kaydetmenin kendisine bağlıdır: kaydı yazan her yol kilidin altında aynı sınamadan geçer.
        foreach (var method in new[] { "CreateAsync", "UpdateAsync", "PatchAsync", "BatchCreateItemAsync", "BatchUpdateItemAsync" })
        {
            var handler = Handler(endpoints, method);

            Assert.Contains("await using var period = await global::Haldus.HaldusOrdering.BeginAsync(context, \"Rates\", cancellationToken, categoryId);", handler);
            Assert.Contains(".Where(item => item.CategoryId == categoryId).Where(item => item.Currency == entity.Currency), entity, cancellationToken)", handler);
            Assert.Contains("EffectivePeriodInvalid(messages)", handler);
            Assert.Contains("EffectivePeriodOverlap(messages, \"Rate\")", handler);
            Assert.Contains("await period.CommitAsync(cancellationToken);", handler);
        }

        // Liste, verilen gün geçerli olan kayıtlara süzülür; dönem yarı açıktır.
        var list = Handler(endpoints, "ListAsync");
        Assert.Contains("global::System.DateOnly? asOf,", list);
        Assert.Contains("item.EffectiveFrom <= day && (item.EffectiveTo == null || item.EffectiveTo > day)", list);

        Assert.Contains("entity.HasIndex(item => item.EffectiveFrom);", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void EffectiveDated_WithoutFields_KeepsOneSeriesPerScope()
    {
        var run = Run("app.MapCrud<Rate>().EffectiveDated();").AssertCompiles();

        Assert.Contains(
            "ApplyAsync(context.Set<Rate>(), entity, cancellationToken)",
            Handler(run.Sources["Shop.Rate.Endpoints.g.cs"], "CreateAsync"));
    }

    [Theory]
    [InlineData("app.MapCrud<Rate>().EffectiveDated(rate => rate.Missing);", "HALDUS010", "cannot separate the periods of 'Rate'")]
    [InlineData("app.MapAppendOnly<Rate>().EffectiveDated();", "HALDUS007", "EffectiveDated cannot be combined with MapAppendOnly")]
    public void EffectiveDated_RejectsDeclarationsThatCannotWork(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    // ── Taslak ve yayın ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Publishable_KeepsThePublishedCopyOutOfEveryOrdinaryQuery()
    {
        var run = Run("app.MapCrud<Post>().Publishable().NaturalKey(post => post.Slug).SoftDeletable().ConcurrencyChecked().Revisioned();").AssertCompiles();

        var entity = run.Sources["Shop.Post.Entity.g.cs"];
        Assert.Contains("public global::System.Guid? PublishedOfId { get; set; }", entity);
        Assert.Contains("public global::System.DateTimeOffset? PublishedAt { get; set; }", entity);

        // Kopyaya işaret eden alan dışarıya çıkmaz; yayın zamanı çıkar.
        var dtos = run.Sources["Shop.Post.Dtos.g.cs"];
        Assert.DoesNotContain("PublishedOfId", dtos);
        Assert.Contains("PublishedAt", Between(dtos, "record PostResponse", "record CreatePostRequest"));
        Assert.DoesNotContain("PublishedAt", dtos.Substring(dtos.IndexOf("record CreatePostRequest", StringComparison.Ordinal)));

        // Kopyaları gizleyen filtre modeldedir; doğal anahtar taslaklar arasında benzersizdir.
        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.HasQueryFilter(global::Haldus.HaldusFilters.Publication, item => item.PublishedOfId == null);", module);
        Assert.Contains("HasForeignKey(item => item.PublishedOfId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)", module);
        Assert.Contains("entity.HasIndex(item => item.PublishedOfId).IsUnique().HasDatabaseName(\"UX_Posts_PublishedOfId\");", module);
        Assert.Contains("HasDatabaseName(\"UX_Posts_Slug\").HasFilter(\"\\\"PublishedOfId\\\" IS NULL\");", module);

        // Filtreyi yalnızca yayın uçları, yalnızca adıyla kaldırır.
        var endpoints = run.Sources["Shop.Post.Endpoints.g.cs"];
        Assert.DoesNotContain("IgnoreQueryFilters()", endpoints);

        foreach (var method in new[] { "ListAsync", "GetAsync", "UpdateAsync", "DeleteAsync", "GetBySlugAsync", "RollbackAsync" })
        {
            Assert.DoesNotContain("HaldusFilters.Publication", Handler(endpoints, method));
        }

        foreach (var method in new[] { "ListPublishedAsync", "GetPublishedAsync", "PublishAsync", "UnpublishAsync" })
        {
            Assert.Contains("IgnoreQueryFilters(new[] { global::Haldus.HaldusFilters.Publication })", Handler(endpoints, method));
        }
    }

    [Fact]
    public void Publishable_PublishesTheDraftAsACopyShownUnderItsOwnId()
    {
        var run = Run("app.MapCrud<Post>().Publishable().ConcurrencyChecked();").AssertCompiles();
        var endpoints = run.Sources["Shop.Post.Endpoints.g.cs"];

        Assert.Contains("""MapGet("/published", ListPublishedAsync)""", endpoints);
        Assert.Contains("""MapGet("/published/{id:guid}", GetPublishedAsync)""", endpoints);
        Assert.Contains("""MapPost("/{id:guid}/publish", PublishAsync)""", endpoints);
        Assert.Contains("""MapPost("/{id:guid}/unpublish", UnpublishAsync)""", endpoints);

        var publish = Handler(endpoints, "PublishAsync");
        Assert.Contains("PreconditionRequired(messages)", publish);
        Assert.Contains("context.Set<Post>().Remove(previous);", publish);
        Assert.Contains("Title = source.Title, Slug = source.Slug, PublishedOfId = source.Id, PublishedAt = now", publish);
        Assert.Contains("source.PublishedAt = now;", publish);
        Assert.Contains("TypedResults.Ok(ToResponse(source))", publish);

        // Kopya dışarıya taslağının kimliğiyle gösterilir.
        Assert.Contains("ToResponse(copy) with { Id = copy.PublishedOfId!.Value }", endpoints);
        Assert.Contains("item.PublishedOfId == id", Handler(endpoints, "GetPublishedAsync"));

        // Yayındaki kayıt silinemez; önce yayından kaldırılır.
        var delete = Handler(endpoints, "DeleteAsync");
        Assert.Contains("entity.PublishedAt is not null", delete);
        Assert.Contains("StateConflict(messages, \"delete\", \"Published\")", delete);
    }

    [Fact]
    public void ScheduledPublishing_LetsMaintenancePublishTheDraftWithTheSameCode()
    {
        var run = Run("app.MapCrud<Post>().Publishable().ScheduledPublishing().ConcurrencyChecked().MultiTenant();").AssertCompiles();
        var endpoints = run.Sources["Shop.Post.Endpoints.g.cs"];

        // Zaman taslağın bir alanıdır; yalnızca zamanlama uçları yazar.
        Assert.Contains("public global::System.DateTimeOffset? ScheduledAt { get; set; }", run.Sources["Shop.Post.Entity.g.cs"]);
        var dtos = run.Sources["Shop.Post.Dtos.g.cs"];
        Assert.Contains("ScheduledAt", dtos.Substring(0, dtos.IndexOf("record CreatePostRequest", StringComparison.Ordinal)));
        Assert.DoesNotContain("ScheduledAt", dtos.Substring(dtos.IndexOf("record CreatePostRequest", StringComparison.Ordinal)));

        Assert.Contains("""MapPost("/{id:guid}/schedule", ScheduleAsync)""", endpoints);
        Assert.Contains("""MapDelete("/{id:guid}/schedule", UnscheduleAsync)""", endpoints);

        // Zamanlamak taslağın bir yazmasıdır: sürümü sorulur.
        foreach (var name in new[] { "ScheduleAsync", "UnscheduleAsync" })
        {
            var handler = Handler(endpoints, name);
            Assert.Contains("global::Haldus.HaldusETag.ReadIfMatch(httpContext)", handler);
            Assert.Contains("entity.ScheduledAt = ", handler);
        }

        // Elle yayın bekleyen zamanlamayı düşürür.
        Assert.Contains("source.ScheduledAt = null;", Handler(endpoints, "PublishAsync"));

        // Bakımın çağırdığı yol kopyayı elle yayınla aynı parçadan kurar ve taslağı kendi kiracısı adına yayınlar.
        var due = Handler(endpoints, "PublishDueAsync");
        Assert.Contains("global::Haldus.HaldusScheduledPublishing.DueAsync<Post>(", due);
        Assert.Contains("global::Haldus.HaldusAmbient.Use(", due);
        Assert.Contains("Title = source.Title, Slug = source.Slug, PublishedOfId = source.Id, PublishedAt = now", due);
        Assert.Contains("context.Set<Post>().Remove(previous);", due);
        Assert.Contains("source.ScheduledAt = null;", due);
        Assert.DoesNotContain("IgnoreQueryFilters()", endpoints);

        // Kendi filtrelerinden geçemeyen taslak hiç yayınlanamaz; zamanlaması düşürülür. Başarısız olanlar atlanarak
        // sıradakilere geçilir: yayınlanamayan birkaç taslak sıranın başını tutup ötekileri (başka kiracıları) bekletemez.
        Assert.Contains("global::Haldus.HaldusScheduledPublishing.ClearAsync<Post>(", due);
        Assert.Contains("skip: failed,", due);
        Assert.Contains("failed++;", due);
        Assert.Contains("attempted < global::Haldus.HaldusScheduledPublishing.MaxPerRun", due);

        Assert.Contains(
            "global::Haldus.HaldusMaintenanceRegistry.RegisterScheduledPublishing<global::Shop.Post>(global::Shop.PostEndpoints.PublishDueAsync);",
            run.Sources["HaldusModule.g.cs"]);

        // Zamanlama istenmediyse hiçbiri üretilmez.
        var plain = Run("app.MapCrud<Post>().Publishable();").AssertCompiles();
        Assert.DoesNotContain("Schedule", plain.Sources["Shop.Post.Endpoints.g.cs"]);
        Assert.DoesNotContain("RegisterScheduledPublishing", plain.Sources["HaldusModule.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Post>().ScheduledPublishing();", "declare Publishable() as well")]
    [InlineData("var posts = app.MapCrud<Post>().Publishable().ScheduledPublishing(); posts.MapAction(\"schedule\", post => { });", "schedule")]
    public void ScheduledPublishing_RejectsDeclarationsThatCannotWork(string body, string message)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == "HALDUS007" && candidate.GetMessage().Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void Publishable_CopiesRowsAndThePrefixedKeyOfTheDraft()
    {
        var run = Run(
            """
            var articles = app.MapCrud<Article>().Publishable().KeyStrategy(KeyStrategy.Prefixed, "art");
            articles.MapDetail<Section>();
            """,
            Articles).AssertCompiles();

        var endpoints = run.Sources["Shop.Article.Endpoints.g.cs"];
        var publish = Handler(endpoints, "PublishAsync");

        Assert.Contains("foreach (var row in source.Sections.OrderBy(row => row.Position).ThenBy(row => row.Id))", publish);
        Assert.Contains("ToResponse(copy) with { Id = copy.PublishedOfId! }", endpoints);
        Assert.Contains("public string? PublishedOfId { get; set; }", run.Sources["Shop.Article.Entity.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Post>().Publishable().Workflow<PostStatus>();", "Publishable cannot be combined with Workflow")]
    [InlineData("app.MapCrud<Post>().Publishable().Approvable();", "Publishable cannot be combined with Approvable")]
    [InlineData("app.MapCrud<Post>().Publishable().EffectiveDated();", "Publishable cannot be combined with EffectiveDated")]
    [InlineData("app.MapCrud<Counter>().Publishable();", "Publishable needs a key that Haldus assigns before saving")]
    [InlineData("app.MapReadOnly<Post>().Publishable();", "Publishable cannot be combined with MapReadOnly")]
    [InlineData(
        """
        var posts = app.MapCrud<Post>().Publishable();
        posts.MapAction("publish", post => post.Title = "");
        """,
        "collides with the 'publish' endpoint")]
    public void Publishable_RejectsDeclarationsThatCannotWork(string body, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == "HALDUS007" && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void HistorySnapshot()
    {
        var run = Run(
            """
            var articles = app.MapCrud<Article>().Revisioned().ChangeFeed().Publishable();
            articles.MapDetail<Section>();
            app.MapCrud<Rate>().EffectiveDated(rate => rate.Currency);
            """,
            Articles).AssertCompiles();

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
