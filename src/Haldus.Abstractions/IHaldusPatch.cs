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

/// <summary>
/// Zorunlu değer tipli alanı işaretler. İstekte hiç gönderilmeyen değer tipli alan, tipinin varsayılan değerini alır
/// (<see cref="DateOnly"/> için 0001-01-01) ve <see cref="RequiredAttribute"/> onu yakalamaz; bu attribute varsayılan
/// değeri gönderilmemiş sayar. Boş değer geçerlidir: PATCH isteğinde gönderilmeyen alan böyle gelir.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class HasValueAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value switch
    {
        null => true,
        DateOnly date => date != default,
        DateTime time => time != default,
        DateTimeOffset time => time != default,
        Guid guid => guid != Guid.Empty,
        _ => true,
    };
}
