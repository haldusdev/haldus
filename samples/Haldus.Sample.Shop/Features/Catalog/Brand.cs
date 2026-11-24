using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Catalog;

public partial class Brand
{
    // Required boş metni de reddeder; kural PATCH dahil tüm yazma işlemlerinde aynı çalışır.
    [Required, MaxLength(100)]
    public string Name { get; set; } = "";
}
