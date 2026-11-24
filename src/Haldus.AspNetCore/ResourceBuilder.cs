using System.Linq.Expressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Haldus;

/// <summary>
/// Bir <c>Map*</c> çağrısının döndürdüğü builder. Trait ve ilişki zincir metotları çalışma zamanında
/// bir şey yapmaz; generator onları derleme zamanında okur. Builder aynı zamanda bir route group gibi
/// davranır, yani standart Minimal API çağrıları zincire eklenebilir.
/// </summary>
public sealed class ResourceBuilder<TEntity>(RouteGroupBuilder group) : IEndpointRouteBuilder, IEndpointConventionBuilder
    where TEntity : class
{
    public RouteGroupBuilder Group { get; } = group;

    IServiceProvider IEndpointRouteBuilder.ServiceProvider => ((IEndpointRouteBuilder)Group).ServiceProvider;

    ICollection<EndpointDataSource> IEndpointRouteBuilder.DataSources => ((IEndpointRouteBuilder)Group).DataSources;

    // Aşağıdaki iki metot uzantı değil üye metottur: yalnızca böyle .Filterable<ProductFilter>() yazılabilir,
    // çünkü C# bir uzantı metodunda tip argümanlarının bir kısmını çıkarsayamaz.

    /// <summary>
    /// Liste endpoint'ine, sorgu parametrelerinden doldurulan bir filtre bağlar. Filtrenin herkese açık,
    /// yazılabilir skaler özellikleri camelCase sorgu parametresi olur.
    /// </summary>
    public ResourceBuilder<TEntity> Filterable<TFilter>()
        where TFilter : IFilter<TEntity>, new() => this;

    /// <summary>
    /// <c>POST /search</c> ucunu ekler: gövdede VE/VEYA ile birleştirilebilen tipli koşullar ve sıralama.
    /// Yalnızca burada sayılan alanlar aranabilir ve sıralanabilir.
    /// </summary>
    public ResourceBuilder<TEntity> Searchable(params Expression<Func<TEntity, object?>>[] fields) => this;

    /// <summary>
    /// Başka bir kaynağa kimlikle referans verir: yabancı anahtar alanı (ör. <c>CategoryId</c>) üretilir ve
    /// yazılırken hedefin var olduğu doğrulanır.
    /// </summary>
    /// <param name="onDelete">Hedef silindiğinde bu kayda ne olacağı.</param>
    /// <param name="required">Referans zorunlu mu.</param>
    public ResourceBuilder<TEntity> References<TTarget>(OnDelete onDelete = OnDelete.Restrict, bool required = true)
        where TTarget : class => this;

    /// <summary>Yabancı anahtar alanını kendin yazdıysan onu gösterirsin; zorunluluğu alanın tipinden okunur.</summary>
    public ResourceBuilder<TEntity> References<TTarget>(
        Expression<Func<TEntity, object?>> foreignKey,
        OnDelete onDelete = OnDelete.Restrict)
        where TTarget : class => this;

    /// <summary>
    /// Kaynağı bir ebeveynin adresi altına yerleştirir (<c>/projects/{projectId}/tasks</c>). Kaynak yine
    /// kendi kimliğine ve kurallarına sahiptir; ebeveyn istek gövdesinden değil adresten gelir.
    /// </summary>
    public ResourceBuilder<TEntity> Under<TParent>(OnDelete onDelete = OnDelete.Cascade)
        where TParent : class => this;

    /// <summary>
    /// Kayıtları bir ağaca dizer: isteğe bağlı <c>ParentId</c> alanı üretilir ve bir düğümün kendi altına
    /// taşınması engellenir.
    /// </summary>
    public ResourceBuilder<TEntity> Hierarchical() => this;

    /// <summary>
    /// <typeparamref name="TValue"/> tipindeki alanları gömülü değer nesnesi sayar (para, adres gibi):
    /// kendi tablosu ve kimliği olmaz, sahibinin kolonlarında saklanır ve DTO'larda iç içe nesne olarak görünür.
    /// </summary>
    public ResourceBuilder<TEntity> Embeds<TValue>() => this;

    /// <summary>
    /// Bu kaynağın sahip olduğu satırları bildirir (sipariş → sipariş satırı). Satır kendi başına bir kaynak
    /// değildir: bu kaynak silinince gider ve onun görünürlüğünü devralır. Varsayılan olarak bu kaynağın
    /// istek ve yanıtlarında gömülü taşınır; <see cref="DetailBuilder{TEntity, TDetail}.AsSubresource"/> ile
    /// kendi uçlarına ayrılır.
    /// </summary>
    public DetailBuilder<TEntity, TDetail> MapDetail<TDetail>()
        where TDetail : class
    {
        // Satır ayrı uçlara sahipse onlar bu kaynağın grubuna, yani adresinin altına eşlenir.
        HaldusEndpointRegistry.MapIfRegistered<TDetail>(Group);
        return new DetailBuilder<TEntity, TDetail>();
    }

    /// <summary>
    /// Kayda bir durum makinesi ekler: <c>Status</c> alanı üretilir, istek gövdesinden yazılamaz ve yalnızca
    /// <c>MapTransition</c> ile bildirilen geçişlerle değişir.
    /// </summary>
    /// <param name="initial">Yeni kaydın durumu.</param>
    public ResourceBuilder<TEntity> Workflow<TStatus>(TStatus initial = default)
        where TStatus : struct, Enum => this;

    /// <summary>Güncellemeye (<c>PUT</c>, <c>PATCH</c>) yalnızca sayılan durumlarda izin verir; diğerlerinde 409 döner.</summary>
    public ResourceBuilder<TEntity> EditableIn(params Enum[] states) => this;

    /// <summary>Silmeye yalnızca sayılan durumlarda izin verir; diğerlerinde 409 döner.</summary>
    public ResourceBuilder<TEntity> DeletableIn(params Enum[] states) => this;

    /// <summary>
    /// Gövdesiz bir geçiş ekler: <c>POST /{id}/{name}</c>. Kayıt <paramref name="from"/> durumlarından
    /// birindeyse <paramref name="to"/> durumuna geçer; değilse 409 döner.
    /// </summary>
    public RouteHandlerBuilder MapTransition(string name, StateSet from, Enum to) =>
        HaldusTransitionRegistry.Map<TEntity>(Group, name, apply: null);

    /// <inheritdoc cref="MapTransition(string, StateSet, Enum)"/>
    /// <param name="apply">Durum değişmeden önce kayıt üzerinde çalışır.</param>
    public RouteHandlerBuilder MapTransition(string name, StateSet from, Enum to, Action<TEntity> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        return HaldusTransitionRegistry.Map<TEntity>(Group, name, (entity, _, _) =>
        {
            apply(entity);
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc cref="MapTransition(string, StateSet, Enum)"/>
    /// <param name="apply">Durum değişmeden önce kayıt üzerinde çalışır; bağımlılıklarını isteğin servis sağlayıcısından alır.</param>
    public RouteHandlerBuilder MapTransition(
        string name,
        StateSet from,
        Enum to,
        Func<TEntity, IServiceProvider, CancellationToken, Task> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return HaldusTransitionRegistry.Map(Group, name, apply);
    }

    /// <summary>
    /// İstek gövdesi alan bir geçiş ekler; adresi istek tipinin adından türetilir
    /// (<c>CancelOrder</c> → <c>POST /{id}/cancel</c>). İş mantığı ya istek tipindedir
    /// (<see cref="ITransition{TEntity}"/>) ya da Haldus'un bulduğu bir
    /// <see cref="ITransitionHandler{TEntity, TRequest}"/> sınıfındadır.
    /// </summary>
    /// <param name="name">Adresin son parçası; verilmezse istek tipinin adından türetilir.</param>
    public RouteHandlerBuilder MapTransition<TRequest>(StateSet from, Enum to, string? name = null)
        where TRequest : class =>
        HaldusTransitionRegistry.Map<TEntity, TRequest>(Group, name, apply: null);

    /// <inheritdoc cref="MapTransition{TRequest}(StateSet, Enum, string?)"/>
    /// <param name="apply">Durum değişmeden önce kayıt üzerinde çalışır.</param>
    public RouteHandlerBuilder MapTransition<TRequest>(StateSet from, Enum to, Action<TEntity, TRequest> apply, string? name = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(apply);

        return HaldusTransitionRegistry.Map<TEntity, TRequest>(Group, name, (entity, request, _, _) =>
        {
            apply(entity, request);
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc cref="MapTransition{TRequest}(StateSet, Enum, string?)"/>
    /// <param name="apply">Durum değişmeden önce kayıt üzerinde çalışır; bağımlılıklarını isteğin servis sağlayıcısından alır.</param>
    public RouteHandlerBuilder MapTransition<TRequest>(
        StateSet from,
        Enum to,
        Func<TEntity, TRequest, IServiceProvider, CancellationToken, Task> apply,
        string? name = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(apply);
        return HaldusTransitionRegistry.Map(Group, name, apply);
    }

    IApplicationBuilder IEndpointRouteBuilder.CreateApplicationBuilder() =>
        ((IEndpointRouteBuilder)Group).CreateApplicationBuilder();

    void IEndpointConventionBuilder.Add(Action<EndpointBuilder> convention) =>
        ((IEndpointConventionBuilder)Group).Add(convention);

    void IEndpointConventionBuilder.Finally(Action<EndpointBuilder> finallyConvention) =>
        ((IEndpointConventionBuilder)Group).Finally(finallyConvention);
}
