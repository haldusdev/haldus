namespace Haldus.Generators.Tests;

/// <summary>
/// Araştırma raporundaki alan sınama tablosunun (59 varlık) bugünkü API ile sınanması. Her satır, raporun
/// önerdiği eşlemeye en yakın biçimde, yalnızca yazılmış özelliklerle ifade edilir ve derlenir. Sonuçların
/// yorumu <c>reports/Haldus taksonomi sınaması.md</c> dosyasındadır; buradaki sayılar oradakilerle aynıdır.
/// </summary>
public sealed class TaxonomyTests
{
    public enum Fit
    {
        /// <summary>Raporun önerdiği eşleme, yazılmış özelliklerle eksiksiz ifade ediliyor.</summary>
        Clean,

        /// <summary>Çekirdek ifade ediliyor; önerilen eşlemedeki bir veya daha çok özellik henüz yok.</summary>
        Partial,

        /// <summary>Gereken archetype veya trait yok; anlamlı bir eşleme yazılamıyor.</summary>
        Missing,
    }

    public sealed record Row(int Number, string Entity, Fit Fit, string? Declarations = null, string? Map = null);

    private const string Order = """
        public partial class Order { public string CustomerName { get; set; } = ""; }
        """;

    private const string Invoice = """
        public partial class Invoice { public string Customer { get; set; } = ""; }
        """;

    private const string User = """
        public partial class User { public string Email { get; set; } = ""; }
        """;

    private const string Product = """
        public partial class Product { public string Name { get; set; } = ""; public decimal Price { get; set; } }
        """;

    private const string Webhook = """
        public partial class Webhook { public string Url { get; set; } = ""; }
        """;

    private const string Job = "(job, context, cancellationToken) => System.Threading.Tasks.Task.CompletedTask";

