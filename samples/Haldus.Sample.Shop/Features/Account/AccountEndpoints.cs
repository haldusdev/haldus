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
            .Auditable();

        // Listenin altındaki kalemler de onun adresini izler: /me/shopping-lists/{shoppingListId}/shopping-list-items
        // Liste görünmüyorsa kalemleri de görünmez.
        app.MapCrud<ShoppingListItem>()
            .Under<ShoppingList>();

        // underMe: false adresi değiştirmez: /products/{productId}/product-notes
        // Ürünü herkes görür, ama herkes yalnızca kendi notlarını.
        app.MapCrud<ProductNote>()
            .Under<Product>()
            .OwnedByCurrentUser(underMe: false);

        // Kullanıcıyı bir kayda bağlar: PUT ve DELETE /me/favorites/{productId}, GET /me/favorites (ürünleri döner).
        // İlk tip argümanı bağın kendisidir; Favorite entity'si üretilir.
        app.MapAssociation<Favorite, Product>().ForCurrentUser();
    }
}
