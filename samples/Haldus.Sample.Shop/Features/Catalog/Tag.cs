using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Catalog;

public partial class Tag
{
    [Required, MaxLength(50)]
    public string Name { get; set; } = "";
}
