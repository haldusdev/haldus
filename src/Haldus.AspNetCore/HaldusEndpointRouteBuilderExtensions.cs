using Microsoft.AspNetCore.Routing;

namespace Haldus;

public static class HaldusEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Kendi kimliği ve yaşam döngüsü olan bağımsız bir entity için liste, tekil okuma, oluşturma,
    /// güncelleme ve silme endpoint'lerini eşler.
    /// </summary>
    public static ResourceBuilder<TEntity> MapCrud<TEntity>(this IEndpointRouteBuilder endpoints)
        where TEntity : class =>
        new(HaldusEndpointRegistry.Map<TEntity>(endpoints, nameof(MapCrud)));
}
