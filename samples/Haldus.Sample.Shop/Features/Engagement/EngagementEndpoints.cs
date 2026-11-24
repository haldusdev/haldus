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
            .Sortable(discussion => discussion.ReplyCount, discussion => discussion.Score);

        // Silinen yanıt sayılmaz; geri yüklenince yeniden sayılır.
        app.MapCrud<Reply>()
            .Under<Discussion>()
            .SoftDeletable();

        // Türetilmiş durum: ödenen tutar ödemelerden toplanır, faturanın durumu Compute() içinde ondan hesaplanır.
        app.MapCrud<Bill>()
            .Sums<BillPayment>(payment => payment.Amount, into: "PaidAmount");

        app.MapAppendOnly<BillPayment>()
            .References<Bill>();
    }
}
