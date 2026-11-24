using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Platform;

/// <summary>
/// Değer nesnesi: kimliği ve kendi tablosu yoktur. Kuralları, onu taşıyan her kaynağın yazma
/// işlemlerinde kendiliğinden çalışır.
/// </summary>
public sealed record Money(
    [Range(0.0, 1_000_000_000.0)] decimal Amount,
    [StringLength(3, MinimumLength = 3)] string Currency);

public partial class Plan
{
    [MaxLength(100)]
    public string Name { get; set; } = "";

    public Money Price { get; set; } = new(0m, "TRY");
}
