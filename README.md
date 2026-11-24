# Haldus

Archetype ve trait'lerle tekrarlayan backend kodunu derleme zamanında üreten .NET 10 framework'ü.

```csharp
builder.Services.AddHaldus<AppDbContext>();

app.UseHaldus();
app.MapCrud<Product>();
```

```csharp
public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}
```

Bu iki parçadan Haldus şunları üretir:

- `Product` sınıfının `Id` alanını taşıyan diğer yarısı,
- `ProductResponse`, `CreateProductRequest`, `UpdateProductRequest`,
- `GET /products`, `GET /products/{id}`, `POST /products`, `PUT /products/{id}`,
  `DELETE /products/{id}`,
- EF Core model kaydı.

## Archetype'lar

Her archetype bir kaynağın hangi işlemlere sahip olduğunu seçer:

| Archetype | Üretilen uçlar |
|---|---|
| `MapCrud<T>()` | Liste, tekil, oluştur, `PUT`, `PATCH`, sil |
| `MapReadOnly<T>()` | Liste, tekil |
| `MapAppendOnly<T>()` | Liste, tekil, oluştur |
| `MapStore<T>()` | Liste, tekil, `PUT /{key}` (ekle veya güncelle), sil |
| `MapLookup<T>()` | Sayfalamasız liste, tekil, `PUT /{code}`, sil; `Code` ve `Name` alanları |
| `MapSingleton<T>()` | Tek kayıt: `GET`, `PUT`, `PATCH` |
| `MapDetail<T>()` | Sahibine ait satırlar: sahibin isteğine gömülü ya da onun adresi altında ayrı uçlar |
| `MapJob<T>()` | Uzun süren iş: `POST` 202 döner, `GET` durumu gösterir, `POST /{id}/cancel` iptal eder |
| `MapAction<TRequest, TResponse>()` | `POST`: entity'ye bağlı olmayan komut |
| `MapQuery<TRequest, TResponse>()` | `GET`: entity'ye bağlı olmayan hesap |

`MapAction` ve `MapQuery`'nin iş mantığı bir handler sınıfında durur; Haldus onu bulur ve kaydeder:

```csharp
app.MapQuery<ShippingQuoteRequest, ShippingQuote>();

public sealed class ShippingQuoteHandler(AppDbContext db)
    : IQueryHandler<ShippingQuoteRequest, ShippingQuote>
{
    public async Task<ShippingQuote> HandleAsync(ShippingQuoteRequest request, CancellationToken cancellationToken)
    {
        // hesap
    }
}
```

Kısa işler için lambda da verilebilir: `app.MapQuery<EchoRequest, EchoResponse>((request, _) => ...)`.

## İlişkiler

```csharp
app.MapCrud<Category>().Hierarchical();
app.MapCrud<Product>().References<Category>(required: false);
app.MapCrud<Address>().Under<Customer>();
app.MapCrud<Plan>().Embeds<Money>();
app.MapAssociation<Product, Tag>();
```

| Bildirim | Etkisi |
|---|---|
| `.References<T>()` | Yabancı anahtar alanı (`CategoryId`); yazılırken hedefin varlığı doğrulanır |
| `.Under<T>()` | Kaynak ebeveynin adresi altında yaşar: `/customers/{customerId}/addresses` |
| `.Hierarchical()` | `ParentId` ile ağaç; bir düğüm kendi altına taşınamaz |
| `.Embeds<T>()` | O tipteki alanlar iç içe değer nesnesi olur, sahibinin kolonlarında saklanır |
| `MapAssociation<TLeft, TRight>()` | Çoktan-çoğa bağ: `PUT` / `DELETE /products/{id}/tags/{tagId}`, iki yönlü liste |
| `MapAssociation<TLink, T>().ForCurrentUser()` | Geçerli kullanıcıyı bir kayda bağlar (favori, yıldız): `PUT` / `DELETE /me/favorites/{productId}` |

Bir kaynağın görünürlüğü başka bir kayıttan geliyorsa (ebeveyni kiracıya bağlı bir kaynak gibi), o
kaynağa dokunan her sorgu bunu kendiliğinden uygular.

## Detaylar ve hesaplanan alanlar

