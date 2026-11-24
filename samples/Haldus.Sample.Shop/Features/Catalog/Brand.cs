using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Catalog;

public partial class Brand
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}
