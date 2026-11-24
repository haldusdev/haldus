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

/// <summary>Kiracıya bağlı kaynakların endpoint'lerinde, kiracısı çözülemeyen isteği reddeder.</summary>
public sealed class HaldusTenantRequiredFilter(ITenantProvider tenants, IHaldusMessages messages) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        tenants.TenantId is null ? HaldusProblems.TenantRequired(messages) : await next(context);
}