Bir kaydın satırları (sipariş ve sipariş satırları gibi) sahibinin builder'ından bildirilir:

```csharp
var orders = app.MapCrud<Order>().ConcurrencyChecked();

orders.MapDetail<OrderItem>();                  // siparişin istek ve yanıtına gömülü
orders.MapDetail<OrderNote>().AsSubresource();  // /orders/{orderId}/order-notes
```

Gömülü satırlar sahibiyle tek istekte yazılır. `PUT` satırları eşitler: kimliği verilen satır
güncellenir, verilmeyen eklenir, istekte bulunmayan silinir. Satırlar gönderildikleri sırayla döner.

Entity `IComputed` uygularsa Haldus her yazmadan önce `Compute()` çağırır. Herkese açık setter'ı
olmayan alanlar yalnızca yanıtta görünür:

```csharp
public partial class Order : IComputed
{
    public string CustomerName { get; set; } = "";
    public decimal Total { get; private set; }

    public void Compute() => Total = OrderItems.Sum(item => item.Quantity * item.UnitPrice);
}
```

## Durum makinesi

Durumu olan bir kayıt `.Workflow<TStatus>()` ile bildirilir. `Status` alanı istek gövdesinden yazılamaz;
yalnızca geçişlerle değişir.

```csharp
var orders = app.MapCrud<Order>()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)                                   // PUT / PATCH yalnızca taslakta
    .DeletableIn(OrderStatus.Draft, OrderStatus.Cancelled)
    .Numbered("ORD-{yyyy}-{seq:6}", assignOn: OrderStatus.Placed);   // numara verilirken atanır

orders.MapTransition("ship", from: OrderStatus.Placed, to: OrderStatus.Shipped);
orders.MapTransition<PlaceOrder>(from: OrderStatus.Draft, to: OrderStatus.Placed);
orders.MapTransition<CancelOrder>(from: [OrderStatus.Draft, OrderStatus.Placed], to: OrderStatus.Cancelled);
```

Her geçiş `POST /orders/{id}/ship` biçiminde bir uçtur; kayıt beklenen durumda değilse 409 döner. Durum
kontrolünü ve kaydetmeyi Haldus üretir, iş mantığı üç yoldan biriyle verilir:

```csharp
// 1. İstek kendi etkisini uygular.
public sealed record CancelOrder(string Reason) : ITransition<Order>
{
    public void Apply(Order order) => order.Cancel(Reason);
}

// 2. Bağımlılık gerekiyorsa bir handler sınıfı; Haldus bulur ve kaydeder.
public sealed class PlaceOrderHandler(IStock stock) : ITransitionHandler<Order, PlaceOrder>
{
    public async Task HandleAsync(Order order, PlaceOrder request, CancellationToken cancellationToken)
    {
        if (order.OrderItems.Count == 0)
        {
            throw new TransitionRejectedException("...");   // 422; hiçbir şey kaydedilmez
        }
    }
}

// 3. Kısa işler için lambda.
orders.MapTransition<ReopenOrder>(OrderStatus.Cancelled, OrderStatus.Draft, (order, request) => order.Reopen());
```

## Uzun süren işler

İsteği aşan işler (rapor üretme, içe aktarma) `MapJob` ile bildirilir. Entity işin girdisini ve çıktısını
taşır; durumunu, ilerlemesini ve hatasını Haldus ekler.

```csharp
builder.Services.AddHaldusPostgreSqlJobs<AppDbContext>();   // veya AddHaldusInMemoryJobs()

app.MapJob<ReportExport>().MultiTenant();
```

```csharp
public partial class ReportExport
{
    public int Year { get; set; }                        // girdi: istekten gelir
    public string? DownloadUrl { get; private set; }     // çıktı: yalnızca yanıtta görünür
}

public sealed class ReportExportHandler(AppDbContext db) : IJobHandler<ReportExport>
{
    public async Task HandleAsync(ReportExport job, JobContext context, CancellationToken cancellationToken)
    {
        await context.ReportProgressAsync(50, cancellationToken);
        // ...
    }
}
```

İş, onu başlatan kiracının ve kullanıcının kimliğiyle, isteğin dilinde çalışır. Altyapı ayrı bir pakettir:

