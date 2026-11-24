namespace Haldus.Sample.Shop.Features.Catalog;

public static class CatalogEndpoints
{
    public static void MapCatalog(this IEndpointRouteBuilder app)
    {
        app.MapCrud<Category>();

        // Basit süzme sorgu parametreleriyle, VE/VEYA gerektiren aramalar POST /products/search ile yapılır.
        app.MapCrud<Product>()
            .Filterable<ProductFilter>()
            .Searchable(product => product.Name, product => product.Price, product => product.Description);

        // Marka silinmez, yalnızca arşivlenir.
        app.MapCrud<Brand>()
            .Archivable()
            .Without(CrudOperation.Delete);
    }
}
