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
        // Kalemler listenin içinde sıralıdır: POST …/shopping-list-items/{id}/move  { "position": 0 }
        app.MapCrud<ShoppingListItem>()
            .Under<ShoppingList>()
            .Orderable();

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

        // Kullanıcının sepeti: /me/baskets. Satırları sepetin isteğine gömülüdür; sepet kullanıldıkça yaşar.
        var baskets = app.MapCrud<Basket>()
            .OwnedByCurrentUser()
            .Expiring(TimeSpan.FromDays(30), sliding: true);

        baskets.MapDetail<BasketItem>();

        // Oturum açınca misafir sepeti kullanıcının sepetine katılır: POST /me/baskets/{id}/merge  { "guestToken": "…" }
        // Birleştirme kuralı alana özgüdür; MergeBasketHandler sınıfındadır.
        baskets.MapAction<MergeBasket>();

        // Misafir sepetinin sahibi bir kullanıcı değil, yalnızca onu oluşturanın bildiği token'dır. Token oluşturma
        // yanıtında bir kez gösterilir ve X-Access-Token başlığında taşınır:
        // GET /guest-baskets/by-token, POST /guest-baskets/by-token/add-basket-item
        // Okuma, liste ve düzenleme uçları kapalıdır (yalnızca oluşturma açık); sepet token olmadan okunamaz.
        // Aksiyonun kimlikle çalışan adresi (/guest-baskets/{id}/add-basket-item) yine de üretilir; gerçek bir
        // uygulamada kaynağa yetkilendirme eklenir (token uçları oturumsuz kalır).
        var guests = app.MapCrud<GuestBasket>()
            .Only(CrudOperation.Create)
            .Expiring(TimeSpan.FromDays(7), sliding: true)
            .TokenAccess(prefix: "basket", "add-basket-item");

        guests.MapDetail<GuestBasketItem>();
        guests.MapAction<AddBasketItem>();
    }
}
