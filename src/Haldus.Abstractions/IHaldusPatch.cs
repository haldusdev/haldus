using System.ComponentModel.DataAnnotations;

namespace Haldus;

/// <summary>
/// Kısmi güncelleme (PATCH) isteklerinin uyguladığı sözleşme. "Alan gönderilmedi" ile "alan null
/// gönderildi" ayrımını taşır; yalnızca gönderilen alanlar doğrulanır ve entity'ye yazılır.
/// </summary>
public interface IHaldusPatch
{
    bool IsSet(string propertyName);
}

/// <summary>
/// PATCH isteğinde, gönderildiyse dolu olması gereken alanı işaretler. Kuralı
/// <see cref="RequiredAttribute"/> ile aynıdır; onun yerine kullanılır, çünkü o OpenAPI şemasında alanı
/// zorunlu gösterirdi, oysa PATCH'te her alan isteğe bağlıdır.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotNullWhenSetAttribute : ValidationAttribute
{
    /// <summary>Boş veya yalnızca boşluktan oluşan metin kabul edilsin mi. Varsayılanı, RequiredAttribute gibi, hayırdır.</summary>
    public bool AllowEmptyStrings { get; set; }

    public override bool IsValid(object? value) =>
        value is not null && (AllowEmptyStrings || value is not string text || !string.IsNullOrWhiteSpace(text));
}
