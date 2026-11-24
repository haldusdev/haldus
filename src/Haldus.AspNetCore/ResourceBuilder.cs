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

    IApplicationBuilder IEndpointRouteBuilder.CreateApplicationBuilder() =>
        ((IEndpointRouteBuilder)Group).CreateApplicationBuilder();

    void IEndpointConventionBuilder.Add(Action<EndpointBuilder> convention) =>
        ((IEndpointConventionBuilder)Group).Add(convention);

    void IEndpointConventionBuilder.Finally(Action<EndpointBuilder> finallyConvention) =>
        ((IEndpointConventionBuilder)Group).Finally(finallyConvention);
}