| Paket | Kuyruk |
|---|---|
| `Haldus.Jobs.PostgreSql` | Uygulamanın veritabanında; iş kaydıyla aynı transaction'da yazılır, sunucular paylaşır |
| `Haldus.Jobs.InMemory` | Sürecin belleğinde; geliştirme ve tek sunucu için, yeniden başlatmada işler kaybolur |
| `Haldus.Jobs.Hangfire` | Hangfire'da; depolamayı, sunucuyu ve paneli uygulama kurar (`AddHaldusHangfireJobs()`) |

## Entity aksiyonları

Bir kayıt üzerinde çalışan ama durumunu değiştirmeyen işlemler (kuponu kullan, parolayı sıfırla, yeniden
gönder) kaynağın builder'ından bildirilir. Durum makinesi gerekmez; Haldus kaydı bulur, sürümünü denetler,
mantık çalıştıktan sonra kaydeder.

```csharp
var coupons = app.MapCrud<Coupon>();

coupons.MapAction("redeem", coupon => coupon.Redeem());   // POST /coupons/{id}/redeem
coupons.MapAction<ExtendCoupon>();                         // mantık istek tipinde veya bir handler sınıfında
coupons.MapAction<EstimateDiscount, DiscountEstimate>();   // yanıt kaydın kendisi değil, aksiyonun sonucu
```

İş mantığı, geçişlerdeki gibi üç yoldan biriyle verilir: istek `IEntityAction<T>` uygular, bir
`IEntityActionHandler` sınıfı yazılır veya çağrıya lambda verilir.

## Trait'ler ve süzme

Davranışlar `Map*` çağrısının devamına zincirlenir:

```csharp
app.MapCrud<Customer>()
    .MultiTenant()
    .Auditable(trackUser: true)
    .SoftDeletable()
    .ConcurrencyChecked()
    .Filterable<CustomerFilter>()
    .Searchable(customer => customer.Name, customer => customer.CreatedAt);
```

| Zincir metodu | Etkisi |
|---|---|
| `.MultiTenant()` | `TenantId` alanı; sorgular kiracıya göre süzülür |
| `.OwnedByCurrentUser()` | `OwnerId` alanı; kayıt onu oluşturana aittir, yalnızca o görür ve `/me` altında sunulur |
| `.Auditable()` | Oluşturulma ve değiştirilme zamanı, isteğe bağlı kullanıcı |
| `.SoftDeletable()` | Silme geri alınabilir; `restore` ve `purge` uçları |
| `.ConcurrencyChecked()` | `ETag` / `If-Match` ile iyimser eşzamanlılık |
| `.Archivable()` | `archive` ve `unarchive` uçları; arşivlenenler listede gizlenir |
| `.Numbered("ORD-{yyyy}-{seq:6}")` | Benzersiz `Number` alanı; varsayılan olarak boşluksuz, `gapless: false` ile sequence |
| `.Expiring(...)` | `ExpiresAt` alanı; süresi dolan kayıt okunamaz ve yazılamaz, `expire` ucu hemen sonlandırır |
| `.Idempotent()` | Oluşturma ve geçişler `Idempotency-Key` ile tekrara karşı korunur |
| `.Batch()` | Toplu uçlar: `POST /batch-get`, `batch-create`, `batch-update`, `batch-delete`; atomik veya `atomic: false` ile kısmi |
| `.Metadata()` | Metin değerli serbest anahtar-değer çantası; `PATCH` birleştirir, `PUT` tamamını değiştirir |
| `.NaturalKey(x => x.Code)` | Alan benzersiz olur (çakışma 409) ve kayıt onunla da okunur: `GET /coupons/by-code/{code}` |
| `.CreateOnly(...)` | Sayılan alanlar yalnızca `POST` isteğinde yer alır; sonradan değişmez |
| `.KeyStrategy(...)` | Anahtarın biçimi: sıralı GUID (varsayılan), rastgele GUID veya önekli kimlik (`ord_01j9…`) |
| `.Sortable(...)` | Liste sıralanabilir: `?sort=-createdAt,name`; yalnızca sayılan alanlara göre |
| `.Orderable()` | `Position` alanı; liste bu sırayla döner, `POST /{id}/move` kaydı taşır ve kardeşleri yeniden numaralar |
| `.Cloneable()` | `POST /{id}/clone`: alanlar ve gömülü satırlar kopyalanır; isteğe bağlı gövde kopyadaki alanları değiştirir |
| `.Previewable()` | `POST /preview` ve `POST /{id}/preview`: yazmanın sonucunu hesaplar ama kaydetmez |
| `.Without(...)` / `.Only(...)` | Üretilecek standart işlemleri seçer |
| `.Filterable<TFilter>()` | Kendi yazdığın filtre sınıfını liste ucunun sorgu parametrelerine bağlar |
| `.Searchable(...)` | `POST /search`: sayılan alanlarda VE/VEYA koşulları ve sıralama |

