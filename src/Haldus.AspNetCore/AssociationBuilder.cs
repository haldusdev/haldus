using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Haldus;

/// <summary>
/// <c>MapAssociation</c> çağrısının döndürdüğü builder. Zincir metotları çalışma zamanında bir şey yapmaz;
/// generator onları derleme zamanında okur. Builder aynı zamanda bağın route group'u gibi davranır.
/// </summary>
public sealed class AssociationBuilder<TLeft, TRight>(RouteGroupBuilder group) : IEndpointConventionBuilder
    where TLeft : class
    where TRight : class
{
    public RouteGroupBuilder Group { get; } = group;

    /// <summary>
    /// Bağı geçerli kullanıcıya bağlar (favori, yıldız, oy). İlk tip argümanı artık bir uç değil, bağın kendisidir:
    /// Haldus onu üretir ve her kullanıcı yalnızca kendi bağlarını görür. Uçlar <c>/me</c> altındadır:
    /// <c>PUT</c> ve <c>DELETE /me/favorites/{productId}</c>, <c>GET /me/favorites</c>.
    /// </summary>
    public AssociationBuilder<TLeft, TRight> ForCurrentUser() => this;

    void IEndpointConventionBuilder.Add(Action<EndpointBuilder> convention) =>
        ((IEndpointConventionBuilder)Group).Add(convention);

    void IEndpointConventionBuilder.Finally(Action<EndpointBuilder> finallyConvention) =>
        ((IEndpointConventionBuilder)Group).Finally(finallyConvention);
}