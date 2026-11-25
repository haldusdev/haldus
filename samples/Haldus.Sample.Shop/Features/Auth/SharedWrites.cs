using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Localization;

namespace Haldus.Sample.Shop.Features.Auth;

/// <summary>
/// Örnek mağazanın yetki kuralı: kimseye ait olmayan, ortak kaynakları (ürünler, kategoriler, özellik bayrakları…)
/// yalnızca personel yazar; herkes okur. Bir kiracıya veya bir kullanıcıya ait kaynaklar bu kuralın dışındadır:
/// onları zaten yalnızca sahibi görür.
/// <para>
/// Kaynağın kime ait olduğu elle sayılmaz; Haldus'un ürettiği her uç bunu metadata olarak taşır
/// (<see cref="HaldusResourceScope"/>). Böylece sonradan eklenen ortak bir kaynak kendiliğinden korunur.
/// </para>
/// Bir kaynak bu kuralın dışında kalacaksa kendi yetki kuralını bildirir
/// (<c>app.MapCrud&lt;Review&gt;().RequireAuthorization()</c>): kendi kuralı olan uca varsayılan kural uygulanmaz.
/// </summary>
public sealed class SharedWrites : IAuthorizationRequirement
{
    public const string StaffRole = "staff";
}

public sealed class SharedWritesHandler : AuthorizationHandler<SharedWrites>
{
    // Gövde taşıdığı için POST ile yapılan okumalar; hiçbir şey yazmazlar.
    private static readonly string[] ReadOnlyPosts = ["/search", "/batch-get", "/preview"];

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SharedWrites requirement)
    {
        if (context.Resource is not HttpContext http
            || http.GetEndpoint()?.Metadata.GetMetadata<HaldusResourceScope>() is not { IsShared: true }
            || IsRead(http.Request)
            || context.User.IsInRole(SharedWrites.StaffRole))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool IsRead(HttpRequest request) =>
        HttpMethods.IsGet(request.Method)
        || HttpMethods.IsHead(request.Method)
        || HttpMethods.IsOptions(request.Method)
        || (HttpMethods.IsPost(request.Method)
            && ReadOnlyPosts.Any(suffix => request.Path.Value?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true));
}

/// <summary>Yetkisi yetmeyen isteğe boş bir 403 yerine, nedenini isteğin dilinde söyleyen bir yanıt verir.</summary>
public sealed class ForbiddenProblemHandler(IStringLocalizer<ShopMessages> messages) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult) =>
        authorizeResult.Forbidden
            ? Results.Problem(detail: messages["StaffOnly"], statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context)
            : _default.HandleAsync(next, context, policy, authorizeResult);
}