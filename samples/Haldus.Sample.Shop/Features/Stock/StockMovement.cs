using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Stock;

public partial class StockMovement
{
    public Guid ProductId { get; set; }

    [Range(-1_000_000, 1_000_000)]
    public int Quantity { get; set; }

    [MaxLength(200)]
    public string? Reason { get; set; }
}

public partial class StockLevel
{
    public Guid ProductId { get; set; }

    public int Quantity { get; set; }
}