    public static readonly Row[] Rows =
    [
        // ── E-ticaret ────────────────────────────────────────────────────────────────────────────────
        new(1, "Order", Fit.Partial,
            """
            public enum OrderStatus { Draft, Placed, Shipped, Cancelled }
            public partial class Order : IComputed
            {
                public string CustomerName { get; set; } = "";
                public decimal Total { get; private set; }
                public void Compute() => Total = OrderItems.Sum(item => item.Quantity * item.UnitPrice);
            }
            public partial class OrderItem { public int Quantity { get; set; } public decimal UnitPrice { get; set; } }
            public partial class Cart { public string CustomerName { get; set; } = ""; }
            public sealed record CancelOrder(string Reason) : ITransition<Order> { public void Apply(Order entity) { } }
            """,
            """
            app.MapCrud<Cart>();
            var orders = app.MapCrud<Order>().MultiTenant().Auditable(trackUser: true).ConcurrencyChecked()
                .Workflow<OrderStatus>(initial: OrderStatus.Draft).EditableIn(OrderStatus.Draft).DeletableIn(OrderStatus.Draft)
                .Numbered("ORD-{yyyy}-{seq:6}", assignOn: OrderStatus.Placed).Idempotent().CreatedFrom<Cart>();
            orders.MapDetail<OrderItem>();
            orders.MapTransition("place", OrderStatus.Draft, OrderStatus.Placed);
            orders.MapTransition("ship", OrderStatus.Placed, OrderStatus.Shipped);
            orders.MapTransition<CancelOrder>([OrderStatus.Draft, OrderStatus.Placed], OrderStatus.Cancelled);
            """),
        new(2, "OrderItem / LineItem", Fit.Clean,
            """
            public partial class Order : IComputed
            {
                public decimal Total { get; private set; }
                public void Compute() => Total = OrderItems.Sum(item => item.Quantity * item.UnitPrice);
            }
            public partial class OrderItem { [Range(1, 1000)] public int Quantity { get; set; } public decimal UnitPrice { get; set; } }
            """,
            "app.MapCrud<Order>().MapDetail<OrderItem>();"),
        new(3, "Product + Variant + Option", Fit.Partial,
            Product + """
            public partial class ProductVariant { public string Sku { get; set; } = ""; public decimal Price { get; set; } }
            """,
            """
            var products = app.MapCrud<Product>().SoftDeletable().Auditable().Batch().Publishable();
            products.MapDetail<ProductVariant>();
            """),
        new(4, "Category / Collection", Fit.Clean,
            "public partial class Category { public string Name { get; set; } = \"\"; }",
            "app.MapCrud<Category>().Hierarchical().Archivable().Orderable();"),
        new(5, "Cart", Fit.Partial,
            """
            public partial class Cart { public string? Note { get; set; } }
            public partial class CartItem { public string Sku { get; set; } = ""; public int Quantity { get; set; } }
            """,
            """
            var carts = app.MapCrud<Cart>().OwnedByCurrentUser().Expiring(TimeSpan.FromDays(7), sliding: true).Cloneable();
            carts.MapDetail<CartItem>();
            """),
        new(6, "Checkout Session", Fit.Clean,
            """
            public enum CheckoutStatus { Open, Completed }
            public partial class CheckoutSession { public decimal Amount { get; set; } }
            public partial class Purchase { public decimal Amount { get; set; } }
            """,
            """
            var sessions = app.MapCrud<CheckoutSession>().Without(CrudOperation.Delete)
                .Workflow<CheckoutStatus>().EditableIn(CheckoutStatus.Open).Expiring(TimeSpan.FromMinutes(30)).Idempotent();
            sessions.MapTransition("complete", CheckoutStatus.Open, CheckoutStatus.Completed);
            app.MapCrud<Purchase>().Without(CrudOperation.Create).CreatedFrom<CheckoutSession>(on: "complete");
            """),
        new(7, "Coupon", Fit.Clean,
            """
            public partial class Coupon : IComputed
            {
                public string Code { get; set; } = "";
                public int Percent { get; set; }
                public int TimesRedeemed { get; private set; }
                public bool Valid { get; private set; }
                public void Compute() => Valid = TimesRedeemed < 100;
            }
            """,
            """
            app.MapCrud<Coupon>().Expiring()
                .NaturalKey(coupon => coupon.Code)
                .CreateOnly(coupon => coupon.Code, coupon => coupon.Percent)
                .Searchable(coupon => coupon.Code);
            """),
        new(8, "Payment (auth/capture/void)", Fit.Clean,
            """
            public enum PaymentStatus { RequiresCapture, Captured, Voided }
            public partial class Payment { public decimal Amount { get; set; } }
            public partial class PaymentTransaction { public string Kind { get; set; } = ""; public decimal Amount { get; set; } }
            """,
            """
            var payments = app.MapCrud<Payment>().Without(CrudOperation.Delete)
                .Workflow<PaymentStatus>().EditableIn(PaymentStatus.RequiresCapture).Idempotent();
            payments.MapTransition("capture", PaymentStatus.RequiresCapture, PaymentStatus.Captured);
            payments.MapTransition("void", PaymentStatus.RequiresCapture, PaymentStatus.Voided);
            app.MapAppendOnly<PaymentTransaction>().Under<Payment>();
            """),
        new(9, "Refund", Fit.Clean,
            Order + """
            public enum RefundStatus { Pending, Succeeded, Cancelled }
            public partial class Refund { public decimal Amount { get; set; } }
            """,
            """
            app.MapCrud<Order>();
            var refunds = app.MapAppendOnly<Refund>().References<Order>().Workflow<RefundStatus>().Idempotent(required: true).Metadata();
            refunds.MapTransition("cancel", RefundStatus.Pending, RefundStatus.Cancelled);
            """),
        new(10, "FulfillmentOrder", Fit.Partial,
            """
            public enum FulfillmentStatus { Open, OnHold, Closed }
            public partial class FulfillmentOrder { public string Location { get; set; } = ""; }
            """,
            """
            var fulfillments = app.MapReadOnly<FulfillmentOrder>().Workflow<FulfillmentStatus>();
            fulfillments.MapTransition("hold", FulfillmentStatus.Open, FulfillmentStatus.OnHold);
            fulfillments.MapTransition("release", FulfillmentStatus.OnHold, FulfillmentStatus.Open);
            """),
        new(11, "Inventory level", Fit.Partial,
            """
            public partial class StockMovement { public string Sku { get; set; } = ""; public int Delta { get; set; } }
            public partial class StockLevel { public string Sku { get; set; } = ""; public int OnHand { get; set; } }
            public sealed class StockLevelProjection : IDerivation<StockMovement, StockLevel>
            {
                public System.Threading.Tasks.Task ApplyAsync(StockMovement source, IDerivedSet<StockLevel> targets, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }
            """,
            """
            app.MapAppendOnly<StockMovement>().MultiTenant();
            app.MapReadOnly<StockLevel>().MultiTenant().DerivedFrom<StockMovement>();
            """),

        // ── Muhasebe ─────────────────────────────────────────────────────────────────────────────────
        new(12, "Invoice", Fit.Partial,
            """
            public enum InvoiceStatus { Draft, Issued, Paid, Void }
            public partial class Invoice { public string Customer { get; set; } = ""; }
            public partial class InvoiceLine { public string Description { get; set; } = ""; public decimal Amount { get; set; } }
            """,
            """
            var invoices = app.MapCrud<Invoice>().ConcurrencyChecked().Idempotent()
                .Workflow<InvoiceStatus>().EditableIn(InvoiceStatus.Draft).DeletableIn(InvoiceStatus.Draft)
                .Numbered("INV-{yyyy}-{seq:6}", assignOn: InvoiceStatus.Issued).Previewable();
            invoices.MapDetail<InvoiceLine>();
            invoices.MapTransition("issue", InvoiceStatus.Draft, InvoiceStatus.Issued);
            invoices.MapTransition("pay", InvoiceStatus.Issued, InvoiceStatus.Paid);
            invoices.MapTransition("void", [InvoiceStatus.Draft, InvoiceStatus.Issued], InvoiceStatus.Void);
            """),
        new(13, "InvoiceLine", Fit.Clean,
            """
            public partial class Invoice : IComputed
            {
                public decimal Total { get; private set; }
                public void Compute() => Total = InvoiceLines.Sum(line => line.Amount);
            }
            public partial class InvoiceLine { public string Description { get; set; } = ""; public decimal Amount { get; set; } }
            """,
            "app.MapCrud<Invoice>().MapDetail<InvoiceLine>();"),
        new(14, "Credit note", Fit.Clean,
            Invoice + """
            public enum CreditNoteStatus { Issued, Voided }
            public partial class CreditNote { public decimal Amount { get; set; } }
            """,
            """
            app.MapCrud<Invoice>();
            var notes = app.MapAppendOnly<CreditNote>().CreatedFrom<Invoice>().Workflow<CreditNoteStatus>().Numbered("CN-{seq:6}");
            notes.MapTransition("void", CreditNoteStatus.Issued, CreditNoteStatus.Voided);
            """),
        new(15, "JournalEntry", Fit.Clean,
            """
            public enum JournalStatus { Draft, Posted }
            public partial class JournalEntry { public string Memo { get; set; } = ""; }
            public partial class JournalLine { public decimal Debit { get; set; } public decimal Credit { get; set; } }
            """,
            """
            var entries = app.MapCrud<JournalEntry>()
                .Workflow<JournalStatus>().EditableIn(JournalStatus.Draft).DeletableIn(JournalStatus.Draft)
                .Numbered("JE-{yyyy}-{seq:6}", assignOn: JournalStatus.Posted).Reversible(JournalStatus.Posted);
            entries.MapDetail<JournalLine>();
            entries.MapTransition("post", JournalStatus.Draft, JournalStatus.Posted, entry =>
            {
                if (entry.JournalLines.Sum(line => line.Debit) != entry.JournalLines.Sum(line => line.Credit))
                {
                    throw new TransitionRejectedException("Debits and credits must balance.");
                }
            });
            """),
        new(16, "Journals (genel defter)", Fit.Clean,
            "public partial class LedgerEntry { public string Account { get; set; } = \"\"; public decimal Amount { get; set; } }",
            "app.MapReadOnly<LedgerEntry>().Searchable(entry => entry.Account, entry => entry.Amount);"),
        new(17, "Chart of accounts", Fit.Clean,
            """
            public partial class LedgerAccount { public string Name { get; set; } = ""; }
            public partial class Posting { public decimal Amount { get; set; } }
            """,
            """
            app.MapCrud<LedgerAccount>().Hierarchical().Archivable();
            app.MapAppendOnly<Posting>().References<LedgerAccount>(OnDelete.Restrict);
            """),
        new(18, "Document numbering", Fit.Clean,
            "public partial class Receipt { public decimal Amount { get; set; } }",
            "app.MapCrud<Receipt>().MultiTenant().Numbered(\"RCP-{yyyy}{MM}-{seq:5}\");"),
        new(19, "Payment (muhasebe) + allocation", Fit.Clean,
            Invoice + "public partial class InvoicePayment { public decimal Amount { get; set; } }",
            """
            app.MapCrud<Invoice>();
            app.MapAppendOnly<InvoicePayment>().References<Invoice>().Idempotent().Reversible();
            """),

        // ── Envanter ─────────────────────────────────────────────────────────────────────────────────
        new(20, "StockMovement / defter kaydı", Fit.Clean,
            "public partial class StockMovement { public string Sku { get; set; } = \"\"; public int Delta { get; set; } }",
            "app.MapAppendOnly<StockMovement>().MultiTenant().Auditable(trackUser: true);"),
        new(21, "Reservation", Fit.Clean,
            """
            public enum ReservationStatus { Held, Confirmed, Released }
            public partial class Reservation { public string Sku { get; set; } = ""; public int Quantity { get; set; } }
            """,
            """
            var reservations = app.MapCrud<Reservation>().Workflow<ReservationStatus>().Expiring(TimeSpan.FromMinutes(15));
            reservations.MapTransition("confirm", ReservationStatus.Held, ReservationStatus.Confirmed);
            reservations.MapTransition("release", ReservationStatus.Held, ReservationStatus.Released);
            """),
        new(22, "Transfer", Fit.Clean,
            """
            public enum TransferStatus { Draft, InTransit, Received }
            public partial class Transfer : IComputed
            {
                public string Destination { get; set; } = "";
                public int TotalQuantity { get; private set; }
                public void Compute() => TotalQuantity = TransferLines.Sum(line => line.Quantity);
            }
            public partial class TransferLine { public string Sku { get; set; } = ""; public int Quantity { get; set; } }
            """,
            """
            var transfers = app.MapCrud<Transfer>().Workflow<TransferStatus>().EditableIn(TransferStatus.Draft).Idempotent().Cloneable();
            transfers.MapDetail<TransferLine>();
            transfers.MapTransition("send", TransferStatus.Draft, TransferStatus.InTransit);
            transfers.MapTransition("receive", TransferStatus.InTransit, TransferStatus.Received);
            """),

        // ── İK ───────────────────────────────────────────────────────────────────────────────────────
        new(23, "Employee", Fit.Partial,
            """
            public partial class Employee { public string Name { get; set; } = ""; }
            public partial class EmploymentRecord { public string Title { get; set; } = ""; }
            """,
            """
            var employees = app.MapCrud<Employee>().MultiTenant().Searchable(employee => employee.Name).ChangeFeed();
            employees.MapAction("terminate", employee => employee.Name = employee.Name.Trim());
            app.MapCrud<EmploymentRecord>().Under<Employee>().EffectiveDated();
            """),
        new(24, "LeaveRequest", Fit.Clean,
            """
            public enum LeaveStatus { Requested, Taken, Cancelled }
            public partial class Employee { public string Name { get; set; } = ""; }
            public partial class LeaveRequest { public DateOnly From { get; set; } public DateOnly To { get; set; } }
            """,
            """
            app.MapCrud<Employee>();
            var leave = app.MapCrud<LeaveRequest>().Under<Employee>().Workflow<LeaveStatus>().Approvable(levels: 2, "take");
            leave.MapTransition("take", LeaveStatus.Requested, LeaveStatus.Taken);
            leave.MapTransition("cancel", LeaveStatus.Requested, LeaveStatus.Cancelled);
            """),
        new(25, "Time-off balance", Fit.Partial,
            """
            public partial class LeaveEntry { public string EmployeeCode { get; set; } = ""; public decimal Days { get; set; } }
            public partial class LeaveBalance { public string EmployeeCode { get; set; } = ""; public decimal Days { get; set; } }
            public sealed class LeaveBalanceProjection : IDerivation<LeaveEntry, LeaveBalance>
            {
                public System.Threading.Tasks.Task ApplyAsync(LeaveEntry source, IDerivedSet<LeaveBalance> targets, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }
            """,
            """
            app.MapAppendOnly<LeaveEntry>().Reversible();
            app.MapReadOnly<LeaveBalance>().DerivedFrom<LeaveEntry>();
            """),
        new(26, "Timesheet / attendance", Fit.Clean,
            """
            public partial class TimeEntry
            {
                public DateOnly Day { get; set; }
                public decimal Hours { get; set; }
                public DateTimeOffset? ClockedOutAt { get; private set; }
                public void ClockOut(DateTimeOffset at) => ClockedOutAt = at;
            }
            """,
            """
            var entries = app.MapCrud<TimeEntry>().MultiTenant().Batch(atomic: false);
            entries.MapAction("clock-out", entry => entry.ClockOut(DateTimeOffset.UtcNow));
            """),

        // ── İçerik yönetimi ──────────────────────────────────────────────────────────────────────────
        new(27, "Post / Entry", Fit.Clean,
            "public partial class Post { public string Title { get; set; } = \"\"; public string? Body { get; set; } }",
            "app.MapCrud<Post>().Publishable().SoftDeletable().ConcurrencyChecked().Revisioned();"),
        new(28, "Localized fields", Fit.Missing),
        new(29, "Revision / Snapshot", Fit.Clean,
            "public partial class Page { public string Title { get; set; } = \"\"; public string? Body { get; set; } }",
            "app.MapCrud<Page>().Revisioned();"),
        new(30, "Comment (threaded)", Fit.Partial,
            """
            public partial class Post { public string Title { get; set; } = ""; }
            public partial class Comment { public string Text { get; set; } = ""; }
            """,
            """
            app.MapCrud<Post>();
            app.MapCrud<Comment>().Under<Post>().Hierarchical().SoftDeletable();
            """),
        new(31, "Tag / Label", Fit.Clean,
            "public partial class Article { public string Title { get; set; } = \"\"; }",
            """
            app.MapLookup<Tag>();
            app.MapCrud<Article>();
            app.MapAssociation<Article, Tag>();
            """),
        new(32, "MediaFile / Asset", Fit.Missing),

        // ── Rezervasyon ──────────────────────────────────────────────────────────────────────────────
        new(33, "EventType / rezerve edilebilir kaynak", Fit.Clean,
            "public partial class EventType { public string Name { get; set; } = \"\"; public int Minutes { get; set; } }",
            "app.MapCrud<EventType>().Without(CrudOperation.Delete).Archivable();"),
        new(34, "Availability / free-busy", Fit.Clean,
            """
            public sealed class AvailabilityRequest { public DateOnly Day { get; set; } }
            public sealed record Availability(bool Free);
            """,
            "app.MapQuery<AvailabilityRequest, Availability>((request, _) => System.Threading.Tasks.Task.FromResult(new Availability(true)));"),
        new(35, "Appointment / Booking", Fit.Clean,
            """
            public enum BookingStatus { Pending, Confirmed, Cancelled }
            public partial class Booking { public DateTimeOffset StartsAt { get; set; } }
            """,
            """
            var bookings = app.MapCrud<Booking>().Workflow<BookingStatus>().Idempotent().ConcurrencyChecked()
                .Supersedes(BookingStatus.Pending, BookingStatus.Confirmed);
            bookings.MapTransition("confirm", BookingStatus.Pending, BookingStatus.Confirmed);
            bookings.MapTransition("cancel", [BookingStatus.Pending, BookingStatus.Confirmed], BookingStatus.Cancelled);
            """),
        new(36, "Recurring event / series", Fit.Missing),

        // ── SaaS ─────────────────────────────────────────────────────────────────────────────────────
        new(37, "Tenant / Organization", Fit.Clean,
            "public partial class Organization { public string Name { get; set; } = \"\"; }",
            "app.MapCrud<Organization>().Auditable();"),
        new(38, "User", Fit.Clean,
            User,
            """
            var users = app.MapCrud<User>().SoftDeletable().Metadata().Searchable(user => user.Email);
            users.MapAction("verify-email", user => user.Email = user.Email.ToLowerInvariant());
            """),
        new(39, "Role & permission", Fit.Clean,
            "public partial class Role { public string Name { get; set; } = \"\"; }",
            """
            app.MapLookup<Permission>();
            app.MapCrud<Role>();
            app.MapAssociation<Role, Permission>();
            """),
        new(40, "Membership", Fit.Clean,
            User + """
            public enum MembershipStatus { Pending, Active, Suspended }
            public partial class Organization { public string Name { get; set; } = ""; }
            public partial class OrganizationUser { public string Role { get; set; } = ""; }
            """,
            """
            app.MapCrud<User>();
            app.MapCrud<Organization>();
            var memberships = app.MapAssociation<Organization, User>().Workflow<MembershipStatus>();
            memberships.MapTransition("activate", MembershipStatus.Pending, MembershipStatus.Active);
            memberships.MapTransition("suspend", MembershipStatus.Active, MembershipStatus.Suspended);
            """),
        new(41, "Invitation", Fit.Clean,
            """
            public enum InvitationStatus { Pending, Accepted, Revoked }
            public partial class Invitation { public string Email { get; set; } = ""; }
            public partial class Member { public string Email { get; set; } = ""; }
            """,
            """
            var invitations = app.MapCrud<Invitation>().Without(CrudOperation.Update, CrudOperation.Patch, CrudOperation.Delete)
                .Workflow<InvitationStatus>().Expiring(TimeSpan.FromDays(7)).TokenAccess("inv", "accept");
            invitations.MapTransition("accept", InvitationStatus.Pending, InvitationStatus.Accepted);
            invitations.MapTransition("revoke", InvitationStatus.Pending, InvitationStatus.Revoked);
            app.MapCrud<Member>().CreatedFrom<Invitation>(on: "accept");
            """),
        new(42, "ApiKey / secret", Fit.Clean,
            "public partial class ApiKey { public string Name { get; set; } = \"\"; }",
            """
            app.MapCrud<ApiKey>().Without(CrudOperation.Update, CrudOperation.Patch).Expiring()
                .SecretOnce(prefix: "sk").Rotatable(overlap: TimeSpan.FromHours(24));
            """),
        new(43, "Settings / Preferences", Fit.Clean,
            """
            public partial class StoreSettings { public string Currency { get; set; } = ""; }
            public partial class Preference { public string Value { get; set; } = ""; }
            """,
            """
            app.MapSingleton<StoreSettings>().MultiTenant();
            app.MapStore<Preference>().MultiTenant();
            """),
        new(44, "AuditLog / Event", Fit.Partial,
            """
            public partial class AuditLog { public string Action { get; set; } = ""; }
            public partial class AuditLogExport { public int Year { get; set; } public string? Url { get; private set; } }
            """,
            $$"""
            app.MapReadOnly<AuditLog>().MultiTenant().Searchable(log => log.Action);
            app.MapJob<AuditLogExport>({{Job}}).MultiTenant();
            """),
        new(45, "Notification", Fit.Partial,
            """
            public partial class Notification
            {
                public string Title { get; private set; } = "";
                public DateTimeOffset? ReadAt { get; private set; }
                public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
            }
            """,
            """
            var notifications = app.MapReadOnly<Notification>().OwnedByCurrentUser();
            notifications.MapAction("mark-read", (notification, services, cancellationToken) =>
            {
                notification.MarkRead(services.GetRequiredService<TimeProvider>().GetUtcNow());
                return System.Threading.Tasks.Task.CompletedTask;
            });
            """),
        new(46, "Webhook endpoint", Fit.Clean,
            Webhook + """
            public partial class WebhookSettings { public int TimeoutSeconds { get; set; } = 30; public bool VerifyTls { get; set; } = true; }
            """,
            """
            var webhooks = app.MapCrud<Webhook>().MultiTenant().Archivable().SecretOnce(prefix: "whsec", recoverable: true).Rotatable();
            webhooks.MapAction("ping", (webhook, services, cancellationToken) => System.Threading.Tasks.Task.CompletedTask);
            app.MapSingleton<WebhookSettings>().Under<Webhook>();
            """),
        new(47, "Webhook delivery", Fit.Clean,
            Webhook + """
            public enum DeliveryStatus { Pending, Delivered, Failed }
            public partial class WebhookDelivery { public int Attempt { get; set; } }
            """,
            """
            app.MapCrud<Webhook>();
            var deliveries = app.MapReadOnly<WebhookDelivery>().Under<Webhook>().Workflow<DeliveryStatus>();
            deliveries.MapTransition("redeliver", DeliveryStatus.Failed, DeliveryStatus.Pending);
            """),
        new(48, "Subscription / Plan", Fit.Clean,
            """
            public enum SubscriptionStatus { Active, PastDue, Cancelled }
            public partial class Subscription { public string Customer { get; set; } = ""; }
            public partial class SubscriptionItem { public string Plan { get; set; } = ""; public int Quantity { get; set; } }
            """,
            """
            var subscriptions = app.MapCrud<Subscription>().Workflow<SubscriptionStatus>();
            subscriptions.MapDetail<SubscriptionItem>();
            subscriptions.MapTransition("cancel", [SubscriptionStatus.Active, SubscriptionStatus.PastDue], SubscriptionStatus.Cancelled);
            """),

        // ── Yatay kalıplar ───────────────────────────────────────────────────────────────────────────
        new(49, "Polimorfik attachment", Fit.Missing),
        new(50, "Comment / activity feed", Fit.Missing),
        new(51, "Custom fields / metadata", Fit.Partial,
            Product,
            "app.MapCrud<Product>().Metadata();"),
        new(52, "/me ve kullanıcıya ait kaynaklar", Fit.Clean,
            """
            public partial class SavedAddress { public string Line { get; set; } = ""; }
            public partial class Draft { public string Text { get; set; } = ""; }
            """,
            """
            app.MapCrud<SavedAddress>().OwnedByCurrentUser();
            app.MapCrud<Draft>().OwnedByCurrentUser(underMe: false);
            """),
        new(53, "Report / aggregation", Fit.Clean,
            """
            public sealed class SalesReportRequest { public int Year { get; set; } }
            public sealed record SalesReport(decimal Total);
            public partial class SalesReportExport { public int Year { get; set; } public string? Url { get; private set; } }
            """,
            $$"""
            app.MapQuery<SalesReportRequest, SalesReport>((request, _) => System.Threading.Tasks.Task.FromResult(new SalesReport(0)));
            app.MapJob<SalesReportExport>({{Job}});
            """),
        new(54, "Search", Fit.Clean,
            Product,
            "app.MapCrud<Product>().Searchable(product => product.Name, product => product.Price);"),
        new(55, "Favorite / star / vote", Fit.Clean,
            Product,
            """
            app.MapCrud<Product>();
            app.MapAssociation<Favorite, Product>().ForCurrentUser();
            """),
        new(56, "Ordering / position", Fit.Clean,
            """
            public partial class Board { public string Name { get; set; } = ""; }
            public partial class Card { public string Title { get; set; } = ""; }
            """,
            """
            app.MapCrud<Board>();
            app.MapCrud<Card>().Under<Board>().Orderable();
            """),
        new(57, "Counter", Fit.Partial,
            """
            public partial class Playlist : IComputed
            {
                public string Name { get; set; } = "";
                public int TrackCount { get; private set; }
                public void Compute() => TrackCount = Tracks.Count;
            }
            public partial class Track { public string Title { get; set; } = ""; }
            """,
            "app.MapCrud<Playlist>().MapDetail<Track>();"),
        new(58, "Bulk operations", Fit.Clean,
            Product + "public partial class PriceUpdate { public int Percent { get; set; } public int Changed { get; private set; } }",
            $$"""
            app.MapCrud<Product>().Batch();
            app.MapJob<PriceUpdate>({{Job}});
            """),
        new(59, "Büyük veri export'u", Fit.Clean,
            "public partial class DataExport { public string Kind { get; set; } = \"\"; public string? Url { get; private set; } }",
            $"app.MapJob<DataExport>({Job}).MultiTenant().Idempotent();"),
    ];

