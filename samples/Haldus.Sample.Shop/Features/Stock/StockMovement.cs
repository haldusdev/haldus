using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Haldus.Sample.Shop.Features.Stock;

public partial class StockMovement : IReversal<StockMovement>
{
    public Guid ProductId { get; set; }

    [Range(-1_000_000, 1_000_000)]
    public int Quantity { get; set; }

    [MaxLength(200)]
    public string? Reason { get; set; }

    /// <summary>Haldus ters hareketi bu hareketin kopyası olarak kurar; burada yalnızca miktarın yönü çevrilir.</summary>
    public void Reverse(StockMovement original) => Quantity = -original.Quantity;
}

public partial class StockLevel
{
    public Guid ProductId { get; set; }

    public int Quantity { get; set; }
}

/// <summary>
/// Stok seviyesinin hareketlerden nasıl türediği. Haldus bunu her hareket oluşturulurken, hareketle aynı
/// transaction'da ve sırayla çağırır; burada okunan seviye başka bir hareket tarafından aynı anda değiştirilemez.
/// </summary>
public sealed class StockLevelProjection : IDerivation<StockMovement, StockLevel>
{
    public async Task ApplyAsync(StockMovement source, IDerivedSet<StockLevel> targets, CancellationToken cancellationToken)
    {
        var level = await targets.Records.FirstOrDefaultAsync(item => item.ProductId == source.ProductId, cancellationToken);

        if (level is null)
        {
            level = new StockLevel { ProductId = source.ProductId };
            targets.Add(level);
        }

        level.Quantity += source.Quantity;
    }
}

/// <summary>Bir ürünün (SKU) stoğu; kovaları Haldus üretir: Available, Reserved, Damaged.</summary>
public partial class Inventory;

/// <summary>Bir çalışanın izin bakiyesi (gün); tek miktarlıdır.</summary>
public partial class LeaveAllowance;
