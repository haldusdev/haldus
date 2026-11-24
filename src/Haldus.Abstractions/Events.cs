using System.Text.Json;

namespace Haldus;

/// <summary>
/// Kayıt değiştikçe olay yayınlanır (<c>.PublishesEvents()</c>): oluşturuldu, güncellendi, silindi ve her geçiş.
/// </summary>
public interface IPublishesEvents;

/// <summary>Bir kaydın başına gelen bir şey; kayıt kalıcı olduktan sonra yayınlanır.</summary>
/// <param name="Id">Olayın sırası; aynı veritabanındaki olaylar arasında artar. Yinelenen teslimatı ayırt etmek için kullanılır.</param>
/// <param name="Type">Olayın türü: <c>order.created</c>, <c>order.updated</c>, <c>order.deleted</c> veya geçişin adıyla <c>order.place</c>.</param>
/// <param name="Resource">Kaydın kaynağı: <c>order</c>.</param>
/// <param name="RecordId">Kaydın anahtarı, metin olarak.</param>
/// <param name="TenantId">Kayıt kiracıya aitse kiracısı.</param>
/// <param name="UserId">Değişikliği yapan kullanıcı.</param>
/// <param name="Data">Kaydın, olay anındaki hali; kaynağın yanıt biçimindedir.</param>
public sealed record HaldusEvent(
    long Id,
    string Type,
    string Resource,
    string RecordId,
    Guid? TenantId,
    string? UserId,
    JsonElement Data,
    DateTimeOffset OccurredAt);

/// <summary>
/// Olayları uygulamanın dışına taşır (bir mesaj kuyruğuna, bir webhook'a, başka bir modüle). Uygulama bir veya birkaç
/// tanesini servis olarak kaydeder; Haldus her olayı, yazıldığı sırayla hepsine teslim eder. Bir yayıncı hata
/// fırlatırsa olay sonra yeniden denenir; bu yüzden aynı olay birden çok kez gelebilir ve yayıncı buna dayanıklı
/// olmalıdır (<see cref="HaldusEvent.Id"/>).
/// </summary>
public interface IHaldusEventPublisher
{
    Task PublishAsync(HaldusEvent published, CancellationToken cancellationToken);
}