    public static TheoryData<int> RowNumbers => [.. Rows.Select(row => row.Number)];

    [Theory]
    [MemberData(nameof(RowNumbers))]
    public void Row_IsExpressedWithTheImplementedApi(int number)
    {
        var row = Rows.Single(candidate => candidate.Number == number);

        if (row.Map is null)
        {
            // Yazılabilecek anlamlı bir eşleme yok; satır bilinçli olarak açık bırakıldı.
            Assert.Equal(Fit.Missing, row.Fit);
            return;
        }

        Assert.NotEqual(Fit.Missing, row.Fit);

        var run = GeneratorHarness.Run($$"""
            using System.Linq;

            namespace Shop
            {
                {{row.Declarations}}

                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app)
                    {
                        {{row.Map}}
                    }
                }
            }
            """).AssertCompiles();

        // Eşleme boş geçmedi: en az bir kaynak üretildi.
        Assert.Contains(run.Sources.Keys, name => name.EndsWith(".Endpoints.g.cs", StringComparison.Ordinal) || name.EndsWith(".Query.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Table_CoversEveryRowOfTheReportOnce()
    {
        Assert.Equal(Enumerable.Range(1, 59), Rows.Select(row => row.Number));
    }

    [Fact]
    public void Tally_MatchesTheWrittenResults()
    {
        // reports/Haldus taksonomi sınaması.md dosyasındaki sayılar; biri değişirse diğeri de güncellenmelidir.
        Assert.Equal(41, Rows.Count(row => row.Fit == Fit.Clean));
        Assert.Equal(13, Rows.Count(row => row.Fit == Fit.Partial));
        Assert.Equal(5, Rows.Count(row => row.Fit == Fit.Missing));
    }
}
