namespace Haldus.Sample.Shop.Features.Catalog;

/// <summary>
/// <c>GET /products</c> için sorgu parametreleri: <c>?search=klav&amp;minPrice=100&amp;maxPrice=500</c>.
/// Hangi alanın nasıl süzüleceği tamamen burada belirlenir.
/// </summary>
public sealed class ProductFilter : IFilter<Product>
{
    public string? Search { get; set; }

    public decimal? MinPrice { get; set; }

    public decimal? MaxPrice { get; set; }

    public bool OnlyDescribed { get; set; }

    public IQueryable<Product> Apply(IQueryable<Product> query) => query
        .WhereIf(Search is not null, product => product.Name.ToLower().Contains(Search!.ToLower()))
        .WhereIf(MinPrice is not null, product => product.Price >= MinPrice)
        .WhereIf(MaxPrice is not null, product => product.Price <= MaxPrice)
        .WhereIf(OnlyDescribed, product => product.Description != null);
}
