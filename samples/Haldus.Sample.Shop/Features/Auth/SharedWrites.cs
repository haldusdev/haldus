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
/// Kural kapalı başlar: kapsamını bildirmeyen, yazan her uç (entity'ye bağlı olmayan aksiyonlar, elle yazılmış uçlar)
/// ortak sayılır. Bir uç bu kuralın dışında kalacaksa kendi yetki kuralını bildirir
/// (<c>app.MapCrud&lt;Review&gt;().RequireAuthorization()</c>): kendi kuralı olan uca varsayılan kural uygulanmaz.
/// </summary>
public sealed class SharedWrites : IAuthorizationRequirement
{
    public const string StaffRole = "staff";
}

public sealed class SharedWritesHandler : AuthorizationHandler<SharedWrites>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SharedWrites requirement)
    {
        if (context.Resource is HttpContext http && (IsRead(http) || IsScoped(http) || context.User.IsInRole(SharedWrites.StaffRole)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    // Okuma: güvenli metotlar ve Haldus'un "yazmaz" diye işaretlediği POST uçları (arama, toplu okuma, önizleme).
    // Adresin sonuna bakılmaz: bir aksiyonun veya istemcinin verdiği bir anahtarın adı da "search" olabilir.
    private static bool IsRead(HttpContext http) =>
        HttpMethods.IsGet(http.Request.Method)
        || HttpMethods.IsHead(http.Request.Method)
        || HttpMethods.IsOptions(http.Request.Method)
        || http.GetEndpoint()?.Metadata.GetMetadata<HaldusReadOnly>() is not null;

    // Kural kapalı başlar: yazan bir uç ancak kapsamı açıkça bir kiracı veya bir kullanıcıysa serbesttir. Kapsamını
    // bildirmeyen uç (entity'ye bağlı olmayan aksiyon, elle yazılmış uç) ortak sayılır ve personel ister; herkese açık
    // olacaksa kendi yetki kuralını bildirir.
    private static bool IsScoped(HttpContext http) =>
        http.GetEndpoint()?.Metadata.GetMetadata<HaldusResourceScope>() is { IsShared: false };
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