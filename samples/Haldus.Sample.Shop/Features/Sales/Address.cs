using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Sales;

public partial class Address
{
    [MaxLength(300)]
    public string Line { get; set; } = "";

    [MaxLength(100)]
    public string City { get; set; } = "";
}

public partial class AddressNote
{
    [MaxLength(500)]
    public string Text { get; set; } = "";
}

public partial class Delivery
{
    [MaxLength(100)]
    public string Carrier { get; set; } = "";
}
