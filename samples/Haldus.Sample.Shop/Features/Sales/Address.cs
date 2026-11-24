using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Sales;

public partial class Address
{
    [MaxLength(300)]
    public string Line { get; set; } = "";

    [MaxLength(100)]
    public string City { get; set; } = "";
}
