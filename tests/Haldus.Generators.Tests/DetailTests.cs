namespace Haldus.Generators.Tests;

public sealed class DetailTests
{
    private const string Entities = """
        namespace Shop
        {
            public partial class Order : IComputed
            {
                public string CustomerName { get; set; } = "";

                public decimal Subtotal { get; set; }

                public decimal TaxRate { get; set; }

                public decimal Total { get; private set; }

                // Satırlara bakmaz; böylece sipariş, satırları gömülü olmayan testlerde de derlenir.
                public void Compute() => Total = Subtotal * (1 + TaxRate);
            }

            public partial class OrderItem
            {
                [MaxLength(200)]
                public string ProductName { get; set; } = "";

                [Range(1, 10_000)]
                public int Quantity { get; set; }

                public decimal UnitPrice { get; set; }
            }

            public partial class OrderNote
            {
                public string Text { get; set; } = "";
            }

            public partial class Invoice
            {
                public string Number { get; set; } = "";
            }

            public partial class InvoiceLine
            {
                public string Description { get; set; } = "";

                public int Position { get; set; }
            }

            public partial class NumberedLine
            {
                public int Id { get; set; }
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
                    public static void Map(IEndpointRouteBuilder app)
                    {
                        {{body}}
                    }
                }
            }
            """);

    [Fact]
    public void Embedded_AddsTheRowsToTheOwnerInsteadOfGeneratingEndpoints()
    {
        var run = Run("app.MapCrud<Order>().MapDetail<OrderItem>();").AssertCompiles();

        Assert.DoesNotContain("Shop.OrderItem.Endpoints.g.cs", run.Sources.Keys);
        Assert.Contains(
            "public global::System.Collections.Generic.List<global::Shop.OrderItem> OrderItems { get; } = new();",
            run.Sources["Shop.Order.Entity.g.cs"]);

        var item = run.Sources["Shop.OrderItem.Entity.g.cs"];
        Assert.Contains("public global::System.Guid Id { get; set; }", item);
        Assert.Contains("public global::System.Guid OrderId { get; set; }", item);

        var dtos = run.Sources["Shop.Order.Dtos.g.cs"];
        Assert.Contains("public required global::System.Collections.Generic.IReadOnlyList<global::Shop.OrderItemResponse> OrderItems { get; init; }", dtos);
        Assert.Contains("public global::System.Collections.Generic.IReadOnlyList<global::Shop.CreateOrderItemRequest>? OrderItems { get; init; }", dtos);

        // Tam değiştirmede satırlar zorunludur; gönderilmeyen koleksiyon "hepsini sil" anlamına gelmemelidir.
        Assert.Contains(
            "[global::System.ComponentModel.DataAnnotations.RequiredAttribute]\n"
            + "    public global::System.Collections.Generic.IReadOnlyList<global::Shop.UpdateOrderItemRequest> OrderItems { get; init; } = default!;",
            dtos);
        Assert.Contains("\"OrderItems\" => _orderItemsSet,", dtos);
    }

    [Fact]
    public void Embedded_RowDtosCarryTheValidationRulesAndAnOptionalIdOnUpdate()
    {
        var run = Run("app.MapCrud<Order>().MapDetail<OrderItem>();").AssertCompiles();

        var dtos = run.Sources["Shop.OrderItem.Dtos.g.cs"];
        var create = Section(dtos, "record CreateOrderItemRequest", "record UpdateOrderItemRequest");
        var update = dtos[dtos.IndexOf("record UpdateOrderItemRequest", StringComparison.Ordinal)..];

        Assert.Contains("[global::System.ComponentModel.DataAnnotations.RangeAttribute(1, 10000)]", create);
        Assert.DoesNotContain(" Id ", create);
        Assert.Contains("public global::System.Guid? Id { get; init; }", update);

        // Sahip adresten ve kayıttan bilinir; satırın isteğinde yer almaz. Satırlar tek tek yamalanmaz.
        Assert.DoesNotContain("OrderId", create + update);
        Assert.DoesNotContain("PatchOrderItemRequest", dtos);
    }

    [Fact]
    public void Embedded_RowsAreStoredAndReturnedInTheOrderTheyWereSent()
    {
        var run = Run("app.MapCrud<Order>().MapDetail<OrderItem>();").AssertCompiles();

        // Kimlikler aynı milisaniyede üretildiğinde sıralı değildir; sıra ayrı bir alanda tutulur.
        Assert.Contains("public int Position { get; set; }", run.Sources["Shop.OrderItem.Entity.g.cs"]);
        Assert.DoesNotContain("Position", run.Sources["Shop.OrderItem.Dtos.g.cs"]);

        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];
        Assert.Contains("Position = orderItemsPosition++", endpoints);
        Assert.Contains("existing.Position = position++;", endpoints);
        Assert.Contains("OrderId = entity.Id, Position = position++", endpoints);

        // Liste izdüşümü ve bellekteki kayıttan yanıt.
        Assert.Equal(2, CountOccurrences(endpoints, "item.OrderItems.OrderBy(detail => detail.Position).ThenBy(detail => detail.Id)"));
    }

    [Fact]
    public void Embedded_APositionWrittenByTheUserIsReusedAndStaysOutOfTheDtos()
    {
        var run = Run("app.MapCrud<Invoice>().MapDetail<InvoiceLine>();").AssertCompiles();

        Assert.DoesNotContain("Position", run.Sources["Shop.InvoiceLine.Entity.g.cs"]);
        Assert.DoesNotContain("Position", run.Sources["Shop.InvoiceLine.Dtos.g.cs"]);
        Assert.Contains("Position = invoiceLinesPosition++, Description = row.Description", run.Sources["Shop.Invoice.Endpoints.g.cs"]);
    }

    [Fact]
    public void Embedded_UpdatesSynchronizeTheRowsAndRejectForeignIds()
    {
        var run = Run("app.MapCrud<Order>().MapDetail<OrderItem>();").AssertCompiles();

        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];

        // PUT, PATCH ve silme kaydı satırlarıyla yükler; tekil okuma da öyle.
        Assert.Equal(4, CountOccurrences(endpoints, ".Include(item => item.OrderItems)"));
        Assert.Contains("if (SyncOrderItems(entity, request.OrderItems, context, messages) is { } orderItemsProblem)", endpoints);
        Assert.Contains("if (((global::Haldus.IHaldusPatch)request).IsSet(\"OrderItems\"))", endpoints);
        Assert.Contains("DetailNotFound(messages, \"orderItems\", \"OrderItem\", rowId)", endpoints);

        // Aynı kimlik istekte iki kez geçemez.
        Assert.Contains("!current.TryGetValue(rowId, out var existing) || !kept.Add(rowId)", endpoints);
    }

    [Fact]
    public void Embedded_RowsBelongToTheOwnerInTheModel()
    {
        var run = Run("app.MapCrud<Order>().MultiTenant().MapDetail<OrderItem>();").AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains(
            "entity.HasMany(item => item.OrderItems).WithOne().HasForeignKey(detail => detail.OrderId)"
            + ".OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade);",
            module);

        // Satırlar sahibinin görünürlüğünü devralır; kiracı filtresi sahibin üzerinden uygulanır.
        Assert.Contains(
            "entity.HasQueryFilter(global::Haldus.HaldusFilters.Parent, item => context.Set<global::Shop.Order>()"
            + ".Any(parent => parent.Id == item.OrderId));",
            module);
        Assert.DoesNotContain("Register<global::Shop.OrderItem>", module);
    }

    [Fact]
    public void Embedded_ChangingOnlyTheRowsStillTouchesTheOwner()
    {
        var run = Run("app.MapCrud<Order>().ConcurrencyChecked().MapDetail<OrderItem>();").AssertCompiles();

        // Sahibin kendi alanları değişmese de sürümü ilerlemelidir; yoksa aynı ETag iki farklı içeriği gösterirdi.
        Assert.Equal(
            2,
            CountOccurrences(
                run.Sources["Shop.Order.Endpoints.g.cs"],
                "context.Entry(entity).State = global::Microsoft.EntityFrameworkCore.EntityState.Modified;"));
    }

    [Fact]
    public void Computed_RunsBeforeEverySaveThatAppliesARequest()
    {
        const string Compute = "((global::Haldus.IComputed)entity).Compute();";

        var computed = Run("app.MapCrud<Order>().MapDetail<OrderItem>();").AssertCompiles();
        var plain = Run("app.MapCrud<Invoice>().MapDetail<InvoiceLine>();").AssertCompiles();

        // Oluşturma, PUT ve PATCH.
        Assert.Equal(3, CountOccurrences(computed.Sources["Shop.Order.Endpoints.g.cs"], Compute));
        Assert.DoesNotContain("IComputed", plain.Sources["Shop.Invoice.Endpoints.g.cs"]);

        // Hesaplanan alanın herkese açık setter'ı yoktur; yalnızca yanıtta görünür.
        var dtos = computed.Sources["Shop.Order.Dtos.g.cs"];
        Assert.Contains("public required decimal Total { get; init; }", dtos);
        Assert.Equal(1, CountOccurrences(dtos, " Total "));
    }

    [Fact]
    public void AsSubresource_GeneratesEndpointsBelowTheOwnerInsteadOfEmbedding()
    {
        var run = Run("app.MapCrud<Order>().MapDetail<OrderNote>().AsSubresource();").AssertCompiles();

        // Adres sahibin grubuna göredir; MapDetail uçları o grubun altına kurar.
        var endpoints = run.Sources["Shop.OrderNote.Endpoints.g.cs"];
        Assert.Contains("""MapGroup("/{orderId:guid}/order-notes")""", endpoints);
        Assert.Contains("OrderId = orderId, Text = request.Text", endpoints);
        Assert.Contains("NotFound(messages, \"Order\", orderId)", endpoints);

        Assert.DoesNotContain("OrderNotes", run.Sources["Shop.Order.Entity.g.cs"]);
        Assert.DoesNotContain("OrderNotes", run.Sources["Shop.Order.Dtos.g.cs"]);
        Assert.DoesNotContain("Include(", run.Sources["Shop.Order.Endpoints.g.cs"]);
        Assert.DoesNotContain("Position", run.Sources["Shop.OrderNote.Entity.g.cs"]);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("Register<global::Shop.OrderNote>(global::Shop.OrderNoteEndpoints.Map)", module);
        Assert.Contains(
            "entity.HasOne<global::Shop.Order>().WithMany().HasForeignKey(item => item.OrderId)"
            + ".OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade);",
            module);
    }

    [Fact]
    public void AsSubresource_IsAllowedBelowAReadOnlyOwner()
    {
        var run = Run("app.MapReadOnly<Order>().MapDetail<OrderNote>().AsSubresource();").AssertCompiles();

        Assert.Contains("""MapGroup("/{orderId:guid}/order-notes")""", run.Sources["Shop.OrderNote.Endpoints.g.cs"]);
    }

    [Fact]
    public void OneOwner_CanHaveBothKindsOfDetail()
    {
        var run = Run("""
            var orders = app.MapCrud<Order>().MultiTenant();
            orders.MapDetail<OrderItem>();
            orders.MapDetail<OrderNote>().AsSubresource();
            """).AssertCompiles();

        Assert.DoesNotContain("Shop.OrderItem.Endpoints.g.cs", run.Sources.Keys);
        Assert.Contains("Shop.OrderNote.Endpoints.g.cs", run.Sources.Keys);
        Assert.Contains("MultiTenant", run.Sources["Shop.Order.Entity.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapSingleton<Settings>().MapDetail<OrderItem>();", "HALDUS007", "MapSingleton does not support embedded details")]
    [InlineData("app.MapReadOnly<Order>().MapDetail<OrderItem>();", "HALDUS007", "MapReadOnly does not support embedded details")]
    [InlineData("app.MapStore<Settings>().MapDetail<OrderNote>().AsSubresource();", "HALDUS007", "MapStore does not support sub-resource details")]
    [InlineData("app.MapCrud<Order>().MapDetail<NumberedLine>();", "HALDUS007", "its Id must be a Guid")]
    [InlineData("var orders = app.MapCrud<Order>(); orders.MapDetail<OrderItem>(); app.MapCrud<OrderItem>();", "HALDUS002", "OrderItem")]
    public void InvalidDetails_AreReported(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void AsSubresourceOnAVariable_ReportsHaldus004()
    {
        var run = Run("""
            var notes = app.MapCrud<Order>().MapDetail<OrderNote>();
            notes.AsSubresource();
            """);

        Assert.Equal(["HALDUS004"], run.GeneratorDiagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public void DetailsSnapshot()
    {
        var run = Run("""
            var orders = app.MapCrud<Order>().ConcurrencyChecked();
            orders.MapDetail<OrderItem>();
            orders.MapDetail<OrderNote>().AsSubresource();
            """).AssertCompiles();

        Snapshot.Verify(run);
    }

    private static string Section(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from, StringComparison.Ordinal);

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
