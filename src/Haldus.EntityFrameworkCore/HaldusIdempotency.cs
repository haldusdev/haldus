using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>
/// <c>Idempotency-Key</c> ile yapılmış ve başarıyla tamamlanmış bir isteğin saklanan yanıtı. Aynı anahtar
/// yeniden gönderildiğinde işlem tekrarlanmaz, bu yanıt döner.
/// </summary>
public sealed class HaldusIdempotencyRecord
{
    /// <summary>Anahtarın geçerli olduğu kapsamın özeti: kiracı, kullanıcı, metot ve adres.</summary>
    public string Scope { get; set; } = "";

    public string Key { get; set; } = "";

    /// <summary>İstek gövdesinin özeti; aynı anahtarın farklı bir istekle kullanıldığını yakalar.</summary>
    public string Fingerprint { get; set; } = "";

    public int StatusCode { get; set; }

    public string? ContentType { get; set; }

    public string? Body { get; set; }

    public string? Location { get; set; }

    public string? ETag { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

public static class HaldusIdempotency
{
    public const int MaxKeyLength = 255;

    /// <summary>Yanıt kayıtlarının tablosunu modele ekler; üretilen kod, idempotent bir kaynak varsa çağırır.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusIdempotencyRecord>(entity =>
        {
            entity.ToTable("HaldusIdempotencyRecords");
            entity.HasKey(record => new { record.Scope, record.Key });
            entity.Property(record => record.Scope).HasMaxLength(64);
            entity.Property(record => record.Key).HasMaxLength(MaxKeyLength);
            entity.Property(record => record.Fingerprint).HasMaxLength(64);
            entity.HasIndex(record => record.ExpiresAt);
        });
    }
}
