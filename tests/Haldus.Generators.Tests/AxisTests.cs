namespace Haldus.Generators.Tests;

/// <summary>Asıl durumdan bağımsız ek durum eksenleri (<c>Axis</c>).</summary>
public sealed class AxisTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum OrderStatus { Draft, Placed, Cancelled }

            public enum PaymentState { Unpaid, Paid, Refunded }

            public enum ShipmentState { Pending = 1, Shipped = 2 }

            public partial class Order
            {
                public string CustomerName { get; set; } = "";
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
    public void Axis_AddsAStateFieldOfItsOwn()
    {
        var run = Run("""
            var orders = app.MapCrud<Order>()
                .Workflow<OrderStatus>().EditableIn(OrderStatus.Draft)
                .Axis<PaymentState>()
                .Axis<ShipmentState>(initial: ShipmentState.Pending);
            orders.MapTransition("place", OrderStatus.Draft, OrderStatus.Placed);
            orders.MapTransition("mark-paid", PaymentState.Unpaid, PaymentState.Paid);
            orders.MapTransition("ship", ShipmentState.Pending, ShipmentState.Shipped);
            """).AssertCompiles();

        var entity = run.Sources["Shop.Order.Entity.g.cs"];
        Assert.Contains("public global::Shop.PaymentState PaymentState { get; set; }", entity);
        Assert.Contains("public global::Shop.ShipmentState ShipmentState { get; set; }", entity);

        // Eksen yanıtta adıyla görünür; istekle yazılamaz.
        var dtos = run.Sources["Shop.Order.Dtos.g.cs"];
        Assert.Contains("JsonStringEnumConverter<global::Shop.PaymentState>))]\n    public required global::Shop.PaymentState PaymentState { get; init; }", dtos);
        Assert.DoesNotContain("PaymentState", dtos.Substring(dtos.IndexOf("record CreateOrderRequest", StringComparison.Ordinal)));

        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];

        // Yeni kayıt her eksende başlangıç durumunda doğar.
        var create = Handler(endpoints, "CreateAsync");
        Assert.Contains("Status = global::Shop.OrderStatus.Draft", create);
        Assert.Contains("PaymentState = global::Shop.PaymentState.Unpaid", create);
        Assert.Contains("ShipmentState = global::Shop.ShipmentState.Pending", create);

        // Geçiş, durumlarının tipinden bulunan eksene bakar ve yalnızca onu yazar.
        var paid = Handler(endpoints, "TransitionMarkPaidAsync");
        Assert.Contains("if (entity.PaymentState is not (global::Shop.PaymentState.Unpaid))", paid);
        Assert.Contains("StateConflict(messages, \"mark-paid\", entity.PaymentState)", paid);
        Assert.Contains("entity.PaymentState = global::Shop.PaymentState.Paid;", paid);
        Assert.DoesNotContain("entity.Status", paid);

        var placed = Handler(endpoints, "TransitionPlaceAsync");
        Assert.Contains("entity.Status = global::Shop.OrderStatus.Placed;", placed);
        Assert.DoesNotContain("PaymentState", placed);

        // Düzenleme kuralı yalnızca asıl duruma bakar.
        var update = Handler(endpoints, "UpdateAsync");
        Assert.Contains("entity.Status is not (global::Shop.OrderStatus.Draft)", update);
        Assert.DoesNotContain("PaymentState", update);

        // Liste her eksene göre süzülür.
        var list = Handler(endpoints, "ListAsync");
        Assert.Contains("global::Shop.PaymentState? paymentState,", list);
        Assert.Contains("query = query.Where(item => item.PaymentState == paymentStateValue);", list);

        // Eksen de adıyla saklanır ve eşzamanlılık belirtecidir: aynı geçişi birlikte isteyen iki istekten biri geçer.
        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.Property(item => item.PaymentState).HasConversion<string>().HasMaxLength(64);", module);
        Assert.Contains("entity.Property(item => item.PaymentState).IsConcurrencyToken();", module);
        Assert.Contains("StateChanged(messages)", paid);
    }

    [Fact]
    public void Axis_WorksWithoutAMainStatus()
    {
        var run = Run("""
            var orders = app.MapCrud<Order>().ConcurrencyChecked().Axis<PaymentState>();
            orders.MapTransition("mark-paid", PaymentState.Unpaid, PaymentState.Paid);
            """).AssertCompiles();

        var entity = run.Sources["Shop.Order.Entity.g.cs"];
        Assert.DoesNotContain(" Status ", entity);

        // Sürüm kontrollü kaynakta aynı işi satır sürümü görür.
        Assert.DoesNotContain("IsConcurrencyToken", run.Sources["HaldusModule.g.cs"]);
        Assert.Contains("global::Haldus.HaldusETag.ReadIfMatch(httpContext)", Handler(run.Sources["Shop.Order.Endpoints.g.cs"], "TransitionMarkPaidAsync"));
    }

    [Fact]
    public void Axis_DoesNotMoveTheNumbering()
    {
        // Numara asıl durumun bir geçişinde atanır; aynı adlı bir üyeye giden eksen geçişi numara ayırmaz.
        var run = Run("""
            var orders = app.MapCrud<Order>().Workflow<OrderStatus>().Axis<PaymentState>()
                .Numbered("ORD-{seq:6}", assignOn: OrderStatus.Placed);
            orders.MapTransition("place", OrderStatus.Draft, OrderStatus.Placed);
            orders.MapTransition("mark-paid", PaymentState.Unpaid, PaymentState.Paid);
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];
        Assert.Contains("HaldusNumbering.AllocateAsync", Handler(endpoints, "TransitionPlaceAsync"));
        Assert.DoesNotContain("HaldusNumbering", Handler(endpoints, "TransitionMarkPaidAsync"));
    }

    [Theory]
    [InlineData("app.MapCrud<Order>().Workflow<OrderStatus>().Axis<OrderStatus>();", "HALDUS007", "repeats the status of 'Order'")]
    [InlineData("app.MapCrud<Order>().Axis<PaymentState>().Axis<PaymentState>();", "HALDUS010", "already has an axis of 'PaymentState'")]
    [InlineData("app.MapCrud<Order>().Axis<int>();", "HALDUS010", "must be an enum")]
    [InlineData("app.MapCrud<Order>().Axis<ShipmentState>();", "HALDUS010", "has no member with the value 0")]
    [InlineData("app.MapStore<Order>().Axis<PaymentState>();", "HALDUS007", "Axis")]
    [InlineData("app.MapCrud<Order>().Publishable().Axis<PaymentState>();", "HALDUS007", "Publishable cannot be combined with Axis")]
    [InlineData("var orders = app.MapCrud<Order>().Workflow<OrderStatus>(); orders.MapTransition(\"mark-paid\", PaymentState.Unpaid, PaymentState.Paid);", "HALDUS010", "'Unpaid' is not a state of 'Order'")]
    [InlineData("var orders = app.MapCrud<Order>().Workflow<OrderStatus>().Axis<PaymentState>(); orders.MapTransition(\"mix\", OrderStatus.Draft, PaymentState.Paid);", "HALDUS010", "belong to different status types")]
    [InlineData("var orders = app.MapCrud<Order>(); orders.MapTransition(\"mark-paid\", PaymentState.Unpaid, PaymentState.Paid);", "HALDUS007", "MapTransition needs a state machine")]
    public void Axis_RejectsDeclarationsThatCannotWork(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void AxisSnapshot()
    {
        var run = Run("""
            var orders = app.MapCrud<Order>().Workflow<OrderStatus>().Axis<PaymentState>();
            orders.MapTransition("place", OrderStatus.Draft, OrderStatus.Placed);
            orders.MapTransition("mark-paid", PaymentState.Unpaid, PaymentState.Paid);
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
