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

    /// <summary>
    /// Bağa bir durum makinesi verir (davet edilmiş / etkin üyelik gibi). <c>Status</c> alanı üretilir, yeni bağda
    /// başlangıç değerini alır, istekle yazılamaz ve yalnızca <see cref="MapTransition"/> ile bildirilen geçişlerle
    /// değişir.
    /// </summary>
    /// <param name="initial">Yeni bağın durumu.</param>
    public AssociationBuilder<TLeft, TRight> Workflow<TStatus>(TStatus initial = default)
        where TStatus : struct, Enum => this;

    /// <summary>
    /// Bağın durumunu değiştiren, gövdesiz bir geçiş bildirir:
    /// <c>POST /organizations/{organizationId}/users/{userId}/activate</c>. Bağ sayılan durumlardan birinde değilse
    /// 409 döner. Uç üretilen kodda eşlenir; bu çağrı çalışma zamanında bir şey yapmaz.
    /// </summary>
    /// <param name="name">Adresin son parçası; sabit olmalıdır.</param>
    public AssociationBuilder<TLeft, TRight> MapTransition(string name, StateSet from, Enum to) => this;

    void IEndpointConventionBuilder.Add(Action<EndpointBuilder> convention) =>
        ((IEndpointConventionBuilder)Group).Add(convention);

    void IEndpointConventionBuilder.Finally(Action<EndpointBuilder> finallyConvention) =>
        ((IEndpointConventionBuilder)Group).Finally(finallyConvention);
}