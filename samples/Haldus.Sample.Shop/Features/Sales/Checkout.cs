using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Sales;

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
