using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Engagement;

/// <summary>Kullanıcıya gönderilen bildirim. Sistem yazar; kullanıcı okur ve okundu işaretler.</summary>
public partial class Notification
{
    [MaxLength(200)]
    public string Title { get; set; } = "";
}

/// <summary>Tartışma başlığı. Yanıt sayısı ve yanıtların puan toplamı Haldus'un tuttuğu alanlardır.</summary>
public partial class Discussion
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";
}

/// <summary>Tartışmaya yazılan yanıt.</summary>
public partial class Reply
{
    [Required]
    [MaxLength(2000)]
    public string Text { get; set; } = "";

    [Range(-5, 5)]
    public int Score { get; set; }
}

/// <summary>İletişim kaydı. Kimlik numarası yalnızca yazılır; hiçbir yanıtta ve saklanan sürümde yer almaz.</summary>
public partial class Contact
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(20)]
    public string? NationalId { get; set; }

    [MaxLength(100)]
    public string? Source { get; set; }
}

/// <summary><c>GET /contacts</c> ve <c>POST /contacts/purge</c> için ölçütler.</summary>
public sealed class ContactFilter : IFilter<Contact>
{
    public string? Source { get; set; }

    public string? NameStartsWith { get; set; }

    public IQueryable<Contact> Apply(IQueryable<Contact> query) => query
        .WhereIf(Source is not null, contact => contact.Source == Source)
        .WhereIf(NameStartsWith is not null, contact => contact.Name.StartsWith(NameStartsWith!));
}

public enum TicketStatus
{
    Open,
    Closed,
}

public enum TicketBilling
{
    Unbilled,
    Billed,
    Refunded,
}

/// <summary>
/// Destek kaydı. İki bağımsız durumu vardır: kaydın kendisi açılır ve kapanır; faturalanması ayrı ilerler ve kapalı
/// bir kayıt da faturalanabilir.
/// </summary>
public partial class Ticket
{
    [Required]
    [MaxLength(200)]
    public string Subject { get; set; } = "";

    public string? BillingNote { get; private set; }

    public void Bill(string? note) => BillingNote = note;
}

/// <summary>Faturalama geçişinin isteği; kendi etkisini kayda uygular.</summary>
public sealed class BillTicket : ITransition<Ticket>
{
    [MaxLength(200)]
    public string? Note { get; set; }

    public void Apply(Ticket entity) => entity.Bill(Note);
}

public enum BillState
{
    Unpaid,
    PartiallyPaid,
    Paid,
}

/// <summary>
/// Ödenecek fatura. Ödenen tutar, ödemelerden toplanır; durum ise ondan türetilir ve hiçbir istekle yazılamaz.
/// </summary>
public partial class Bill : IComputed
{
    [Required]
    [MaxLength(200)]
    public string Payee { get; set; } = "";

    [Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }

    public BillState State { get; private set; }

    public void Compute() =>
        State = PaidAmount >= Amount ? BillState.Paid
            : PaidAmount > 0 ? BillState.PartiallyPaid
            : BillState.Unpaid;
}

/// <summary>Faturaya yapılan ödeme; yalnızca eklenir.</summary>
public partial class BillPayment
{
    [Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }
}
