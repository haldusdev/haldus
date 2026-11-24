using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Sales;

public partial class Customer
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [EmailAddress, MaxLength(320)]
    public string? Email { get; set; }

    public int LoyaltyPoints { get; set; }
}
