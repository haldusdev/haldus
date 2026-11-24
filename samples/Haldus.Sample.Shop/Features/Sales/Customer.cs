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

/// <summary>
/// <c>POST /customers/{id}/adjust-loyalty</c>. Bağımlılık gerekmediği için istek kendi etkisini uygular; kaydı
/// bulmayı, sürüm kontrolünü ve kaydetmeyi Haldus üretir.
/// </summary>
public sealed record AdjustLoyalty : IEntityAction<Customer>
{
    [Range(-1000, 1000)]
    public int Delta { get; init; }

    public void Apply(Customer entity) => entity.LoyaltyPoints += Delta;
}
