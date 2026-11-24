using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Catalog;

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [Range(0.0, 1_000_000.0)]
    public decimal Price { get; set; }

    public string? Description { get; set; }
}

public partial class ProductReview
{
    [MaxLength(2000)]
    public string Text { get; set; } = "";

    [Range(1, 5)]
    public int Rating { get; set; }
}
