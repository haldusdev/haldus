using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Haldus;

public sealed class HaldusOptions
{
    /// <summary>Kiracı kimliğini taşıyan claim. Değeri bir Guid olmalıdır.</summary>
    public string TenantClaimType { get; set; } = "tenant_id";

    /// <summary>Kullanıcı kimliğini taşıyan claim.</summary>
    public string UserClaimType { get; set; } = ClaimTypes.NameIdentifier;

    /// <summary>
    /// <c>Idempotency-Key</c> ile saklanan yanıtların ne kadar süre yeniden döndürüleceği. Süre dolduktan
    /// sonra aynı anahtar yeni bir istek sayılır.
    /// </summary>
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Süresi dolan (<c>.Expiring()</c>) kayıtların, süreleri dolduktan ne kadar sonra fiziksel olarak
    /// silineceği. Varsayılan <c>null</c>: kayıtlar gizlenir ama hiç silinmez. Silme geri alınamaz ve o
    /// kayıtların zincirleme silinen alt kayıtlarını da götürür.
    /// </summary>
    public TimeSpan? PurgeExpiredAfter { get; set; }

    /// <summary>Bakımın (saklama süresi geçmiş idempotency yanıtları, süresi dolmuş kayıtlar) ne sıklıkla çalışacağı.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Bekleyen olayların (<c>.PublishesEvents()</c>) en geç ne kadar sonra yayınlanacağı. Aynı süreçte yazılan olay
    /// beklemeden yayınlanır; bu süre, başka bir sunucunun yazdığı veya açık bir transaction'ın içinde yazılan olaylar
    /// içindir.
    /// </summary>
    public TimeSpan EventPollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

internal sealed class ClaimsTenantProvider(IHttpContextAccessor accessor, IOptions<HaldusOptions> options) : ITenantProvider
{
    public Guid? TenantId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(options.Value.TenantClaimType), out var tenantId)
            ? tenantId
            : null;
}

internal sealed class ClaimsCurrentUser(IHttpContextAccessor accessor, IOptions<HaldusOptions> options) : ICurrentUser
{
    public string? UserId => accessor.HttpContext?.User.FindFirstValue(options.Value.UserClaimType);
}

/// <summary>Kullanıcının rollerini kimlik doğrulamanın kurduğu kimlikten (rol claim'leri) okur.</summary>
internal sealed class ClaimsCurrentUserRoles(IHttpContextAccessor accessor) : ICurrentUserRoles
{
    public bool IsInRole(string role) => accessor.HttpContext?.User.IsInRole(role) == true;
}

/// <summary>Üretilen uçların kimlikle ilgili soruları.</summary>
public static class HaldusRoles
{
    /// <summary>Geçerli isteğin kullanıcısı verilen rolde mi.</summary>
    public static bool IsInRole(HttpContext httpContext, string role)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.RequestServices.GetService(typeof(ICurrentUserRoles)) is ICurrentUserRoles roles && roles.IsInRole(role);
    }
}

/// <summary>Kiracıya bağlı kaynakların endpoint'lerinde, kiracısı çözülemeyen isteği reddeder.</summary>
public sealed class HaldusTenantRequiredFilter(ITenantProvider tenants, IHaldusMessages messages) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        // Token ile erişilen uca gelen kişinin kiracısı yoktur; onu kayıt tanıtır.
        tenants.TenantId is null && !HaldusTokenAccess.Applies(context.HttpContext)
            ? HaldusProblems.TenantRequired(messages)
            : await next(context);
}

/// <summary>Kullanıcıya ait kaynakların endpoint'lerinde, kullanıcısı çözülemeyen isteği reddeder.</summary>
public sealed class HaldusUserRequiredFilter(ICurrentUser user, IHaldusMessages messages) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        string.IsNullOrEmpty(user.UserId) && !HaldusTokenAccess.Applies(context.HttpContext)
            ? HaldusProblems.UserRequired(messages)
            : await next(context);
}