using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Sales;

/// <summary>
/// Sipariş, satırlarıyla birlikte bir bütündür: satırlar siparişin isteğinde gelir ve onunla kaydedilir.
/// </summary>
public partial class Order : IComputed
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";

    // Herkese açık setter'ı olmayan alanlar yalnızca yanıtta görünür; değerlerini Compute belirler.
    public decimal Total { get; private set; }

    public int ItemCount { get; private set; }

    /// <summary>Haldus bunu, istek uygulandıktan ve satırlar yüklendikten sonra, kaydetmeden önce çağırır.</summary>
    public void Compute()
    {
        Total = OrderItems.Sum(item => item.Quantity * item.UnitPrice);
        ItemCount = OrderItems.Count;
    }
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
