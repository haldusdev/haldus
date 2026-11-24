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

    IApplicationBuilder IEndpointRouteBuilder.CreateApplicationBuilder() =>
        ((IEndpointRouteBuilder)Group).CreateApplicationBuilder();

    void IEndpointConventionBuilder.Add(Action<EndpointBuilder> convention) =>
        ((IEndpointConventionBuilder)Group).Add(convention);

    void IEndpointConventionBuilder.Finally(Action<EndpointBuilder> finallyConvention) =>
        ((IEndpointConventionBuilder)Group).Finally(finallyConvention);
}
