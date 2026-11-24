using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Platform;

public partial class FeatureFlag
{
    public bool Enabled { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }
}

public partial class Preference
{
    [MaxLength(1000)]
    public string Value { get; set; } = "";
}

public partial class MaintenanceNotice
{
    public bool Enabled { get; set; }

    [MaxLength(500)]
    public string? Message { get; set; }
}

public partial class StoreSettings
{
    [MaxLength(200)]
    public string StoreName { get; set; } = "";

    // Hiç yazılmamış ayarlar buradaki varsayılanlarla okunur.
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "TRY";

    [Range(0.0, 100.0)]
    public decimal TaxRate { get; set; } = 20m;
}

/// <summary>Katalogdaki ürünleri sayan iş; girdisi yoktur, çıktısı <see cref="ProductCount"/> alanıdır.</summary>
public partial class CatalogRecount
{
    public int ProductCount { get; private set; }

    public void Record(int productCount) => ProductCount = productCount;
}
