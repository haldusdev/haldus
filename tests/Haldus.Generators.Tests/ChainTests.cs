namespace Haldus.Generators.Tests;

/// <summary>Kayıtlar arası zincir: ters kayıt, yerine geçme, başka bir kayıttan üretme ve türetme.</summary>
public sealed class ChainTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum PaymentStatus { Pending, Captured }

            public partial class Entry : IReversal<Entry>
            {
                public decimal Amount { get; set; }
                public string? Memo { get; set; }
                public void Reverse(Entry original) => Amount = -original.Amount;
            }

            public partial class Plain
            {
                public decimal Amount { get; set; }
            }

            public partial class Counted
            {
                public int Id { get; set; }
                public decimal Amount { get; set; }
            }

            public partial class Booking : ISupersession<Booking>
            {
                public DateTimeOffset StartsAt { get; set; }
                public string? Note { get; set; }
                public void Supersede(Booking previous) => Note = null;
            }

            public partial class Cart
            {
                public string? Note { get; set; }
            }

            public partial class CartItem
            {
                public string Sku { get; set; } = "";
                public int Quantity { get; set; }
            }

            public partial class Payment
            {
                public decimal Amount { get; set; }
            }

            public partial class Receipt
            {
                public decimal Amount { get; set; }
                public string? Remark { get; set; }
            }

            public partial class Movement
            {
                public string Sku { get; set; } = "";
                public int Delta { get; set; }
            }

            public partial class Level
            {
                public string Sku { get; set; } = "";
                public int OnHand { get; set; }
            }

            public sealed class LevelProjection : IDerivation<Movement, Level>
            {
                public System.Threading.Tasks.Task ApplyAsync(Movement source, IDerivedSet<Level> targets, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }

            public partial class Settings
            {
                public string Theme { get; set; } = "";
            }

            public class Unmapped
            {
                public Guid Id { get; set; }
            }

            public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
        }
        """;

    private const string Carts = """
        var carts = app.MapCrud<Cart>();
        carts.MapDetail<CartItem>();
        var orders = app.MapCrud<Order>().CreatedFrom<Cart>();
        orders.MapDetail<OrderItem>();
        """;

    /// <summary>
    /// Sepetten üretilen sipariş. Sözleşmesi iki kaynağın satırlarına da başvurduğu için yalnızca satırların
    /// eşlendiği senaryolarda (<see cref="Carts"/>) derlenir; diğerlerine eklenmez.
    /// </summary>
    private const string Orders = """
        namespace Shop
        {
            public partial class Order : ICreatedFrom<Cart>
            {
                public string Customer { get; set; } = "";
                public string? Note { get; set; }

                public void CreateFrom(Cart source)
                {
                    foreach (var item in source.CartItems)
                    {
                        OrderItems.Add(new OrderItem { Sku = item.Sku, Quantity = item.Quantity });
                    }
                }
            }

            public partial class OrderItem
            {
                public string Sku { get; set; } = "";
                public int Quantity { get; set; }
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

    // ── Ters kayıt ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reversible_LinksTheTwoRecordsWithFieldsOnlyHaldusWrites()
    {
        var run = Run("app.MapAppendOnly<Entry>().Reversible();").AssertCompiles();

        var entity = run.Sources["Shop.Entry.Entity.g.cs"];
        Assert.Contains("public global::System.Guid? ReversesId { get; set; }", entity);
        Assert.Contains("public global::System.Guid? ReversedById { get; set; }", entity);

        var dtos = run.Sources["Shop.Entry.Dtos.g.cs"];
        Assert.Contains("ReversesId", Record(dtos, "EntryResponse"));
        Assert.Contains("ReversedById", Record(dtos, "EntryResponse"));
        Assert.DoesNotContain("Revers", Record(dtos, "CreateEntryRequest"));
        Assert.DoesNotContain("Revers", Record(dtos, "ReverseEntryRequest").Replace("ReverseEntryRequest", string.Empty));

        // Bir kaydın yalnızca bir ters kaydı olabilir; bunu, eşzamanlı istekler için de, benzersiz indeks sağlar.
        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("HasForeignKey(item => item.ReversesId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict);", module);
        Assert.Contains("""entity.HasIndex(item => item.ReversesId).IsUnique().HasDatabaseName("UX_Entries_ReversesId");""", module);
    }

    [Fact]
    public void Reversible_BuildsTheReversalAsACopyThatTheContractCorrects()
    {
        var endpoints = Run("app.MapAppendOnly<Entry>().Reversible();").AssertCompiles().Sources["Shop.Entry.Endpoints.g.cs"];
        Assert.Contains("""group.MapPost("/{id:guid}/reverse", ReverseAsync)""", endpoints);

        var reverse = Handler(endpoints, "ReverseAsync");
        Assert.Contains("Amount = original.Amount, Memo = original.Memo, ReversesId = original.Id", reverse);
        Assert.Contains("((global::Haldus.IReversal<Entry>)entity).Reverse(original);", reverse);
        Assert.Contains("original.ReversedById = entity.Id;", reverse);

        // Sıra: kopya, sözleşme, sonra isteğin gönderdiği alanlar.
        Assert.True(reverse.IndexOf(".Reverse(original)", StringComparison.Ordinal) < reverse.IndexOf("""IsSet("Amount")""", StringComparison.Ordinal));

        // Kapanmış bir kayıt ve bir ters kaydın kendisi ters çevrilemez; yarışı indeks yakalar.
        Assert.Contains("if (original.ReversedById is not null)", reverse);
        Assert.Contains("global::Haldus.HaldusMessageKeys.ReversedDetail", reverse);
        Assert.Contains("if (original.ReversesId is not null)", reverse);
        Assert.Contains("global::Haldus.HaldusMessageKeys.ReversalDetail", reverse);
        Assert.Contains("""IsUniqueViolation(taken, "UX_Entries_ReversesId")""", reverse);
    }

    [Fact]
    public void Reversible_WithoutAContract_IsAPlainCopy()
    {
        var reverse = Handler(Run("app.MapAppendOnly<Plain>().Reversible();").AssertCompiles().Sources["Shop.Plain.Endpoints.g.cs"], "ReverseAsync");

        Assert.Contains("Amount = original.Amount, ReversesId = original.Id", reverse);
        Assert.DoesNotContain("IReversal", reverse);
    }


    [Fact]
    public void Reversible_OnAnEditableResource_GuardsEveryWrite()
    {
        var endpoints = Run("app.MapCrud<Entry>().ConcurrencyChecked().Reversible();").AssertCompiles().Sources["Shop.Entry.Endpoints.g.cs"];

        foreach (var handler in new[] { "UpdateAsync", "PatchAsync" })
        {
            Assert.Contains("if (entity.ReversedById is not null)", Handler(endpoints, handler));

            // Ters kaydın içeriği asıl kayıttan türemiştir; o da düzenlenemez.
            Assert.Contains("if (entity.ReversesId is not null)", Handler(endpoints, handler));
            Assert.Contains("global::Haldus.HaldusMessageKeys.ReversalLockedDetail", Handler(endpoints, handler));
        }

        // Bir çiftin parçası olan kayıt silinemez; hiçbir çifte girmemiş kayıt silinebilir.
        var delete = Handler(endpoints, "DeleteAsync");
        Assert.Contains("if (entity.ReversedById is not null || entity.ReversesId is not null)", delete);
        Assert.Contains("global::Haldus.HaldusMessageKeys.ChainMemberDetail", delete);

        // Asıl kayıt da değişir; sürümü onun üzerinden denetlenir.
        var reverse = Handler(endpoints, "ReverseAsync");
        Assert.Contains("context.Entry(original).Property(item => item.Version).OriginalValue = expectedVersion;", reverse);
        Assert.Contains("DbUpdateConcurrencyException", reverse);
    }

    /// <summary>
    /// Ters çevirme ve yerine geçme, kaydın düzenlenemediği durumlarda da çalışan yazmalardır. Durum makinesi olan
    /// bir kaynakta hangi durumlarda yapılabilecekleri açıkça sayılır; sayılmazsa durum kuralları atlanmış olurdu.
    /// </summary>
    [Fact]
    public void Chains_OnAResourceWithAStateMachine_AreLimitedToTheDeclaredStates()
    {
        var run = Run("""
            var payments = app.MapCrud<Payment>()
                .Workflow<PaymentStatus>(initial: PaymentStatus.Pending)
                .EditableIn(PaymentStatus.Pending)
                .Reversible(PaymentStatus.Captured);
            payments.MapTransition("capture", PaymentStatus.Pending, PaymentStatus.Captured);
            payments.MapAction("flag", payment => { });
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Payment.Endpoints.g.cs"];

        var reverse = Handler(endpoints, "ReverseAsync");
        Assert.Contains("if (original.Status is not (global::Shop.PaymentStatus.Captured))", reverse);
        Assert.Contains("""StateConflict(messages, "reverse", original.Status)""", reverse);

        // Kapanmış kayıtta geçişler ve aksiyonlar da çalışmaz. Ters kaydın kendisi ise geçişlerden geçebilir:
        // başlangıç durumunda doğar ve ilerletilmesi gerekir.
        foreach (var handler in new[] { "TransitionCaptureAsync", "ActionFlagAsync" })
        {
            Assert.Contains("if (entity.ReversedById is not null)", Handler(endpoints, handler));
            Assert.DoesNotContain("entity.ReversesId is not null", Handler(endpoints, handler));
        }
    }

    [Theory]
    [InlineData(
        "app.MapCrud<Payment>().Workflow<PaymentStatus>(initial: PaymentStatus.Pending).Reversible();",
        "Reversible must list the states in which a record may be reversed")]
    [InlineData(
        "app.MapCrud<Payment>().Supersedes().Workflow<PaymentStatus>(initial: PaymentStatus.Pending);",
        "Supersedes must list the states in which a record may be superseded")]
    [InlineData("app.MapCrud<Plain>().Reversible(PaymentStatus.Captured);", "Reversible can list states only on a resource with a state machine")]
    public void Chains_StateListsMustMatchTheStateMachine(string body, string message) =>
        AssertSingle(Run(body), "HALDUS010", message);

    [Theory]
    [InlineData("app.MapAppendOnly<Counted>().Reversible();", "needs a key that Haldus assigns before saving")]
    [InlineData("app.MapStore<Settings>().Reversible();", "Reversible cannot be combined with MapStore")]
    [InlineData("app.MapReadOnly<Entry>().Reversible();", "Reversible cannot be combined with MapReadOnly")]
    [InlineData("app.MapCrud<Entry>().Without(CrudOperation.Create).Reversible();", "Reversible requires the Create operation")]
    [InlineData("var entries = app.MapAppendOnly<Entry>().Reversible(); entries.MapAction(\"reverse\", entry => { });", "Reversible")]
    public void Reversible_InvalidDeclarations_AreReported(string body, string message) =>
        AssertSingle(Run(body), "HALDUS007", message);

    // ── Yerine geçme ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Supersedes_ReplacesARecordAndLetsTheChainContinue()
    {
        var run = Run("""app.MapCrud<Booking>().KeyStrategy(KeyStrategy.Prefixed, "bkg").Supersedes();""").AssertCompiles();

        // İşaret alanlarının tipi anahtarın tipidir.
        var entity = run.Sources["Shop.Booking.Entity.g.cs"];
        Assert.Contains("public string? SupersedesId { get; set; }", entity);
        Assert.Contains("public string? SupersededById { get; set; }", entity);

        var endpoints = run.Sources["Shop.Booking.Endpoints.g.cs"];
        Assert.Contains("""group.MapPost("/{id}/supersede", SupersedeAsync)""", endpoints);

        var supersede = Handler(endpoints, "SupersedeAsync");
        Assert.Contains("SupersedesId = original.Id", supersede);
        Assert.Contains("((global::Haldus.ISupersession<Booking>)entity).Supersede(original);", supersede);
        Assert.Contains("original.SupersededById = entity.Id;", supersede);

        // Yerine geçen kaydın da yerine geçilebilir; yalnızca yerine geçilmiş olan kapanır.
        Assert.Contains("if (original.SupersededById is not null)", supersede);
        Assert.DoesNotContain("original.SupersedesId is not null", supersede);
        Assert.Contains("if (entity.SupersededById is not null)", Handler(endpoints, "UpdateAsync"));
    }

    [Theory]
    [InlineData("app.MapAppendOnly<Booking>().Supersedes();", "Supersedes cannot be combined with MapAppendOnly")]
    [InlineData("var bookings = app.MapCrud<Booking>().Supersedes(); bookings.MapAction(\"supersede\", booking => { });", "Supersedes")]
    public void Supersedes_InvalidDeclarations_AreReported(string body, string message) =>
        AssertSingle(Run(body), "HALDUS007", message);

    // ── Başka bir kayıttan üretme ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void CreatedFrom_KeepsALinkThatOnlyTheFromEndpointSets()
    {
        var run = Run(Carts, Orders).AssertCompiles();

        Assert.Contains("public global::System.Guid? CartId { get; set; }", run.Sources["Shop.Order.Entity.g.cs"]);

        var dtos = run.Sources["Shop.Order.Dtos.g.cs"];
        Assert.Contains("CartId", Record(dtos, "OrderResponse"));

        foreach (var request in new[] { "CreateOrderRequest", "UpdateOrderRequest", "PatchOrderRequest", "CreateOrderFromCartRequest" })
        {
            Assert.DoesNotContain("CartId", Record(dtos, request));
        }

        // Kaynak silinemez (varsayılan); bağ gerçek bir yabancı anahtardır.
        Assert.Contains(
            "entity.HasOne<global::Shop.Cart>().WithMany().HasForeignKey(item => item.CartId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict);",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void CreatedFrom_ReadsTheSourceThroughItsFiltersAndLetsTheContractFillTheRest()
    {
        var endpoints = Run(Carts, Orders).AssertCompiles().Sources["Shop.Order.Endpoints.g.cs"];
        Assert.Contains("""group.MapPost("/from-cart/{cartId:guid}", CreateFromCartAsync)""", endpoints);

        var endpoint = Handler(endpoints, "CreateFromCartAsync");
        Assert.Contains("contexts.For<global::Shop.Cart>().Set<global::Shop.Cart>().AsNoTracking().Include(item => item.CartItems)", endpoint);
        Assert.Contains("""NotFound(messages, "Cart", cartId)""", endpoint);

        // Satırlar sözleşmeye kendi sıralarıyla verilir.
        Assert.Contains("source.CartItems.Sort(static (left, right) => left.Position.CompareTo(right.Position));", endpoint);

        // Aynı adlı alan (Note) kendiliğinden kopyalanır; adı farklı olanlar ve satırlar sözleşmenin işidir.
        var builder = Handler(endpoints, "FromCartAsync");
        Assert.Contains("public static async", endpoints[..endpoints.IndexOf(" FromCartAsync(\n", StringComparison.Ordinal)][^200..]);
        Assert.Contains("Note = source.Note, CartId = source.Id", builder);
        Assert.DoesNotContain("Customer = source", builder);
        Assert.Contains("((global::Haldus.ICreatedFrom<global::Shop.Cart>)entity).CreateFrom(source);", builder);

        // Sözleşmenin eklediği satırlara kimlik ve sıra verilir.
        Assert.Contains("row.Id = global::System.Guid.CreateVersion7();", builder);
        Assert.Contains("row.Position = orderItemsIndex++;", builder);
    }

    [Fact]
    public void CreatedFrom_OnDeleteCanBeChosen()
    {
        var module = Run("""
            app.MapCrud<Cart>();
            app.MapCrud<Receipt>().CreatedFrom<Cart>(onDelete: OnDelete.SetNull);
            """).AssertCompiles().Sources["HaldusModule.g.cs"];

        Assert.Contains("HasForeignKey(item => item.CartId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.SetNull);", module);
    }

    [Fact]
    public void CreatedFrom_OnATransition_IsCreatedByItAndHasNoEndpointOfItsOwn()
    {
        var run = Run("""
            var payments = app.MapCrud<Payment>().Workflow<PaymentStatus>(initial: PaymentStatus.Pending);
            payments.MapTransition("capture", PaymentStatus.Pending, PaymentStatus.Captured);
            app.MapAppendOnly<Receipt>().Without(CrudOperation.Create).CreatedFrom<Payment>(on: "capture");
            """).AssertCompiles();

        var capture = Handler(run.Sources["Shop.Payment.Endpoints.g.cs"], "TransitionCaptureAsync");
        Assert.Contains("await global::Shop.ReceiptEndpoints.FromPaymentAsync(", capture);
        Assert.Contains("new global::Shop.CreateReceiptFromPaymentRequest()", capture);
        Assert.Contains("contexts.For<global::Shop.Receipt>().Set<global::Shop.Receipt>().Add(createdReceipt.Record!);", capture);

        // Kayıt, geçişle aynı kaydetmede kalıcı olur.
        Assert.True(capture.IndexOf("createdReceipt.Record!", StringComparison.Ordinal) < capture.IndexOf("SaveChangesAsync", StringComparison.Ordinal));

        // Açık bir uç, geçişin koşullarını atlayarak aynı kaydı üretmenin yolu olurdu.
        var receipts = run.Sources["Shop.Receipt.Endpoints.g.cs"];
        Assert.DoesNotContain("from-payment", receipts);
        Assert.DoesNotContain("MapPost", receipts);
        Assert.Contains("Amount = source.Amount, PaymentId = source.Id", Handler(receipts, "FromPaymentAsync"));
    }

    [Theory]
    [InlineData("app.MapCrud<Receipt>().CreatedFrom<Unmapped>();", "HALDUS007", "the source must be mapped with a Map call")]
    [InlineData("app.MapCrud<Cart>(); app.MapStore<Settings>().CreatedFrom<Cart>();", "HALDUS007", "CreatedFrom cannot be combined with MapStore")]
    [InlineData("app.MapCrud<Cart>(); app.MapCrud<Receipt>().Under<Cart>().CreatedFrom<Cart>();", "HALDUS007", "it cannot also be created from it")]
    [InlineData("app.MapCrud<Cart>(); app.MapCrud<Receipt>().References<Cart>().CreatedFrom<Cart>();", "HALDUS007", "it cannot also be created from it")]
    [InlineData("app.MapCrud<Payment>(); app.MapCrud<Receipt>().CreatedFrom<Payment>(on: \"capture\");", "HALDUS007", "has no transition or action named 'capture'")]
    [InlineData(
        "app.MapCrud<Cart>(); app.MapCrud<Receipt>().Without(CrudOperation.Create).CreatedFrom<Cart>();",
        "HALDUS007",
        "CreatedFrom adds an endpoint that creates records, but the Create operation has been removed")]
    [InlineData(
        "var name = \"capture\"; app.MapCrud<Payment>(); app.MapCrud<Receipt>().CreatedFrom<Payment>(on: name);",
        "HALDUS010",
        "must be written as a string literal")]
    [InlineData(
        """
        var payments = app.MapCrud<Payment>();
        payments.MapAction("capture", payment => { });
        app.MapCrud<Receipt>().Numbered("RC-{seq:6}").CreatedFrom<Payment>(on: "capture");
        """,
        "HALDUS007",
        "takes a number or a position when it is created")]
    public void CreatedFrom_InvalidDeclarations_AreReported(string body, string id, string message) =>
        AssertSingle(Run(body), id, message);

    // ── Türetme ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DerivedFrom_CallsTheProjectionWithEverySourceUnderALock()
    {
        var run = Run("""
            app.MapAppendOnly<Movement>().Reversible().Batch();
            app.MapReadOnly<Level>().DerivedFrom<Movement>();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Movement.Endpoints.g.cs"];

        // Kaynak nasıl oluşursa oluşsun (tekil, toplu, ters kayıt) izdüşüm çağrılır ve kayıtla birlikte kaydedilir.
        foreach (var handler in new[] { "CreateAsync", "BatchCreateItemAsync", "ReverseAsync" })
        {
            var create = Handler(endpoints, handler);

            Assert.Contains("""await using var derived = await global::Haldus.HaldusDerivations.BeginAsync(contexts.For<Movement>(), "Movements", cancellationToken);""", create);
            Assert.Contains("await global::Haldus.HaldusDerivations.ApplyAsync<Movement, global::Shop.Level>(contexts.For<Movement>(), entity, contexts, cancellationToken);", create);
            Assert.True(create.IndexOf("ApplyAsync<Movement", StringComparison.Ordinal) < create.IndexOf("SaveChangesAsync", StringComparison.Ordinal));
            Assert.True(create.IndexOf("SaveChangesAsync", StringComparison.Ordinal) < create.IndexOf("derived.CommitAsync", StringComparison.Ordinal));
        }

        // İzdüşüm sınıfı bulunur ve kaydedilir.
        Assert.Contains(
            "TryAddScoped<global::Haldus.IDerivation<global::Shop.Movement, global::Shop.Level>, global::Shop.LevelProjection>(services);",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapAppendOnly<Movement>(); app.MapCrud<Level>().DerivedFrom<Movement>();", "HALDUS007", "DerivedFrom cannot be combined with MapCrud")]
    [InlineData("app.MapCrud<Movement>(); app.MapReadOnly<Level>().DerivedFrom<Movement>();", "HALDUS007", "can only be derived from an append-only resource")]
    [InlineData("app.MapReadOnly<Level>().DerivedFrom<Unmapped>();", "HALDUS009", "Unmapped")]
    public void DerivedFrom_InvalidDeclarations_AreReported(string body, string id, string message) =>
        AssertSingle(Run(body), id, message);

    [Fact]
    public void ChainsSnapshot()
    {
        var run = Run("""
            app.MapAppendOnly<Entry>().Reversible();
            app.MapCrud<Booking>().Supersedes();
            var carts = app.MapCrud<Cart>();
            carts.MapDetail<CartItem>();
            var orders = app.MapCrud<Order>().CreatedFrom<Cart>();
            orders.MapDetail<OrderItem>();
            app.MapAppendOnly<Movement>();
            app.MapReadOnly<Level>().DerivedFrom<Movement>();
            """, Orders).AssertCompiles();

        Snapshot.Verify(run);
    }
}
