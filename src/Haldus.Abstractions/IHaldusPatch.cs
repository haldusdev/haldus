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
/// PATCH isteğinde gönderildiyse null olamayan alanı işaretler. <see cref="RequiredAttribute"/> yerine
/// kullanılır, çünkü o OpenAPI şemasında alanı zorunlu gösterirdi; oysa PATCH'te her alan isteğe bağlıdır.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotNullWhenSetAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value is not null;
}
