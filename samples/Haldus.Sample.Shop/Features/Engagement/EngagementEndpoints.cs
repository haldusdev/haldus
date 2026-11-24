namespace Haldus.Sample.Shop.Features.Engagement;

public static class EngagementEndpoints
{
    public static void MapEngagement(this IEndpointRouteBuilder app)
    {
        // Bildirim kullanıcıya aittir (/me/notifications) ve salt-okunurdur: sistem yazar. Okundu bilgisi ise
        // kullanıcınındır: POST /me/notifications/{id}/read ve /unread, POST /me/notifications/read-all,
        // GET /me/notifications/unread-count, GET /me/notifications?unread=true
        // Bildirimler 90 gün saklanır; süresi geçenler bakım sırasında silinir.
        app.MapReadOnly<Notification>()
            .OwnedByCurrentUser()
            .ReadTracking()
            .Retention(TimeSpan.FromDays(90));

        // Yanıt sayısı (ReplyCount) ve puan toplamı (Score) tartışmada durur; yanıt hangi yoldan yazılırsa yazılsın
        // aynı transaction içinde baştan hesaplanır. Bu alanlar süzülebilir ve sıralanabilir.
        app.MapCrud<Discussion>()
            .Counts<Reply>()
            .Sums<Reply>(reply => reply.Score, into: "Score")
            .Sortable(discussion => discussion.ReplyCount, discussion => discussion.Score)

            // Durum makinesi olmayan kayıt her zaman bir değişiklik kümesiyle düzenlenebilir.
            .ChangeSets();

        // Silinen yanıt sayılmaz; geri yüklenince yeniden sayılır.
        app.MapCrud<Reply>()
            .Under<Discussion>()
            .SoftDeletable()
            .PublishesEvents();

        // Kimlik numarası hassastır: isteklerde yazılır, hiçbir yanıtta ve saklanan sürümde yer almaz.
        // Ölçüte uyan kayıtlar topluca silinir: POST /contacts/purge?source=import-2024 önce sayar,
        // &force=true ile siler. Ölçüt, liste ucunun filtresidir.
        app.MapCrud<Contact>()
            .Revisioned()
            .Sensitive(contact => contact.NationalId)
            .Filterable<ContactFilter>()
            .Batch()
            .Purgeable();

        // İki bağımsız durum ekseni: kaydın kendi durumu (Status) ve faturalanması (TicketBilling). Geçişin hangi
        // eksende olduğunu durumlarının tipi söyler. EditableIn yalnızca asıl duruma bakar; kapalı kayıt da faturalanır.
        var tickets = app.MapCrud<Ticket>()
            .Workflow<TicketStatus>(initial: TicketStatus.Open)
            .EditableIn(TicketStatus.Open)
            .Axis<TicketBilling>(initial: TicketBilling.Unbilled)

            // Kayıt değiştikçe olay yayınlanır: ticket.created, ticket.updated, ticket.deleted ve her geçiş için
            // ticket.close, ticket.bill... Olaylar kayıtla aynı kaydetmede yazılır ve uygulamanın kaydettiği
            // IHaldusEventPublisher servislerine, kayıt kalıcı olduktan sonra sırayla teslim edilir.
            .PublishesEvents();

        tickets.MapTransition("close", from: TicketStatus.Open, to: TicketStatus.Closed);
        tickets.MapTransition("reopen", from: TicketStatus.Closed, to: TicketStatus.Open);
        tickets.MapTransition<BillTicket>(from: TicketBilling.Unbilled, to: TicketBilling.Billed);
        tickets.MapTransition("refund", from: TicketBilling.Billed, to: TicketBilling.Refunded);

        // Türetilmiş durum: ödenen tutar ödemelerden toplanır, faturanın durumu Compute() içinde ondan hesaplanır.
        app.MapCrud<Bill>()
            .Sums<BillPayment>(payment => payment.Amount, into: "PaidAmount");

        app.MapAppendOnly<BillPayment>()
            .References<Bill>();
    }
}
