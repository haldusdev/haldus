using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Sales;

/// <summary>
/// Sipariş, satırlarıyla birlikte bir bütündür: satırlar siparişin isteğinde gelir ve onunla kaydedilir.
/// Durumu (<c>Status</c>) ve numarası (<c>Number</c>) Haldus'a aittir; istek gövdesinden yazılamaz.
/// </summary>
public partial class Order : IComputed
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";

    // Herkese açık setter'ı olmayan alanlar yalnızca yanıtta görünür; değerlerini Compute belirler.
    public decimal Total { get; private set; }

    public int ItemCount { get; private set; }

    // Bu ikisini geçişler yazar.
    public string? PlacementNote { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>Haldus bunu, istek uygulandıktan ve satırlar yüklendikten sonra, kaydetmeden önce çağırır.</summary>
    public void Compute()
    {
        Total = OrderItems.Sum(item => item.Quantity * item.UnitPrice);
        ItemCount = OrderItems.Count;
    }

    public void Place(string? note) => PlacementNote = note;

    public void Cancel(string reason) => CancellationReason = reason;

    public void Reopen() => CancellationReason = null;
}

public partial class OrderItem
{
    [MaxLength(200)]
    public string ProductName { get; set; } = "";

    [Range(1, 10_000)]
    public int Quantity { get; set; }

    [Range(0.0, 1_000_000.0)]
    public decimal UnitPrice { get; set; }
}

public partial class OrderNote
{
    [MaxLength(500)]
    public string Text { get; set; } = "";
}
