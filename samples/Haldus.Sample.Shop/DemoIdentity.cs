namespace Haldus.Sample.Shop;

/// <summary>
/// Yalnızca örnek içindir: kiracıyı ve kullanıcıyı istek başlıklarından okur. Başlıkları istemci
/// belirlediği için güvenli değildir; gerçek bir uygulamada Haldus'un varsayılanı olan claim tabanlı
/// çözümleme (kimlik doğrulamayla birlikte) kullanılmalıdır.
/// </summary>
public sealed class DemoIdentity(IHttpContextAccessor accessor) : ITenantProvider, ICurrentUser
{
    public const string TenantHeader = "X-Tenant-Id";
    public const string UserHeader = "X-User-Id";

    public Guid? TenantId =>
        Guid.TryParse(accessor.HttpContext?.Request.Headers[TenantHeader], out var tenantId) ? tenantId : null;

    public string? UserId => accessor.HttpContext?.Request.Headers[UserHeader] is { Count: > 0 } values
        ? values.ToString()
        : null;
}
