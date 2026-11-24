using Haldus.Sample.Shop.Features.Catalog;

namespace Haldus.Sample.Shop.Features.Account;

public static class AccountEndpoints
{
    public static void MapAccount(this IEndpointRouteBuilder app)
    {
        // Kayıtlar onları oluşturan kullanıcıya aittir ve /me altında sunulur: /me/shopping-lists
        // OwnerId alanı üretilir; kullanıcısı çözülemeyen istek 401 alır.
        app.MapCrud<ShoppingList>()
            .OwnedByCurrentUser()
            .Auditable()

            // Kimlik önekli bir metindir: list_01j9…
            .KeyStrategy(KeyStrategy.Prefixed, "list")

            // Liste adı kullanıcı başına benzersizdir ve adıyla da okunur: GET /me/shopping-lists/by-name/{name}
            .NaturalKey(list => list.Name)
            .Sortable(list => list.Name, list => list.CreatedAt);

        // Listenin altındaki kalemler de onun adresini izler: /me/shopping-lists/{shoppingListId}/shopping-list-items
        // Liste görünmüyorsa kalemleri de görünmez.
        app.MapCrud<ShoppingListItem>()
            .Under<ShoppingList>();

        // underMe: false adresi değiştirmez: /products/{productId}/product-notes
        // Ürünü herkes görür, ama herkes yalnızca kendi notlarını.
        app.MapCrud<ProductNote>()
            .Under<Product>()
            .OwnedByCurrentUser(underMe: false)

            // Notun kimliği ne zaman yazıldığını ele vermez.
            .KeyStrategy(KeyStrategy.Random);

        // Kullanıcıyı bir kayda bağlar: PUT ve DELETE /me/favorites/{productId}, GET /me/favorites (ürünleri döner).
        // İlk tip argümanı bağın kendisidir; Favorite entity'si üretilir.
        app.MapAssociation<Favorite, Product>().ForCurrentUser();
    }
}
