namespace Haldus.Sample.Shop.Features.Catalog;

public static class CatalogEndpoints
{
    public static void MapCatalog(this IEndpointRouteBuilder app)
    {
        // Kategoriler bir ağaçtır: ParentId alanı üretilir, bir kategori kendi altına taşınamaz.
        app.MapCrud<Category>()
            .Hierarchical();

        // Basit süzme sorgu parametreleriyle, VE/VEYA gerektiren aramalar POST /products/search ile yapılır.
        // Ürün isteğe bağlı olarak bir kategoriye bağlanır; ürünü olan kategori silinemez.
        app.MapCrud<Product>()
            .References<Category>(onDelete: OnDelete.Restrict, required: false)
            .Filterable<ProductFilter>()
            .Searchable(
                product => product.Name,
                product => product.Price,
                product => product.Description,
                product => product.CategoryId);

        // Marka silinmez, yalnızca arşivlenir.
        app.MapCrud<Brand>()
            .Archivable()
            .Without(CrudOperation.Delete);
    }
}
