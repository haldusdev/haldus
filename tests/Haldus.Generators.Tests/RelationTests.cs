namespace Haldus.Generators.Tests;

public sealed class RelationTests
{
    private const string Entities = """
        namespace Shop
        {
            public partial class Category
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            public partial class Product
            {
                public string Name { get; set; } = "";
            }

            public partial class Project
            {
                public string Name { get; set; } = "";
            }

            public partial class WorkItem
            {
                public string Title { get; set; } = "";
            }

            public partial class Settings
            {
                public string Theme { get; set; } = "";
            }

            public class Warehouse
            {
                public long Id { get; set; }
            }
        }
        """;

    private static GeneratorRun Run(string body, string entities = Entities) =>
        GeneratorHarness.Run($$"""
            {{entities}}

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
    public void References_GeneratesAForeignKeyTypedAfterTheTargetKey()
    {
        var run = Run("""
            app.MapCrud<Category>();
            app.MapCrud<Product>().References<Category>();
            """).AssertCompiles();

        Assert.Contains("public int CategoryId { get; set; }", run.Sources["Shop.Product.Entity.g.cs"]);

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        Assert.Contains("public required int CategoryId { get; init; }", dtos);
        Assert.Contains("public int CategoryId { get; init; }", dtos);
        Assert.Contains("public int? CategoryId", dtos);

        Assert.Contains(
            "entity.HasOne<global::Shop.Category>().WithMany().HasForeignKey(item => item.CategoryId)"
            + ".OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict);",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void References_ChecksThatTheTargetExistsOnEveryWrite()
    {
        var run = Run("""
            app.MapCrud<Category>();
            app.MapCrud<Product>().References<Category>();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];

