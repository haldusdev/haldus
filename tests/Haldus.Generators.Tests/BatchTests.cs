namespace Haldus.Generators.Tests;

public sealed class BatchTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum OrderStatus { Draft, Placed }

            public partial class Category
            {
                public int Id { get; set; }

                public string Name { get; set; } = "";
            }

            public partial class Product
            {
                [MaxLength(200)]
                public string Name { get; set; } = "";

                public decimal Price { get; set; }
            }

            public partial class Order
            {
                public string CustomerName { get; set; } = "";
            }

            public partial class OrderItem
            {
                public int Quantity { get; set; }
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
        }
        """;

    private static GeneratorRun Run(string body) =>
        GeneratorHarness.Run($$"""
            {{Entities}}

            namespace Shop
            {
                public static class Endpoints
                {
                    public static readonly int Limit = 10;

                    public static void Map(IEndpointRouteBuilder app)
                    {
                        {{body}}
                    }
                }
            }
            """);

    [Theory]
    [InlineData("app.MapCrud<Product>().Batch();", true, true, true, true)]
    [InlineData("app.MapAppendOnly<Product>().Batch();", true, true, false, false)]
    [InlineData("app.MapReadOnly<Product>().Batch();", true, false, false, false)]
    [InlineData("app.MapCrud<Product>().Without(CrudOperation.Delete, CrudOperation.Get).Batch();", false, true, true, false)]
    public void Batch_FollowsTheOperationsTheResourceHas(string body, bool get, bool create, bool update, bool delete)
    {
        var run = Run(body).AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Equal(get, endpoints.Contains("group.MapPost(\"/batch-get\", BatchGetAsync)", StringComparison.Ordinal));
        Assert.Equal(create, endpoints.Contains("group.MapPost(\"/batch-create\", BatchCreateAsync)", StringComparison.Ordinal));
        Assert.Equal(update, endpoints.Contains("group.MapPost(\"/batch-update\", BatchUpdateAsync)", StringComparison.Ordinal));
        Assert.Equal(delete, endpoints.Contains("group.MapPost(\"/batch-delete\", BatchDeleteAsync)", StringComparison.Ordinal));

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        Assert.Equal(create, dtos.Contains("record BatchCreateProductRequest", StringComparison.Ordinal));
        Assert.Equal(update, dtos.Contains("record BatchUpdateProductRequest", StringComparison.Ordinal));
    }

    [Fact]
    public void Requests_CarryTheSameItemsAsTheSingleEndpoints()
    {
        var run = Run("app.MapCrud<Product>().Batch();").AssertCompiles();

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        Assert.Contains(
            "public sealed partial record BatchCreateProductRequest\n{\n"
            + "    [global::System.ComponentModel.DataAnnotations.RequiredAttribute]\n"
            + "    public global::System.Collections.Generic.IReadOnlyList<CreateProductRequest> Items { get; init; } = default!;\n}",
            dtos);

        // Güncelleme öğesi, tam değiştirme isteğinin alanlarına ek olarak hangi kaydı değiştirdiğini taşır.
        var item = dtos[dtos.IndexOf("record BatchUpdateProductItem", StringComparison.Ordinal)..];
        Assert.Contains("public global::System.Guid Id { get; init; }", item);
        Assert.Contains("[global::System.ComponentModel.DataAnnotations.MaxLengthAttribute(200)]", item);
        Assert.Contains("IReadOnlyList<BatchUpdateProductItem> Items", item);
    }

    [Fact]
    public void Atomic_ValidatesTheWholeRequestAndStopsAtTheFirstFailure()
    {
        var run = Run("app.MapCrud<Product>().Batch();").AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        var create = Section(endpoints, "> BatchCreateAsync(", "> BatchCreateItemAsync(");

        Assert.Contains(".AddEndpointFilter<global::Haldus.HaldusValidationFilter<BatchCreateProductRequest>>()", endpoints);
        Assert.Contains("await using var batch = await global::Haldus.HaldusBatch.BeginAsync(context, cancellationToken);", create);
        Assert.Contains("return global::Haldus.HaldusBatch.ItemProblem(problem, index);", create);
        Assert.Contains("await batch.CommitAsync(cancellationToken);", create);
        Assert.Contains("new global::Haldus.BatchResult<ProductResponse>(saved.Select(ToResponse).ToList())", create);
        Assert.DoesNotContain("207", endpoints);

        // Sınır varsayılan olarak 100'dür.
        Assert.Contains("if (items.Count > 100)", create);
        Assert.Contains("BatchTooLarge(messages, 100)", create);
    }

    [Fact]
    public void Partial_ValidatesAndSavesEachItemOnItsOwn()
    {
        var run = Run("app.MapCrud<Product>().Batch(atomic: false, maxItems: 25);").AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        var create = Section(endpoints, "> BatchCreateAsync(", "> BatchCreateItemAsync(");
        var delete = Section(endpoints, "> BatchDeleteAsync(", "> BatchDeleteItemAsync(");

        // İsteğin tamamı doğrulanmaz; aksi halde geçersiz tek bir öğe diğerlerini de düşürürdü.
        Assert.DoesNotContain("HaldusValidationFilter<BatchCreateProductRequest>", endpoints);
        Assert.Contains("var problem = global::Haldus.HaldusBatch.Validate(items[index], messages)", create);
        Assert.Contains("await using var itemScope = await global::Haldus.HaldusBatch.BeginAsync(context, cancellationToken);", create);
        Assert.Contains("results.Add(global::Haldus.HaldusBatch.Succeeded(index, 201, ToResponse(saved[saved.Count - 1])));", create);
        Assert.Contains("results.Add(global::Haldus.HaldusBatch.Failed<ProductResponse>(index, problem));", create);
        Assert.Contains("statusCode: 207", create);
        Assert.Contains(".Produces<global::Haldus.BatchResults<ProductResponse>>(207)", endpoints);

        Assert.Contains("results.Add(global::Haldus.HaldusBatch.Succeeded(index, 204, ids[index]));", delete);
        Assert.Contains("if (ids.Count > 25)", delete);
    }

    [Fact]
    public void Items_GoThroughTheRulesOfTheSingleEndpoints()
    {
        var run = Run("""
            app.MapCrud<Category>();

            var orders = app.MapCrud<Order>()
                .References<Category>()
                .Workflow<OrderStatus>()
                .EditableIn(OrderStatus.Draft)
                .DeletableIn(OrderStatus.Draft)
                .Numbered("O-{seq}")
                .Batch();

            orders.MapDetail<OrderItem>();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];
        var create = Section(endpoints, "> BatchCreateItemAsync(", "> BatchUpdateAsync(");
        var update = Section(endpoints, "> BatchUpdateItemAsync(", "> BatchDeleteAsync(");
        var delete = endpoints[endpoints.IndexOf("> BatchDeleteItemAsync(", StringComparison.Ordinal)..];

        // Oluşturma: referans kontrolü, başlangıç durumu, satırlar ve numara.
        Assert.Contains("ReferenceNotFound(messages, \"categoryId\", \"Category\", request.CategoryId)", create);
        Assert.Contains("Status = global::Shop.OrderStatus.Draft", create);
        Assert.Contains("entity.OrderItems.Add(new global::Shop.OrderItem", create);
        Assert.Contains("await global::Haldus.HaldusNumbering.AllocateAsync(context, Numbering, cancellationToken)", create);

        // Güncelleme: durum kuralı, referans kontrolü, satır eşitleme ve durumun yazmayla doğrulanması.
        Assert.Contains("var id = request.Id;", update);
        Assert.Contains("StateConflict(messages, \"update\", entity.Status)", update);
        Assert.Contains("ReferenceNotFound(messages, \"categoryId\"", update);
        Assert.Contains("SyncOrderItems(entity, request.OrderItems, context, messages)", update);
        Assert.Contains("return global::Haldus.HaldusProblems.StateChanged(messages);", update);

        Assert.Contains("StateConflict(messages, \"delete\", entity.Status)", delete);
        Assert.Contains("NotFound(messages, \"Order\", id)", delete);
    }

    [Fact]
    public void UnderAParent_TheParentIsCheckedOncePerRequest()
    {
        var run = Run("""
            app.MapCrud<Project>();
            app.MapCrud<WorkItem>().Under<Project>().Batch();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.WorkItem.Endpoints.g.cs"];
        var create = Section(endpoints, "> BatchCreateAsync(", "> BatchCreateItemAsync(");
        var item = Section(endpoints, "> BatchCreateItemAsync(", "> BatchUpdateAsync(");

        Assert.Contains("global::System.Guid projectId,", create);
        Assert.Contains("NotFound(messages, \"Project\", projectId)", create);
        Assert.Contains("BatchCreateItemAsync(projectId, items[index], context, saved, contexts, messages, cancellationToken)", create);

        Assert.DoesNotContain("\"Project\"", item);
        Assert.Contains("ProjectId = projectId, Title = request.Title", item);

        // Toplu okuma ve silme de yalnızca bu ebeveynin kayıtlarını görür.
        Assert.Contains(".Where(item => item.ProjectId == projectId)\n            .Where(item => ids.Contains(item.Id))", endpoints);
        Assert.Contains("BatchDeleteItemAsync(projectId, ids[index], context, messages, cancellationToken)", endpoints);
    }

    [Fact]
    public void Idempotent_CoversBatchCreation()
    {
        var run = Run("app.MapCrud<Product>().Idempotent().Batch();").AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("new global::Haldus.HaldusIdempotencyFilter<Product>(typeof(BatchCreateProductRequest), required: false)", endpoints);

        // Tekil ve toplu oluşturma; güncelleme ve silme zaten tekrara dayanıklıdır.
        Assert.Equal(2, CountOccurrences(endpoints, "HaldusIdempotencyFilter"));
    }

    [Fact]
    public void ConcurrencyChecked_IsAllowedWhenOnlyCreationAndReadsAreBatched()
    {
        var run = Run("app.MapCrud<Product>().ConcurrencyChecked().Only(CrudOperation.List, CrudOperation.Get, CrudOperation.Create).Batch();")
            .AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("BatchCreateAsync", endpoints);
        Assert.DoesNotContain("BatchUpdateAsync", endpoints);
    }

    [Theory]
    [InlineData("app.MapCrud<Product>().ConcurrencyChecked().Batch();", "HALDUS007", "Batch cannot be combined with ConcurrencyChecked")]
    [InlineData("app.MapStore<Settings>().Batch();", "HALDUS007", "Batch cannot be combined with MapStore")]
    [InlineData("app.MapSingleton<Settings>().Batch();", "HALDUS007", "Batch cannot be combined with MapSingleton")]
    [InlineData("app.MapJob<Settings>().Batch();", "HALDUS007", "Batch cannot be combined with MapJob")]
    [InlineData("app.MapCrud<Product>().Batch(maxItems: Limit);", "HALDUS010", "arguments of Batch must be constants")]
    [InlineData("app.MapCrud<Product>().Batch(maxItems: 0);", "HALDUS010", "maxItems of Batch must be between 1 and 10000")]
    public void InvalidBatches_AreReported(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void BatchOnAVariable_ReportsHaldus004()
    {
        var run = Run("""
            var products = app.MapCrud<Product>();
            products.Batch();
            """);

        Assert.Equal(["HALDUS004"], run.GeneratorDiagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public void BatchSnapshot()
    {
        var run = Run("app.MapCrud<Product>().Batch();").AssertCompiles();

        Snapshot.Verify(run);
    }

    [Fact]
    public void PartialBatchSnapshot()
    {
        var run = Run("app.MapCrud<Product>().Batch(atomic: false, maxItems: 25);").AssertCompiles();

        Snapshot.Verify(run);
    }

    private static string Section(string source, string start, string end)
    {
        var from = source.LastIndexOf(start, StringComparison.Ordinal);
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
