namespace Haldus.Sample.Shop;

/// <summary>
/// Yalnızca testler ve yerel denemeler içindir (<c>Shop:DemoIdentity</c>): kiracıyı ve kullanıcıyı istek
/// başlıklarından okur. Başlıkları istemci belirlediği için güvenli değildir ve varsayılan olarak kapalıdır; örnek
/// uygulama normalde Haldus'un varsayılanı olan claim tabanlı çözümlemeyi, gerçek kimlik doğrulamayla
/// (<c>Features/Auth</c>) birlikte kullanır.
/// </summary>
public sealed class DemoIdentity(IHttpContextAccessor accessor) : ITenantProvider, ICurrentUser, ICurrentUserRoles
{
    public const string TenantHeader = "X-Tenant-Id";
    public const string UserHeader = "X-User-Id";

    /// <summary>Kullanıcının rolleri, virgülle ayrılmış: <c>manager,finance</c>.</summary>
    public const string RolesHeader = "X-User-Roles";

    public bool IsInRole(string role) =>
        accessor.HttpContext?.Request.Headers[RolesHeader].ToString()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(role, StringComparer.OrdinalIgnoreCase) == true;

    public Guid? TenantId =>
        Guid.TryParse(accessor.HttpContext?.Request.Headers[TenantHeader], out var tenantId) ? tenantId : null;

    public string? UserId => accessor.HttpContext?.Request.Headers[UserHeader] is { Count: > 0 } values
        ? values.ToString()
        : null;
}
