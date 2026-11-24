namespace Haldus.Generators.Tests;

public sealed class WorkflowTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum OrderStatus { Draft, Placed, Shipped, Cancelled }

            public enum PaymentStatus { Pending, Paid }

            public partial class Order
            {
                public string CustomerName { get; set; } = "";

                public string? CancellationReason { get; set; }
            }

            public partial class OrderNote
            {
                public string Text { get; set; } = "";
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

            public sealed class OrderFilter : IFilter<Order>
            {
                public OrderStatus? Status { get; set; }

                public System.Linq.IQueryable<Order> Apply(System.Linq.IQueryable<Order> query) => query;
            }

            public sealed record PlaceOrder
            {
                [MaxLength(500)]
                public string? Note { get; init; }
            }

            public sealed class PlaceOrderHandler : ITransitionHandler<Order, PlaceOrder>
            {
                public System.Threading.Tasks.Task HandleAsync(Order entity, PlaceOrder request, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }

            public sealed record CancelOrder(string Reason) : ITransition<Order>
            {
                public void Apply(Order entity) => entity.CancellationReason = Reason;
            }

            public sealed record ReopenOrder;

            public sealed record OrderApprovalRequest;
        }
        """;

    private const string Workflow = "app.MapCrud<Order>().Workflow<OrderStatus>(initial: OrderStatus.Draft)";

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
    public void Workflow_AddsAStatusThatRequestsCannotWrite()
    {
        var run = Run($"{Workflow};").AssertCompiles();

        Assert.Contains("public global::Shop.OrderStatus Status { get; set; }", run.Sources["Shop.Order.Entity.g.cs"]);

        var dtos = run.Sources["Shop.Order.Dtos.g.cs"];

        // Durum yalnızca yanıtta yer alır ve adıyla yazılır.
        Assert.Equal(1, CountOccurrences(dtos, " Status "));
        Assert.Contains(
            "    [global::System.Text.Json.Serialization.JsonConverter(typeof(global::System.Text.Json.Serialization.JsonStringEnumConverter<global::Shop.OrderStatus>))]\n"
            + "    public required global::Shop.OrderStatus Status { get; init; }",
            dtos);

        Assert.Contains("Status = global::Shop.OrderStatus.Draft, CustomerName = request.CustomerName", run.Sources["Shop.Order.Endpoints.g.cs"]);
        Assert.Contains(
            "entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(64);",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Workflow_WithoutAnInitialState_StartsAtTheZeroMember()
    {
        var run = Run("app.MapCrud<Order>().Workflow<OrderStatus>();").AssertCompiles();

        Assert.Contains("Status = global::Shop.OrderStatus.Draft", run.Sources["Shop.Order.Endpoints.g.cs"]);
    }

    [Fact]
    public void Workflow_ADeclaredStatusPropertyIsReusedAndStaysOutOfTheRequests()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop
            {
                public enum TicketStatus { Open, Closed }

                public partial class Ticket
                {
                    public string Title { get; set; } = "";

                    public TicketStatus Status { get; set; }
                }

                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Ticket>().Workflow<TicketStatus>();
                }
            }
            """).AssertCompiles();

        Assert.DoesNotContain("Status", run.Sources["Shop.Ticket.Entity.g.cs"]);
        Assert.Equal(1, CountOccurrences(run.Sources["Shop.Ticket.Dtos.g.cs"], " Status "));
    }

    [Fact]
    public void StateRules_GuardUpdatesAndDeletes()
    {
        var run = Run($"{Workflow}.EditableIn(OrderStatus.Draft).DeletableIn(OrderStatus.Draft, OrderStatus.Cancelled);").AssertCompiles();

        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];

        // PUT ve PATCH.
        Assert.Equal(2, CountOccurrences(endpoints, "StateConflict(messages, \"update\", entity.Status)"));
        Assert.Equal(2, CountOccurrences(endpoints, "if (entity.Status is not (global::Shop.OrderStatus.Draft))"));
        Assert.Contains(
            "if (entity.Status is not (global::Shop.OrderStatus.Draft or global::Shop.OrderStatus.Cancelled))\n"
            + "        {\n"
            + "            return global::Haldus.HaldusProblems.StateConflict(messages, \"delete\", entity.Status);",
            endpoints);
    }

    [Fact]
    public void StateRules_AreOptional()
    {
        var run = Run($"{Workflow};").AssertCompiles();

        Assert.DoesNotContain("StateConflict", run.Sources["Shop.Order.Endpoints.g.cs"]);
    }

    [Fact]
    public void Transition_WithoutABody_ChecksTheStateAndMovesTheRecord()
    {
        var run = Run($"""
            var orders = {Workflow};
            orders.MapTransition("ship", from: OrderStatus.Placed, to: OrderStatus.Shipped);
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];

        Assert.Contains("public static global::Microsoft.AspNetCore.Builder.RouteHandlerBuilder MapShipTransition(", endpoints);
        Assert.Contains("return group.MapPost(\"/{id:guid}/ship\", (", endpoints);
        Assert.Contains(".WithName(\"ShipOrder\")", endpoints);
        Assert.Contains("StateConflict(messages, \"ship\", entity.Status)", endpoints);
        Assert.Contains("entity.Status = global::Shop.OrderStatus.Shipped;", endpoints);

        // Geçiş, çağrısı çalıştığında bağlanır; standart uçlarla birlikte kendiliğinden eşlenmez.
        var map = Section(endpoints, "RouteGroupBuilder Map(", "return group;");
        Assert.DoesNotContain("ship", map);

        Assert.Contains(
            "global::Haldus.HaldusTransitionRegistry.Register<global::Shop.Order>(\"ship\", global::Shop.OrderEndpoints.MapShipTransition);",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Transition_TakesItsLogicFromTheRequestALambdaOrAHandler()
    {
        var run = Run($"""
            var orders = {Workflow};
            orders.MapTransition<PlaceOrder>(from: OrderStatus.Draft, to: OrderStatus.Placed);
            orders.MapTransition<CancelOrder>(from: [OrderStatus.Draft, OrderStatus.Placed], to: OrderStatus.Cancelled);
            orders.MapTransition<ReopenOrder>(OrderStatus.Cancelled, OrderStatus.Draft, (order, request) => order.CancellationReason = null);
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];
        var place = Section(endpoints, "> TransitionPlaceAsync(", "> TransitionCancelAsync(");
        var cancel = Section(endpoints, "> TransitionCancelAsync(", "> TransitionReopenAsync(");

        // İstek kendi etkisini uygulamıyorsa lambda ya da kayıtlı handler çalışır.
        Assert.DoesNotContain("ITransition<Order>", place);
        Assert.Contains("await apply(entity, request, httpContext.RequestServices, cancellationToken);", place);
        Assert.Contains(".GetService<global::Haldus.ITransitionHandler<Order, global::Shop.PlaceOrder>>(httpContext.RequestServices) is { } handler)", place);
        Assert.Contains("TransitionRejected(messages, rejection.Message)", place);

        Assert.Contains("((global::Haldus.ITransition<Order>)request).Apply(entity);", cancel);
        Assert.Contains("if (entity.Status is not (global::Shop.OrderStatus.Draft or global::Shop.OrderStatus.Placed))", cancel);

        // İş mantığı istek tipinde değilse, lambda da verilmediğinde handler eksikliği açılışta fark edilir.
        Assert.Contains("global::Haldus.HaldusTransitionRegistry.RequireHandler<Order, global::Shop.PlaceOrder>(group);", endpoints);
        Assert.Contains("global::Haldus.HaldusTransitionRegistry.RequireHandler<Order, global::Shop.ReopenOrder>(group);", endpoints);
        Assert.DoesNotContain("RequireHandler<Order, global::Shop.CancelOrder>", endpoints);

        // İstek gövdesi doğrulanır.
        Assert.Contains(".AddEndpointFilter<global::Haldus.HaldusValidationFilter<global::Shop.PlaceOrder>>()", endpoints);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains(
            "global::Haldus.HaldusTransitionRegistry.Register<global::Shop.Order, global::Shop.CancelOrder>(null, global::Shop.OrderEndpoints.MapCancelTransition);",
            module);
        Assert.Contains(
            "TryAddScoped<global::Haldus.ITransitionHandler<global::Shop.Order, global::Shop.PlaceOrder>, global::Shop.PlaceOrderHandler>(services);",
            module);
    }

    [Theory]
    [InlineData("orders.MapTransition<CancelOrder>(OrderStatus.Draft, OrderStatus.Cancelled);", "/{id:guid}/cancel", "CancelOrder", "null")]
    [InlineData("orders.MapTransition<OrderApprovalRequest>(OrderStatus.Draft, OrderStatus.Placed, (order, request) => { });", "/{id:guid}/approval", "ApprovalOrder", "null")]
    [InlineData("orders.MapTransition<CancelOrder>(OrderStatus.Draft, OrderStatus.Cancelled, name: \"void\");", "/{id:guid}/void", "VoidOrder", "\"void\"")]
    [InlineData("orders.MapTransition(\"mark-paid\", OrderStatus.Placed, OrderStatus.Shipped, order => { });", "/{id:guid}/mark-paid", "MarkPaidOrder", "\"mark-paid\"")]
    public void Transition_RouteComesFromTheRequestTypeOrTheGivenName(string call, string route, string operation, string registryName)
    {
        var run = Run($"var orders = {Workflow}; {call}").AssertCompiles();

        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];
        Assert.Contains($"group.MapPost(\"{route}\", (", endpoints);
        Assert.Contains($".WithName(\"{operation}\")", endpoints);
        Assert.Contains($">({registryName}, global::Shop.OrderEndpoints.Map", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Transition_OnAConcurrencyCheckedResourceUnderAParent_ChecksBoth()
    {
        var run = Run("""
            app.MapCrud<Project>();
            var items = app.MapCrud<WorkItem>().Under<Project>().ConcurrencyChecked().Workflow<OrderStatus>();
            items.MapTransition("ship", OrderStatus.Draft, OrderStatus.Shipped);
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.WorkItem.Endpoints.g.cs"];
        var ship = endpoints[endpoints.LastIndexOf("> TransitionShipAsync(", StringComparison.Ordinal)..];

        Assert.Contains("TransitionShipAsync(projectId, id, apply, httpContext, contexts, messages, cancellationToken))", endpoints);
        Assert.Contains("item => item.Id == id && item.ProjectId == projectId", ship);

        // Durum değiştiren her uç gibi geçiş de If-Match ister.
        Assert.Contains("global::Haldus.HaldusETag.ReadIfMatch(httpContext)", ship);
        Assert.Contains("Property(item => item.Version).OriginalValue = expectedVersion;", ship);
        Assert.Contains("catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)", ship);
    }

    [Fact]
    public void Transition_OnAReadOnlyResource_IsItsOnlyWrite()
    {
        var run = Run("""
            var orders = app.MapReadOnly<Order>().Workflow<OrderStatus>();
            orders.MapTransition("ship", OrderStatus.Draft, OrderStatus.Shipped);
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Order.Endpoints.g.cs"];
        Assert.Contains("MapShipTransition(", endpoints);
        Assert.DoesNotContain("CreateAsync", endpoints);
        Assert.DoesNotContain("DeleteAsync", endpoints);
    }

    [Fact]
    public void List_CanBeFilteredByStateUnlessTheUsersFilterAlreadyDoes()
    {
        var plain = Run($"{Workflow};").AssertCompiles();
        var filtered = Run($"{Workflow}.Filterable<OrderFilter>();").AssertCompiles();

        var endpoints = plain.Sources["Shop.Order.Endpoints.g.cs"];
        Assert.Contains("        global::Shop.OrderStatus? status,\n", endpoints);
        Assert.Contains("query = query.Where(item => item.Status == statusValue);", endpoints);

        Assert.DoesNotContain("statusValue);", filtered.Sources["Shop.Order.Endpoints.g.cs"]);
    }

    [Fact]
    public void Status_CanBeSearched()
    {
        var run = Run($"{Workflow}.Searchable(order => order.Status);").AssertCompiles();

        Assert.Contains(
            "public global::Haldus.EqualityCondition<global::Shop.OrderStatus>? Status { get; init; }",
            run.Sources["Shop.Order.Dtos.g.cs"]);
    }

    [Fact]
    public void SubresourceDetail_IsWritableOnlyWhileItsOwnerIsEditable()
    {
        var guarded = Run($"{Workflow}.EditableIn(OrderStatus.Draft).MapDetail<OrderNote>().AsSubresource();").AssertCompiles();
        var free = Run($"{Workflow}.MapDetail<OrderNote>().AsSubresource();").AssertCompiles();

        var endpoints = guarded.Sources["Shop.OrderNote.Endpoints.g.cs"];

        // Oluşturma, PUT, PATCH ve silme sahibin durumuna bakar; okuma bakmaz.
        Assert.Equal(4, CountOccurrences(endpoints, ".Select(parent => (global::Shop.OrderStatus?)parent.Status)"));
        Assert.Equal(4, CountOccurrences(endpoints, "if (ownerStatus is not (global::Shop.OrderStatus.Draft))"));
        Assert.Contains("StateConflict(messages, \"create\", ownerStatus)", endpoints);
        Assert.Equal(2, CountOccurrences(endpoints, "StateConflict(messages, \"update\", ownerStatus)"));
        Assert.Contains("StateConflict(messages, \"delete\", ownerStatus)", endpoints);

        Assert.DoesNotContain("ownerStatus", free.Sources["Shop.OrderNote.Endpoints.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Order>().Workflow<int>();", "HALDUS010", "must be an enum")]
    [InlineData("var initial = OrderStatus.Draft; app.MapCrud<Order>().Workflow<OrderStatus>(initial);", "HALDUS010", "initial state must be written as an enum member")]
    [InlineData("app.MapCrud<Order>().EditableIn(OrderStatus.Draft);", "HALDUS007", "need a state machine")]
    [InlineData("app.MapCrud<Order>().Workflow<OrderStatus>().EditableIn(PaymentStatus.Paid);", "HALDUS010", "'Paid' is not a member of the status type")]
    [InlineData("app.MapCrud<Order>().Workflow<OrderStatus>().DeletableIn();", "HALDUS010", "DeletableIn needs at least one state")]
    [InlineData("app.MapStore<Settings>().Workflow<OrderStatus>();", "HALDUS007", "Workflow cannot be combined with MapStore")]
    [InlineData("app.MapSingleton<Settings>().Workflow<OrderStatus>();", "HALDUS007", "Workflow cannot be combined with MapSingleton")]
    [InlineData("app.MapCrud<Order>().MapTransition(\"ship\", OrderStatus.Draft, OrderStatus.Shipped);", "HALDUS007", "MapTransition needs a state machine")]
    [InlineData("app.MapCrud<Order>().Workflow<OrderStatus>().MapTransition(\"pay\", PaymentStatus.Pending, PaymentStatus.Paid);", "HALDUS010", "'Pending' is not a state of 'Order'")]
    [InlineData("app.MapCrud<Order>().Workflow<OrderStatus>().MapTransition(\"Ship It\", OrderStatus.Draft, OrderStatus.Shipped);", "HALDUS010", "not a valid transition name")]
    [InlineData("var name = \"ship\"; app.MapCrud<Order>().Workflow<OrderStatus>().MapTransition(name, OrderStatus.Draft, OrderStatus.Shipped);", "HALDUS010", "must be a constant")]
    [InlineData("var to = OrderStatus.Shipped; app.MapCrud<Order>().Workflow<OrderStatus>().MapTransition(\"ship\", OrderStatus.Draft, to);", "HALDUS010", "must be written as enum members")]
    [InlineData("var orders = app.MapCrud<Order>().Workflow<OrderStatus>(); orders.MapTransition(\"ship\", OrderStatus.Draft, OrderStatus.Shipped); orders.MapTransition(\"ship\", OrderStatus.Placed, OrderStatus.Shipped);", "HALDUS007", "already has a transition named 'ship'")]
    [InlineData("app.MapCrud<Order>().Archivable().Workflow<OrderStatus>().MapTransition(\"archive\", OrderStatus.Draft, OrderStatus.Shipped);", "HALDUS007", "collides with the 'archive' endpoint that Archivable generates")]
    public void InvalidWorkflows_AreReported(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void WorkflowDeclarationsOnAVariable_ReportHaldus004()
    {
        var run = Run("""
            var orders = app.MapCrud<Order>();
            orders.Workflow<OrderStatus>();
            orders.EditableIn(OrderStatus.Draft);
            """);

        Assert.Equal(["HALDUS004", "HALDUS004"], run.GeneratorDiagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public void WorkflowSnapshot()
    {
        var run = Run("""
            var orders = app.MapCrud<Order>()
                .ConcurrencyChecked()
                .Workflow<OrderStatus>(initial: OrderStatus.Draft)
                .EditableIn(OrderStatus.Draft)
                .DeletableIn(OrderStatus.Draft, OrderStatus.Cancelled);

            orders.MapDetail<OrderNote>().AsSubresource();
            orders.MapTransition<PlaceOrder>(from: OrderStatus.Draft, to: OrderStatus.Placed);
            orders.MapTransition("ship", from: OrderStatus.Placed, to: OrderStatus.Shipped);
            orders.MapTransition<CancelOrder>(from: [OrderStatus.Draft, OrderStatus.Placed], to: OrderStatus.Cancelled);
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
