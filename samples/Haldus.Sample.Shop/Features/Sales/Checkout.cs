using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Sales;

public enum ReservationStatus
{
    Held,
    Confirmed,
    Released,
}

/// <summary>Stok rezervasyonu: 15 dakika yaşar, süresi dolunca kendiliğinden görünmez olur.</summary>
public partial class Reservation
{
    [MaxLength(200)]
    public string ProductName { get; set; } = "";

    [Range(1, 10_000)]
    public int Quantity { get; set; }
}

/// <summary>Sepet kullanıldıkça yaşar: her yazma, ömrünü yeniden başlatır.</summary>
public partial class Cart
{
    [MaxLength(200)]
    public string? Note { get; set; }
}

/// <summary>Kuponun son kullanma zamanını oluşturan kişi verir; boş bırakılırsa kupon süresizdir.</summary>
public partial class Coupon
{
    [MaxLength(50)]
    public string Code { get; set; } = "";

    [Range(1, 90)]
    public int Percent { get; set; }

    /// <summary>Kuponun en çok kaç kez kullanılabileceği; boşsa sınırsızdır.</summary>
    [Range(1, 1_000_000)]
    public int? MaxRedemptions { get; set; }

    public int TimesRedeemed { get; private set; }

    public bool TryRedeem()
    {
        if (MaxRedemptions is { } limit && TimesRedeemed >= limit)
        {
            return false;
        }

        TimesRedeemed++;
        return true;
    }
}

/// <summary>
/// İade oluşturulur ve sonra değişmez. İsteğin tekrarı ikinci bir iade yaratmamalıdır; bu yüzden
/// <c>Idempotency-Key</c> zorunludur.
/// </summary>
public partial class Refund
{
    [Range(0.01, 1_000_000.0)]
    public decimal Amount { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }
}

public partial class CartItem
{
    [MaxLength(200)]
    public string ProductName { get; set; } = "";

    [Range(1, 1_000)]
    public int Quantity { get; set; }
}

public enum PaymentStatus
{
    RequiresCapture,
    Captured,
    Voided,
}

public partial class Payment
{
    [Range(0.01, 1_000_000.0)]
    public decimal Amount { get; set; }
}

/// <summary>Ödeme sağlayıcısıyla yapılan tek bir hareket; eklenir ve sonra değişmez.</summary>
public partial class PaymentTransaction
{
    [MaxLength(50)]
    public string Kind { get; set; } = "";

    public decimal Amount { get; set; }
}

public enum RefundStatus
{
    Pending,
    Succeeded,
    Cancelled,
}

public enum QuoteStatus
{
    Draft,
    Sent,
    Withdrawn,
}

/// <summary>Müşteriye verilen fiyat teklifi. Gönderilen teklif değişmez; değişiklik, yerine geçen yeni bir teklifle yapılır.</summary>
public partial class Quote
{
    [Required]
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";

    [Range(0.0, 10_000_000.0)]
    public decimal Amount { get; set; }

    [MaxLength(500)]
    public string? Terms { get; set; }
}

/// <summary>Tahsil edilen ödemenin makbuzu. Tutarı, aynı adı taşıdığı için ödemeden kendiliğinden kopyalanır.</summary>
public partial class Receipt
{
    public decimal Amount { get; set; }
}
