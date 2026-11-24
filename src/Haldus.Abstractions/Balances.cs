using System.ComponentModel.DataAnnotations;

namespace Haldus;

/// <summary>
/// Bakiyeyi bir miktar artırır veya azaltır (<c>POST /{key}/adjust</c>). Bakiyenin yeni değeri değil, değişimi
/// gönderilir; böylece aynı anda gelen iki düzeltme birbirini ezmez.
/// </summary>
public sealed record BalanceAdjustment
{
    /// <summary>Eklenecek (pozitif) veya düşülecek (negatif) miktar; sıfır olamaz.</summary>
    public decimal Delta { get; init; }

    /// <summary>Düzeltilen kova; verilmezse ilk kova.</summary>
    [MaxLength(64)]
    public string? Bucket { get; init; }

    /// <summary>Deftere yazılan açıklama: "sayım farkı", "sipariş 1042".</summary>
    [MaxLength(500)]
    public string? Reason { get; init; }
}

/// <summary>
/// Bakiyeyi mutlak bir değere çeker (<c>POST /{key}/set</c>); sayım sonucu gibi. <see cref="Expected"/> verilirse
/// yalnızca bakiye hâlâ o değerdeyse yazılır.
/// </summary>
public sealed record BalanceSetting
{
    public decimal Quantity { get; init; }

    /// <summary>İstemcinin gördüğü bakiye; verilmişse ve bakiye bu sırada değiştiyse istek 409 ile reddedilir.</summary>
    public decimal? Expected { get; init; }

    [MaxLength(64)]
    public string? Bucket { get; init; }

    [MaxLength(500)]
    public string? Reason { get; init; }
}

/// <summary>Bir miktarı bir kovadan ötekine taşır (<c>POST /{key}/move</c>): eldeki stoktan ayrılmış stoğa gibi.</summary>
public sealed record BalanceMove
{
    [Required]
    [MaxLength(64)]
    public string From { get; init; } = "";

    [Required]
    [MaxLength(64)]
    public string To { get; init; } = "";

    /// <summary>Taşınan miktar; sıfırdan büyük olmalıdır.</summary>
    public decimal Quantity { get; init; }

    [MaxLength(500)]
    public string? Reason { get; init; }
}

/// <summary>Bakiyenin defterindeki bir kayıt: bakiyeyi değiştiren her yazma bir tane üretir.</summary>
/// <param name="Id">Kaydın sırası; aynı bakiyenin kayıtları arasında artar.</param>
/// <param name="Delta">Kovadaki değişim.</param>
/// <param name="Balance">Kovanın bu kayıttan sonraki bakiyesi.</param>
public sealed record BalanceEntry(
    long Id,
    string Bucket,
    decimal Delta,
    decimal Balance,
    string? Reason,
    DateTimeOffset CreatedAt,
    string? CreatedBy);