## Kullanıcıya ait kaynaklar

```csharp
app.MapCrud<ShoppingList>().OwnedByCurrentUser();                 // /me/shopping-lists
app.MapCrud<ShoppingListItem>().Under<ShoppingList>();            // /me/shopping-lists/{shoppingListId}/shopping-list-items
app.MapCrud<ProductNote>().Under<Product>()
    .OwnedByCurrentUser(underMe: false);                          // /products/{productId}/product-notes
app.MapAssociation<Favorite, Product>().ForCurrentUser();         // /me/favorites/{productId}
```

`.OwnedByCurrentUser()` kaydı onu oluşturan kullanıcıya bağlar: sahip oluştururken atanır, istekte verilemez,
yanıtta yinelenmez ve sonradan değişmez. Başkasının kaydı bulunamaz sayılır (404); kullanıcısı çözülemeyen
istek 401 alır. Kaynak varsayılan olarak `/me` altındadır; `underMe: false` adresi değiştirmeden aynı
korumayı verir.

`.ForCurrentUser()` bir bağı geçerli kullanıcıya bağlar. İlk tip argümanı bir uç değil, üretilecek bağın
adıdır (`Favorite`). `GET /me/favorites` bağlı kayıtları döndürür; adreste kullanıcı yer almadığı için kimse
başkasının bağına ulaşamaz.

## Modüler yapı

Özellikler ayrı sınıf kütüphanelerine bölünebilir. Her modül kendi `Map*` çağrılarını ve entity'lerini
taşır; veritabanı bağlamı proje başına seçilir ve iki seçenek birlikte kullanılabilir.

```csharp
// Ortak bağlam: kendi bağlamı olmayan modüllerin entity'leri buraya girer.
builder.Services.AddHaldus<AppDbContext>();

// Modül başına bağlam: modül kendi şemasına ve migration'larına sahip olur.
builder.Services.AddHaldusModule<CatalogDbContext>();
builder.Services.AddHaldusModule<SalesDbContext>();
```

Modüllerin ayrıca kaydedilmesi gerekmez; ana projenin başvurduğu modüller kendiliğinden bulunur. Bir modül
başka bir modülün kaydına `.References<T>()` ile bağlanabilir: aynı bağlamdaysa gerçek bir yabancı anahtar
kısıtı olur, ayrı bağlamlardaysa referans yalnızca kimlikle tutulur ve varlığı yazarken doğrulanır.

## Durum

İlk sürümün planlanan on aşaması tamamlandı; eksikleri kapatan ikinci plan sürüyor. Model, araştırmadaki 59
gerçek varlığa karşı sınanıyor: bugün 28'i temiz, 24'ü kısmen ifade ediliyor, 7'si için gereken özellik henüz
yok. Ayrıntısı ve eksiklerin listesi [taksonomi sınaması](reports/Haldus%20taksonomi%20sınaması.md)
belgesindedir.

Tasarım kararları için [CLAUDE.md](CLAUDE.md), kararların dayandığı araştırma için [reports](reports/)
klasörüne bakın. İki örnek uygulama var: tek projeli [mağaza](samples/Haldus.Sample.Shop) ve
[modüler](samples/Haldus.Sample.Modular) uygulama.

## Geliştirme

```bash
dotnet build
```

```bash
dotnet test
```

Entegrasyon testleri Docker gerektirir.
