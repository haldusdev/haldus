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

/// <summary>
/// İletişim kaydı. Kimlik numarası yalnızca yazılır; hiçbir yanıtta ve saklanan sürümde yer almaz. Banka hesabı da
/// öyledir ve ayrıca veritabanında şifreli saklanır.
/// </summary>
public partial class Contact
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(20)]
    public string? NationalId { get; set; }

    [MaxLength(34)]
    public string? Iban { get; set; }

    [MaxLength(100)]
    public string? Source { get; set; }
}

/// <summary><c>GET /contacts</c> ve <c>POST /contacts/purge</c> için ölçütler.</summary>
public sealed class ContactFilter : IFilter<Contact>
{
    public string? Source { get; set; }

    public string? NameStartsWith { get; set; }

    /// <summary>Yalnızca kaynağı bilinmeyen kayıtlar.</summary>
    public bool Unsourced { get; set; }

    public IQueryable<Contact> Apply(IQueryable<Contact> query) => query
        .WhereIf(Source is not null, contact => contact.Source == Source)
        .WhereIf(NameStartsWith is not null, contact => contact.Name.StartsWith(NameStartsWith!))
        .WhereIf(Unsourced, contact => contact.Source == null);
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

/// <summary>Cihaz modeli: cihazların tipi. Garanti süresi ve bakım aralığı, cihazlar için varsayılandır.</summary>
public partial class DeviceModel
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    [Range(0, 120)]
    public int WarrantyMonths { get; set; }

    [MaxLength(200)]
    public string? Manual { get; set; }
}

/// <summary>Cihaz. Boş bıraktığı alanları modelinden devralır; yalnızca farklı olanı yazar.</summary>
public partial class Device
{
    [Required]
    [MaxLength(50)]
    public string SerialNumber { get; set; } = "";

    [Range(0, 120)]
    public int? WarrantyMonths { get; set; }

    [MaxLength(200)]
    public string? Manual { get; set; }
}

/// <summary>Kişi: müşteri veya çalışan rollerini oynayabilir.</summary>
public partial class Person
{
    [Required]
    [MaxLength(200)]
    public string FullName { get; set; } = "";
}

/// <summary>Kişinin çalışan rolü; kendi numarası ve alanları vardır. Bir kişi en çok bir kez çalışandır.</summary>
public partial class StaffMember
{
    [MaxLength(100)]
    public string? Department { get; set; }
}

/// <summary>Araç: otomobil ve kamyonun üst tipi. Ortak alanlar buradadır.</summary>
public partial class Vehicle
{
    [Required]
    [MaxLength(20)]
    public string Plate { get; set; } = "";
}

/// <summary>Otomobil bir araçtır; aynı tabloda, aynı kimlikle durur ve kendi alanını ekler.</summary>
public partial class Car : Vehicle
{
    [Range(1, 9)]
    public int Seats { get; set; }
}

/// <summary>Kamyon bir araçtır.</summary>
public partial class Truck : Vehicle
{
    [Range(0.0, 100.0)]
    public decimal PayloadTons { get; set; }
}

/// <summary>
/// Toplantı: bir kez saklanır, kuralına göre yinelenir. İlk oluşu ve kuralı Haldus'un eklediği alanlar taşır
/// (StartsAt, RecurrenceFrequency, RecurrenceInterval, RecurrenceUntil, RecurrenceCount).
/// </summary>
public partial class Meeting
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [Range(5, 1440)]
    public int DurationMinutes { get; set; } = 30;
}

/// <summary>Giysi: bedenlere ve renklere göre varyantları olan ürün.</summary>
public partial class Garment
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";
}

/// <summary>Giysinin bir varyantı; giysinin isteğine ve yanıtına gömülüdür.</summary>
public partial class GarmentVariant
{
    [Required]
    [MaxLength(100)]
    public string Sku { get; set; } = "";

    [MaxLength(20)]
    public string Size { get; set; } = "";

    [MaxLength(20)]
    public string Color { get; set; } = "";
}

/// <summary>
/// Seçeneklerin bütün birleşimlerinden varyant üretir. Alana özgü bu mantık bir entity aksiyonudur: Haldus kaydı
/// satırlarıyla yükler, isteği doğrular, aksiyonu çalıştırır ve kaydı satırlarıyla birlikte kaydeder.
/// </summary>
public sealed class GenerateVariants : IEntityAction<Garment>
{
    [Required]
    [MinLength(1)]
    [MaxLength(10)]
    public string[] Sizes { get; set; } = [];

    [Required]
    [MinLength(1)]
    [MaxLength(10)]
    public string[] Colors { get; set; } = [];

    public void Apply(Garment entity)
    {
        foreach (var size in Sizes)
        {
            foreach (var color in Colors)
            {
                // Var olan birleşim korunur; yalnızca eksik olanlar eklenir.
                if (!entity.GarmentVariants.Any(variant => variant.Size == size && variant.Color == color))
                {
                    entity.GarmentVariants.Add(new GarmentVariant
                    {
                        Size = size,
                        Color = color,
                        Sku = $"{entity.Name}-{size}-{color}".ToUpperInvariant().Replace(' ', '-'),
                    });
                }
            }
        }
    }
}
