using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Supply;

/// <summary>Tedarikçi. Sertifika sayısı satırlardan hesaplanır.</summary>
public partial class Supplier : IComputed
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public int CertificateCount { get; private set; }

    public void Compute() => CertificateCount = SupplierCertificates.Count;
}

/// <summary>Tedarikçinin sertifikası. Yanıtta tedarikçiye gömülüdür, ama tek tek komutlarla yazılır.</summary>
public partial class SupplierCertificate
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    public DateTimeOffset? ValidUntil { get; set; }
}

/// <summary>Tedarikçi başına tek kayıt: ödeme koşulları. Hiç yazılmadıysa buradaki varsayılanlar döner.</summary>
public partial class SupplierTerms
{
    [Range(0, 180)]
    public int PaymentDays { get; set; } = 30;

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "TRY";
}

/// <summary>Tedarikçideki ilgili kişi. Tedarikçiye aittir, ama kendi adresinde yaşar.</summary>
public partial class SupplierContact
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [EmailAddress]
    [MaxLength(200)]
    public string? Email { get; set; }
}

public enum PurchaseRequestStatus
{
    Open,
    Ordered,
}

/// <summary>Satın alma talebi. İki farklı kişi onaylamadan siparişe dönüşemez.</summary>
public partial class PurchaseRequest
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [Range(0.0, 10_000_000.0)]
    public decimal Amount { get; set; }
}

/// <summary>Masraf talebi; tek bir onay yeter.</summary>
public partial class ExpenseClaim
{
    [Required]
    [MaxLength(200)]
    public string Description { get; set; } = "";

    [Range(0.0, 100_000.0)]
    public decimal Amount { get; set; }
}

/// <summary>Tedarik sözleşmesi. Düzeltildiğinde yeni taslağın revizyonu bir artar.</summary>
public partial class SupplyContract : IAmendment<SupplyContract>
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [Range(0.0, 10_000_000.0)]
    public decimal Value { get; set; }

    public int Revision { get; private set; } = 1;

    public void Amend(SupplyContract cancelled) => Revision = cancelled.Revision + 1;
}

public enum SupplyStatus
{
    Proposed,
    Approved,
    Discontinued,
}

/// <summary>
/// Tedarikçi ile ürün arasındaki bağın taşıdığı alanlar. Bağın anahtarlarını (SupplierId, ProductId) Haldus üretir;
/// burada yazılanlar bağın yüküdür ve <c>PUT</c> isteğinin gövdesinde gelir.
/// </summary>
public partial class SupplierProduct
{
    [Range(0.0, 1_000_000.0)]
    public decimal Cost { get; set; }

    [Range(0, 365)]
    public int LeadTimeDays { get; set; }
}