        // Oluşturma, PUT ve PATCH.
        Assert.Equal(3, CountOccurrences(endpoints, "ReferenceNotFound(messages, \"categoryId\", \"Category\","));
        Assert.Contains(
            "if (!await contexts.For<global::Shop.Category>().Set<global::Shop.Category>()\n"
            + "            .AnyAsync(target => target.Id == request.CategoryId, cancellationToken))",
            endpoints);
        Assert.Contains("IsSet(\"CategoryId\") && request.CategoryId is { } categoryIdReference", endpoints);
    }

    [Fact]
    public void References_Optional_IsNullableAndOnlyCheckedWhenGiven()
    {
        var run = Run("""
            app.MapCrud<Category>();
            app.MapCrud<Product>().References<Category>(onDelete: OnDelete.SetNull, required: false);
            """).AssertCompiles();

        Assert.Contains("public int? CategoryId { get; set; }", run.Sources["Shop.Product.Entity.g.cs"]);
        Assert.Contains("if (request.CategoryId is { } categoryIdReference", run.Sources["Shop.Product.Endpoints.g.cs"]);
        Assert.Contains("DeleteBehavior.SetNull", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void References_WithDeclaredForeignKey_UsesItInsteadOfGeneratingOne()
    {
        var run = Run(
            """
            app.MapCrud<Category>();
            app.MapCrud<Product>().References<Category>(product => product.MainCategoryId, OnDelete.Cascade);
            """,
            Entities.Replace(
                "public partial class Product\n    {",
                "public partial class Product\n    {\n        public int? MainCategoryId { get; set; }"))
            .AssertCompiles();

        var entity = run.Sources["Shop.Product.Entity.g.cs"];
        Assert.DoesNotContain("CategoryId", entity);

        // Alan nullable yazıldığı için referans isteğe bağlıdır.
        Assert.Contains("if (request.MainCategoryId is { } mainCategoryIdReference", run.Sources["Shop.Product.Endpoints.g.cs"]);
        Assert.Contains("HasForeignKey(item => item.MainCategoryId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void References_ToAnEntityThatHaldusGenerates_IsResolvedByName()
    {
        var run = Run("""
            app.MapLookup<Country>();
            app.MapCrud<Product>().References<Country>();
            """).AssertCompiles();

        var entity = run.Sources["Shop.Product.Entity.g.cs"];
        Assert.Contains("public string CountryCode { get; set; } = \"\";", entity);
        Assert.Contains("AnyAsync(target => target.Code == request.CountryCode, cancellationToken)", run.Sources["Shop.Product.Endpoints.g.cs"]);
    }

    [Fact]
    public void References_ToAPlainEntity_ReadsTheKeyTypeFromItsId()
    {
        var run = Run("app.MapCrud<Product>().References<Warehouse>();").AssertCompiles();

        Assert.Contains("public long WarehouseId { get; set; }", run.Sources["Shop.Product.Entity.g.cs"]);
    }

    [Fact]
    public void Deletion_ReportsADatabaseRefusalAsConflictUnlessItIsSoftDeleted()
    {
        var hard = Run("""
            app.MapCrud<Category>();
            app.MapCrud<Product>().References<Category>();
            """).AssertCompiles();
        var soft = Run("""
            app.MapCrud<Category>().SoftDeletable();
            app.MapCrud<Product>().References<Category>();
            """).AssertCompiles();

        Assert.Contains("InUse(messages, \"Category\", id)", hard.Sources["Shop.Category.Endpoints.g.cs"]);

        // Kayda kimin referans verdiği bu derlemede bilinemeyebilir (başka bir modül, elle yazılmış bir ilişki);
        // bu yüzden her fiziksel silme veritabanının reddini çakışma olarak bildirir.
        Assert.Contains("InUse(messages, \"Product\", id)", hard.Sources["Shop.Product.Endpoints.g.cs"]);

        // Geri alınabilir silme kaydı fiziksel olarak kaldırmaz, dolayısıyla çakışma oluşmaz; çakışma ancak
        // kalıcı silmede ortaya çıkar.
        var softEndpoints = soft.Sources["Shop.Category.Endpoints.g.cs"];
        // "> " öneki handler bildirimini seçer; yoksa ExecuteDeleteAsync çağrısı da eşleşirdi.
        var delete = Handler(softEndpoints, "> DeleteAsync(", "> RestoreAsync(");
        var purge = Handler(softEndpoints, "> PurgeAsync(", " ToResponse(");

        Assert.DoesNotContain("InUse", delete);
        Assert.Contains("catch (global::System.Data.Common.DbException)", purge);
        Assert.Contains("InUse(messages, \"Category\", id)", purge);
    }

    /// <summary>Üretilen dosyadan tek bir handler'ın gövdesini keser.</summary>
    private static string Handler(string source, string start, string end)
    {
        var from = source.LastIndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from, StringComparison.Ordinal);

        Assert.True(from >= 0 && to > from, $"Could not locate the handler between '{start}' and '{end}'.");
        return source[from..to];
    }

    [Fact]
    public void Hierarchical_AddsAnOptionalParentAndGuardsAgainstCycles()
    {
        var run = Run("app.MapCrud<Category>().Hierarchical();").AssertCompiles();

        Assert.Contains("public int? ParentId { get; set; }", run.Sources["Shop.Category.Entity.g.cs"]);

        var endpoints = run.Sources["Shop.Category.Endpoints.g.cs"];
        Assert.Contains("int? parentId,\n        bool? roots,", endpoints);
        Assert.Contains("query = query.Where(item => item.ParentId == null);", endpoints);
        Assert.Contains("private static async global::System.Threading.Tasks.Task<bool> CreatesCycleAsync(", endpoints);
        Assert.Equal(2, CountOccurrences(endpoints, "HierarchyCycle(messages, \"parentId\")"));
        Assert.Contains("IsSet(\"ParentId\") && await CreatesCycleAsync(contexts, id, request.ParentId, cancellationToken)", endpoints);

        // Çocuğu olan düğüm silinemez.
        Assert.Contains("InUse(messages, \"Category\", id)", endpoints);
        Assert.Contains(
            "entity.HasOne<global::Shop.Category>().WithMany().HasForeignKey(item => item.ParentId)"
            + ".OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict);",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Under_NestsTheRoutesAndTakesTheParentFromTheRoute()
    {
        var run = Run("""
            app.MapCrud<Project>();
            app.MapCrud<WorkItem>().Under<Project>();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.WorkItem.Endpoints.g.cs"];
        Assert.Contains("""MapGroup("/projects/{projectId:guid}/work-items")""", endpoints);
        Assert.Contains("ProjectId = projectId, Title = request.Title", endpoints);
        Assert.Contains("NotFound(messages, \"Project\", projectId)", endpoints);

        var dtos = run.Sources["Shop.WorkItem.Dtos.g.cs"];
        var response = dtos[..dtos.IndexOf("CreateWorkItemRequest", StringComparison.Ordinal)];
        Assert.Contains("public required global::System.Guid ProjectId { get; init; }", response);
        Assert.DoesNotContain("ProjectId", dtos[response.Length..]);

        Assert.Contains("DeleteBehavior.Cascade", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Under_HidesTheResourceWheneverItsParentIsNotVisible()
    {
        var run = Run("""
            app.MapCrud<Project>().MultiTenant();
            app.MapCrud<WorkItem>().Under<Project>().SoftDeletable().Archivable().Searchable(item => item.Title);
            """).AssertCompiles();

        // Görünürlük tek tek sorgulara değil modele konur; böylece bu kaynağa referans veren veya onun
        // altına yerleşen kaynaklar da aynı korumadan geçer.
        Assert.Contains(
            "entity.HasQueryFilter(global::Haldus.HaldusFilters.Parent, item => context.Set<global::Shop.Project>()"
            + ".Any(parent => parent.Id == item.ProjectId));",
            run.Sources["HaldusModule.g.cs"]);

        var endpoints = run.Sources["Shop.WorkItem.Endpoints.g.cs"];

        // Her handler ebeveyni adresten alır ve kaydı onunla eşleştirir: liste, arama, tekil okuma,
        // oluşturma, PUT, PATCH, silme, geri yükleme, kalıcı silme, arşivleme ve arşivden çıkarma.
        Assert.Equal(11, CountOccurrences(endpoints, "global::System.Guid projectId,"));
        Assert.Equal(10, CountOccurrences(endpoints, "item.ProjectId == projectId"));
    }

    [Fact]
    public void Under_TenantOwnedRecordsBelowAGlobalParentAreNotCascadedUnlessAskedFor()
    {
        const string Cascade = "HasForeignKey(item => item.ProjectId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)";
        const string Restrict = "HasForeignKey(item => item.ProjectId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict)";

        var sameScope = Run("app.MapCrud<Project>(); app.MapCrud<WorkItem>().Under<Project>();").AssertCompiles();
        var tenantBelowGlobal = Run("app.MapCrud<Project>(); app.MapCrud<WorkItem>().MultiTenant().Under<Project>();").AssertCompiles();
        var explicitCascade = Run(
            "app.MapCrud<Project>(); app.MapCrud<WorkItem>().MultiTenant().Under<Project>(OnDelete.Cascade);").AssertCompiles();
        var tenantBelowTenant = Run(
            "app.MapCrud<Project>().MultiTenant(); app.MapCrud<WorkItem>().MultiTenant().Under<Project>();").AssertCompiles();

        Assert.Contains(Cascade, sameScope.Sources["HaldusModule.g.cs"]);
        Assert.Contains(Cascade, tenantBelowTenant.Sources["HaldusModule.g.cs"]);

        // Genel bir projeyi silen kişi, başka kiracıların iş kalemlerini de silmiş olurdu; varsayılan bunu
        // reddeder ve projenin silme ucu durumu çakışma olarak bildirir.
        Assert.Contains(Restrict, tenantBelowGlobal.Sources["HaldusModule.g.cs"]);
        Assert.Contains("InUse(messages, \"Project\", id)", tenantBelowGlobal.Sources["Shop.Project.Endpoints.g.cs"]);

        // Geliştirici zincirleme silmeyi açıkça seçtiyse karar onundur.
        Assert.Contains(Cascade, explicitCascade.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Under_ParentFilterIsKeptWhenOnlyTheSoftDeleteFilterIsIgnored()
    {
        var run = Run("""
            app.MapCrud<Project>();
            app.MapCrud<WorkItem>().Under<Project>().SoftDeletable();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.WorkItem.Endpoints.g.cs"];

        // Silinmişleri gösteren uçlar yalnızca kendi filtrelerini kaldırır; hepsini kaldırsalardı ebeveyn
        // görünürlüğü de devre dışı kalırdı.
        Assert.Contains("IgnoreQueryFilters(new[] { global::Haldus.HaldusFilters.SoftDelete })", endpoints);
        Assert.DoesNotContain("IgnoreQueryFilters()", endpoints);
    }

    [Theory]
    [InlineData("app.MapCrud<Product>().References<Missing>();", "HALDUS009", "Missing")]
    [InlineData("app.MapCrud<WorkItem>().Under<Warehouse>();", "HALDUS009", "Warehouse")]
    [InlineData("app.MapSingleton<Settings>(); app.MapCrud<Product>().References<Settings>();", "HALDUS007", "'Settings' cannot be referenced")]
    [InlineData("app.MapStore<Settings>().MultiTenant(); app.MapCrud<Product>().References<Settings>();", "HALDUS007", "'Settings' cannot be referenced")]
    [InlineData("app.MapCrud<Category>(); app.MapCrud<Product>().References<Category>(OnDelete.SetNull);", "HALDUS007", "'CategoryId' is required")]
    [InlineData("app.MapCrud<Project>(); app.MapStore<WorkItem>().Under<Project>();", "HALDUS007", "Under cannot be combined with MapStore")]
    [InlineData("app.MapLookup<Settings>().Hierarchical();", "HALDUS007", "Hierarchical cannot be combined with MapLookup")]
    [InlineData("app.MapCrud<Category>(); app.MapSingleton<Settings>().References<Category>();", "HALDUS007", "References cannot be combined with MapSingleton")]
    [InlineData("app.MapCrud<Project>(); app.MapCrud<Category>(); app.MapCrud<WorkItem>().Under<Project>().Under<Category>();", "HALDUS007", "only one parent")]
    public void InvalidRelations_AreReported(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void RelationsOnAVariable_ReportHaldus004()
    {
        var run = Run("""
            app.MapCrud<Category>();
            var products = app.MapCrud<Product>();
            products.References<Category>();
            products.Hierarchical();
            """);

        Assert.Equal(["HALDUS004", "HALDUS004"], run.GeneratorDiagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public void RelationsSnapshot()
    {
        var run = Run("""
            app.MapCrud<Category>().Hierarchical();
            app.MapCrud<Product>().References<Category>(required: false).Searchable(product => product.CategoryId);
            app.MapCrud<Project>().MultiTenant();
            app.MapCrud<WorkItem>().Under<Project>();
            """).AssertCompiles();

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
