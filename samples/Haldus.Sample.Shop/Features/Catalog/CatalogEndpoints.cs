namespace Haldus.Sample.Shop.Features.Catalog;

public static class CatalogEndpoints
{
    public static void MapCatalog(this IEndpointRouteBuilder app)
    {
        app.MapCrud<Category>();
        app.MapCrud<Product>();

        // Marka silinmez, yalnızca arşivlenir.
        app.MapCrud<Brand>()
            .Archivable()
            .Without(CrudOperation.Delete);
    }
}
