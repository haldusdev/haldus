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
                product => product.CategoryId)

            // Toplu uçlar: POST /products/batch-get, batch-create, batch-update, batch-delete.
            // Varsayılan atomiktir: bir öğe reddedilirse hiçbiri kaydedilmez.
            .Batch();

        // Ürünler ve etiketler çoktan-çoğa bağlanır: PUT /products/{productId}/tags/{tagId}
        // Bağ kayıtlarının entity'si (ProductTag) üretilir.
        // Etiketlerde kısmi başarı: geçerli öğeler kaydedilir, yanıt 207 ile öğe başına sonuç döner.
        app.MapCrud<Tag>()
            .Batch(atomic: false, maxItems: 5);
        app.MapAssociation<Product, Tag>();

        // Marka silinmez, yalnızca arşivlenir.
        app.MapCrud<Brand>()
            .Archivable()
            .Without(CrudOperation.Delete);
    }
}
