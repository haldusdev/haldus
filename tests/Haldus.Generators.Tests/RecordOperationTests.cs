namespace Haldus.Generators.Tests;

/// <summary>Kayıt işlemleri: sıra ve taşıma, kopyalama, kaydetmeyen önizleme.</summary>
public sealed class RecordOperationTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum OrderStatus { Draft, Placed }

            public partial class Board
            {
                public string Name { get; set; } = "";
            }

            public partial class Todo
            {
                public string Title { get; set; } = "";
            }

            public partial class Ranked
            {
                public string Title { get; set; } = "";
                public int Position { get; set; }
            }

            public partial class Coupon
            {
                public string Code { get; set; } = "";
                public int Percent { get; set; }
                public int Used { get; private set; }
            }

            public partial class Settings
            {
                public string Theme { get; set; } = "";
            }

            public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
        }
        """;

    /// <summary>
    /// Satırları olan sipariş. Toplamı satırlardan hesapladığı için yalnızca satırların da eşlendiği senaryolarda
    /// derlenir; diğerlerine eklenmez.
    /// </summary>
    private const string Orders = """
        namespace Shop
        {
            public partial class Order : IComputed
            {
                public string Customer { get; set; } = "";
                public decimal Total { get; private set; }
                public void Compute() => Total = OrderItems.Sum(item => item.Quantity * item.UnitPrice);
            }

            public partial class OrderItem
            {
                public int Quantity { get; set; }
                public decimal UnitPrice { get; set; }
            }
        }
        """;

    private static GeneratorRun Run(string body, string extra = "") =>
        GeneratorHarness.Run($$"""
            using System.Linq;

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

    private static string Record(string source, string name)
    {
        var start = source.IndexOf($"record {name}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{name}' was not generated.");

        var end = source.IndexOf("\npublic ", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static string Handler(string source, string name)
    {
        // Bildirimde parantezden sonra satır biter; çağrılarda bitmez.
        var start = source.IndexOf($" {name}(\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{name}' was not generated.");

        var end = source.IndexOf("\n    private static ", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static void AssertSingle(GeneratorRun run, string id, string message)
    {
        var diagnostic = Assert.Single(run.GeneratorDiagnostics);

        Assert.Equal(id, diagnostic.Id);
        Assert.Contains(message, diagnostic.GetMessage());
    }

    // ── Sıra ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Orderable_AddsAPositionThatOnlyMoveCanChange()
    {
        var run = Run("app.MapCrud<Todo>().Orderable();").AssertCompiles();

        var entity = run.Sources["Shop.Todo.Entity.g.cs"];
        Assert.Contains("global::Haldus.IOrderable", entity);
        Assert.Contains("public int Position { get; set; }", entity);

        var dtos = run.Sources["Shop.Todo.Dtos.g.cs"];
        Assert.Contains("public required int Position", Record(dtos, "TodoResponse"));
        Assert.DoesNotContain("Position", Record(dtos, "CreateTodoRequest"));
        Assert.DoesNotContain("Position", Record(dtos, "UpdateTodoRequest"));
        Assert.DoesNotContain("Position", Record(dtos, "PatchTodoRequest"));

        var endpoints = run.Sources["Shop.Todo.Endpoints.g.cs"];
        Assert.Contains("""group.MapPost("/{id:guid}/move", MoveAsync)""", endpoints);
        Assert.Contains("global::Haldus.HaldusValidationFilter<global::Haldus.MoveRequest>", endpoints);
        Assert.Contains("request.Position.GetValueOrDefault()", Handler(endpoints, "MoveAsync"));
    }

    [Fact]
    public void Orderable_ListsInOrderAndAppendsNewRecordsUnderALock()
    {
        var endpoints = Run("app.MapCrud<Todo>().Orderable();").AssertCompiles().Sources["Shop.Todo.Endpoints.g.cs"];

        var list = Handler(endpoints, "ListAsync");
        Assert.Contains(".OrderBy(item => item.Position)", list);
        Assert.Contains(".ThenBy(item => item.Id)", list);

        var create = Handler(endpoints, "CreateAsync");
        Assert.Contains("""await using var ordering = await global::Haldus.HaldusOrdering.BeginAsync(context, "Todos", cancellationToken);""", create);
        Assert.Contains("entity.Position = await global::Haldus.HaldusOrdering.NextAsync(context.Set<Todo>(), cancellationToken);", create);
        Assert.Contains("await ordering.CommitAsync(cancellationToken);", create);
    }

    [Fact]
    public void Orderable_DeletingClosesTheGapAndRestoringAppends()
    {
        var endpoints = Run("app.MapCrud<Todo>().SoftDeletable().Orderable().Batch();").AssertCompiles().Sources["Shop.Todo.Endpoints.g.cs"];

        foreach (var handler in new[] { "DeleteAsync", "BatchDeleteItemAsync" })
        {
            Assert.Contains("HaldusOrdering.BeginAsync", Handler(endpoints, handler));
            Assert.Contains("HaldusOrdering.CloseGapAsync(context.Set<Todo>(), entity.Position, cancellationToken)", Handler(endpoints, handler));
        }

        // Geri yüklenen kayıt eski yerine değil sona gelir; eski yeri çoktan kapanmıştır.
        Assert.Contains("HaldusOrdering.NextAsync", Handler(endpoints, "RestoreAsync"));
        Assert.Contains("HaldusOrdering.NextAsync", Handler(endpoints, "BatchCreateItemAsync"));
    }

    [Fact]
    public void Orderable_IsScopedToTheTenantOwnerAndParent()
    {
        var endpoints = Run("""
            app.MapCrud<Board>();
            app.MapCrud<Todo>().MultiTenant().OwnedByCurrentUser(underMe: false).Under<Board>().Orderable();
            """).AssertCompiles().Sources["Shop.Todo.Endpoints.g.cs"];

        var move = Handler(endpoints, "MoveAsync");

        // Kilit kapsamı ayırır: başka bir kiracının, kullanıcının veya ebeveynin sırası beklenmez.
        Assert.Contains(
            """BeginAsync(context, "Todos", cancellationToken, global::Haldus.HaldusScope.TenantId(context), global::Haldus.HaldusScope.UserId(context), boardId)""",
            move);

        // Kardeşler aynı ebeveynin altındakilerdir; kiracı ve sahip sorgu filtresinden gelir.
        Assert.Contains("context.Set<Todo>().Where(item => item.BoardId == boardId).OrderBy(item => item.Position).ThenBy(item => item.Id)", move);
    }

    [Fact]
    public void Orderable_InATree_OrdersTheChildrenOfEachNode()
    {
        var endpoints = Run("app.MapCrud<Todo>().Hierarchical().Orderable();").AssertCompiles().Sources["Shop.Todo.Endpoints.g.cs"];

        Assert.Contains("context.Set<Todo>().Where(item => item.ParentId == entity.ParentId)", Handler(endpoints, "CreateAsync"));

        // Üst düğümü değiştiren güncelleme, eski daldaki boşluğu kapatır ve düğümü yeni dalın sonuna koyar.
        foreach (var handler in new[] { "UpdateAsync", "PatchAsync" })
        {
            var update = Handler(endpoints, handler);
            Assert.Contains("var orderedUnder = entity.ParentId;", update);
            Assert.Contains("await using var ordering = entity.ParentId == orderedUnder", update);
            Assert.Contains("CloseGapAsync(context.Set<Todo>().Where(item => item.ParentId == orderedUnder), entity.Position, cancellationToken)", update);
        }

        // Kilit bütün ağacı kapsar; düğüm bir daldan ötekine geçerken iki dal birlikte değişir.
        Assert.Contains("""BeginAsync(context, "Todos", cancellationToken)""", Handler(endpoints, "UpdateAsync"));
    }

    [Fact]
    public void Orderable_WhenTheParentIsFixedAtCreation_UpdatesDoNotTouchTheOrder()
    {
        var endpoints = Run("app.MapCrud<Todo>().Hierarchical().Orderable().CreateOnly(todo => todo.ParentId);")
            .AssertCompiles().Sources["Shop.Todo.Endpoints.g.cs"];

        Assert.DoesNotContain("HaldusOrdering", Handler(endpoints, "UpdateAsync"));
    }

    [Fact]
    public void Orderable_CombinesWithSortable()
    {
        var endpoints = Run("app.MapCrud<Todo>().Orderable().Sortable(todo => todo.Title, todo => todo.Position);")
            .AssertCompiles().Sources["Shop.Todo.Endpoints.g.cs"];

        // Başka bir sıra istenmediyse kayıtların kendi sırası geçerlidir.
        Assert.Contains("ordered ??= query.OrderBy(item => item.Position);", endpoints);
        Assert.Contains("""case "position":""", endpoints);
    }

    [Fact]
    public void Orderable_UsesAPositionWrittenByTheUser()
    {
        var run = Run("app.MapCrud<Ranked>().Orderable();").AssertCompiles();

        Assert.DoesNotContain("int Position", run.Sources["Shop.Ranked.Entity.g.cs"]);
        Assert.DoesNotContain("Position", Record(run.Sources["Shop.Ranked.Dtos.g.cs"], "CreateRankedRequest"));
        Assert.Contains("public required int Position", Record(run.Sources["Shop.Ranked.Dtos.g.cs"], "RankedResponse"));
    }

    [Theory]
    [InlineData("app.MapAppendOnly<Todo>().Orderable();", "Orderable cannot be combined with MapAppendOnly")]
    [InlineData("app.MapStore<Settings>().Orderable();", "Orderable cannot be combined with MapStore")]
    public void Orderable_IsOnlyForResourcesTheClientCreatesAndDeletes(string body, string message) =>
        AssertSingle(Run(body), "HALDUS007", message);

    // ── Kopya ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cloneable_CopiesTheWritableFieldsAndLetsTheRequestOverrideThem()
    {
        var run = Run("app.MapCrud<Coupon>().CreateOnly(coupon => coupon.Code).Cloneable();").AssertCompiles();

        // Kopya yeni bir kayıttır; yalnızca oluştururken yazılan alan da verilebilir.
        var request = Record(run.Sources["Shop.Coupon.Dtos.g.cs"], "CloneCouponRequest");
        Assert.Contains(": global::Haldus.IHaldusPatch", request);
        Assert.Contains("string? Code", request);
        Assert.Contains("int? Percent", request);
        Assert.DoesNotContain("Used", request);

        var endpoints = run.Sources["Shop.Coupon.Endpoints.g.cs"];
        Assert.Contains("""group.MapPost("/{id:guid}/clone", CloneAsync)""", endpoints);

        var clone = Handler(endpoints, "CloneAsync");
        Assert.Contains("CloneCouponRequest? request,", clone);
        Assert.Contains("request ??= new CloneCouponRequest();", clone);
        Assert.Contains("Code = source.Code, Percent = source.Percent", clone);
        Assert.Contains("""IsSet("Code")""", clone);
        Assert.Contains("global::Haldus.HaldusRoutes.Sibling(httpContext, entity.Id)", clone);

        // Sunucunun tuttuğu alanlar kopyalanmaz.
        Assert.DoesNotContain("source.Used", clone);
    }

    [Fact]
    public void Cloneable_CopiesTheRowsAndStartsTheCopyAsANewRecord()
    {
        var run = Run("""
            var orders = app.MapCrud<Order>()
                .Workflow<OrderStatus>(initial: OrderStatus.Draft)
                .Numbered("ORD-{seq:6}")
                .Idempotent()
                .Cloneable();
            orders.MapDetail<OrderItem>();
            """, Orders).AssertCompiles();

        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];
        var clone = Handler(endpoints, "CloneAsync");

        // Satırlar kendi kimlikleriyle, sıraları korunarak kopyalanır.
        Assert.Contains("foreach (var row in source.OrderItems.OrderBy(row => row.Position).ThenBy(row => row.Id))", clone);
        Assert.Contains("Id = global::System.Guid.CreateVersion7(), Position = orderItemsPosition++, Quantity = row.Quantity", clone);

        // Durum başlangıçtan başlar, numara kendi kuralıyla yeniden verilir, hesaplanan alanlar yeniden hesaplanır.
        Assert.Contains("Status = global::Shop.OrderStatus.Draft", clone);
        Assert.Contains("entity.Number = numbering.Number;", clone);
        Assert.Contains("((global::Haldus.IComputed)entity).Compute();", clone);
        Assert.DoesNotContain("source.Status", clone);
        Assert.DoesNotContain("source.Number", clone);

        // Kopyalama da bir oluşturmadır; tekrara karşı aynı biçimde korunur.
        Assert.Contains("new global::Haldus.HaldusIdempotencyFilter<Order>(typeof(CloneOrderRequest)", endpoints);
    }

    [Fact]
    public void Cloneable_ChecksTheNaturalKeyOfTheCopy()
    {
        var clone = Handler(
            Run("app.MapCrud<Coupon>().NaturalKey(coupon => coupon.Code).Cloneable();").AssertCompiles().Sources["Shop.Coupon.Endpoints.g.cs"],
            "CloneAsync");

        // İstek değeri değiştirmediyse kopya kaynakla çakışır; bu, yeni bir değer verilmesi gerektiğini söyler.
        Assert.Contains("AnyAsync(item => item.Code == entity.Code, cancellationToken)", clone);
        Assert.Contains("""Duplicate(messages, "Coupon", "code", entity.Code)""", clone);
        Assert.Contains("IsUniqueViolation", clone);
    }

    [Theory]
    [InlineData("app.MapAppendOnly<Coupon>().Cloneable();", "Cloneable cannot be combined with MapAppendOnly")]
    [InlineData("app.MapCrud<Coupon>().Without(CrudOperation.Create).Cloneable();", "Cloneable requires the Create operation")]
    public void Cloneable_InvalidDeclarations_AreReported(string body, string message) =>
        AssertSingle(Run(body), "HALDUS007", message);

    // ── Önizleme ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Previewable_RunsTheRealChecksAndComputationWithoutSaving()
    {
        var endpoints = Run("""
            var orders = app.MapCrud<Order>().Workflow<OrderStatus>(initial: OrderStatus.Draft).EditableIn(OrderStatus.Draft).Previewable();
            orders.MapDetail<OrderItem>();
            """, Orders).AssertCompiles().Sources["Shop.Order.Endpoints.g.cs"];

        Assert.Contains("""group.MapPost("/preview", PreviewAsync)""", endpoints);
        Assert.Contains("""group.MapPost("/{id:guid}/preview", PreviewUpdateAsync)""", endpoints);

        // Oluşturma önizlemesi gerçek isteğin tipini ve doğrulamasını kullanır; kimlik üretmez ve kaydetmez.
        var create = Handler(endpoints, "PreviewAsync");
        Assert.Contains("CreateOrderRequest request,", create);
        Assert.Contains("((global::Haldus.IComputed)entity).Compute();", create);
        Assert.DoesNotContain("CreateVersion7", create.Split("foreach")[0]);
        Assert.DoesNotContain("SaveChangesAsync", create);

        // Güncelleme önizlemesi kaydın durum kuralına uyar ve bekleyen değişiklikleri bırakır.
        var update = Handler(endpoints, "PreviewUpdateAsync");
        Assert.Contains("StateConflict(messages, \"update\", entity.Status)", update);
        Assert.Contains("context.ChangeTracker.Clear();", update);
        Assert.DoesNotContain("SaveChangesAsync", update);
    }

    [Fact]
    public void Previewable_FollowsTheOperationsOfTheResource()
    {
        var appendOnly = Run("app.MapAppendOnly<Coupon>().Previewable();").AssertCompiles().Sources["Shop.Coupon.Endpoints.g.cs"];
        var createOnly = Run("app.MapCrud<Coupon>().Without(CrudOperation.Update).Previewable();").AssertCompiles().Sources["Shop.Coupon.Endpoints.g.cs"];

        foreach (var endpoints in new[] { appendOnly, createOnly })
        {
            Assert.Contains("PreviewAsync", endpoints);
            Assert.DoesNotContain("PreviewUpdateAsync", endpoints);
        }
    }

    [Theory]
    [InlineData("app.MapStore<Settings>().Previewable();", "Previewable cannot be combined with MapStore")]
    [InlineData("app.MapCrud<Coupon>().Without(CrudOperation.Create).Previewable();", "Previewable requires the Create operation")]
    public void Previewable_InvalidDeclarations_AreReported(string body, string message) =>
        AssertSingle(Run(body), "HALDUS007", message);

    [Theory]
    [InlineData("var todos = app.MapCrud<Todo>().Orderable(); todos.MapAction(\"move\", todo => { });", "Orderable")]
    [InlineData("var todos = app.MapCrud<Todo>().Cloneable(); todos.MapAction(\"clone\", todo => { });", "Cloneable")]
    [InlineData("var todos = app.MapCrud<Todo>().Previewable(); todos.MapAction(\"preview\", todo => { });", "Previewable")]
    public void ActionsCannotTakeTheAddressOfAGeneratedOperation(string body, string trait) =>
        AssertSingle(Run(body), "HALDUS007", trait);

    [Fact]
    public void RecordOperationsSnapshot()
    {
        var run = Run("""
            var orders = app.MapCrud<Order>()
                .Orderable()
                .Cloneable()
                .Previewable();
            orders.MapDetail<OrderItem>();
            """, Orders).AssertCompiles();

        Snapshot.Verify(run);
    }
}
