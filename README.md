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
| `MapBalance<T>()` | Yalnızca komutlarla değişen nicelik: liste, tekil, `POST /{key}/adjust`, `/set`, `/move`, `GET /{key}/entries` |
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
| `.Under<T>(shallow: true)` | Kaynak ebeveynine aittir ama kendi adresinde yaşar (`/addresses/{id}`); ebeveyn oluşturma isteğinde gelir ve sonradan değişmez, liste `?customerId=` ile süzülür |
| `MapSingleton<T>().Under<TParent>()` | Ebeveyn başına tek kayıt: `GET` / `PUT` / `PATCH /customers/{customerId}/preferences` |
| `MapAssociation<TLeft, TRight>()` | Çoktan-çoğa bağ: `PUT` / `DELETE /products/{id}/tags/{tagId}`, iki yönlü liste; `PUT /products/{id}/tags` bağları verilen listeyle değiştirir |
| `MapAssociation<TLink, T>().ForCurrentUser()` | Geçerli kullanıcıyı bir kayda bağlar (favori, yıldız): `PUT` / `DELETE /me/favorites/{productId}` |

Bir kaynağın görünürlüğü başka bir kayıttan geliyorsa (ebeveyni kiracıya bağlı bir kaynak gibi), o
kaynağa dokunan her sorgu bunu kendiliğinden uygular.

Bağ kendi alanlarını ve durumunu taşıyabilir. Bağ sınıfı (`{Sol}{Sağ}`) yazılırsa alanları bağın yüküdür;
`.Workflow<TStatus>()` bağa yalnızca geçişlerle değişen bir durum verir:

```csharp
public partial class OrganizationUser          // anahtarları (OrganizationId, UserId) Haldus üretir
{
    public string Role { get; set; } = "";
}

var memberships = app.MapAssociation<Organization, User>()
    .Workflow<MembershipStatus>(initial: MembershipStatus.Invited);

memberships.MapTransition("activate", from: MembershipStatus.Invited, to: MembershipStatus.Active);
```

`PUT /organizations/{id}/users/{userId}` gövdesinde yükü alır (`{ "role": "admin" }`) ve bağı döndürür;
`POST …/users/{userId}/activate` durumu değiştirir; bağlar yükleri ve durumlarıyla
`GET /organizations/{id}/users/links` altında (öteki yönden `GET /users/{id}/organizations/links`) listelenir.

### Tip, rol ve genelleme

```csharp
// Tip–örnek: cihaz, modelinden varsayılan devralır.
app.MapCrud<DeviceModel>();
app.MapCrud<Device>().TypedBy<DeviceModel>();

// Rol: çalışan, bir kişinin oynadığı roldür.
app.MapCrud<Person>();
app.MapCrud<StaffMember>().RoleOf<Person>();

// Genelleme: otomobil bir araçtır (public partial class Car : Vehicle).
app.MapCrud<Vehicle>().Auditable().SoftDeletable();
app.MapCrud<Car>().SubtypeOf<Vehicle>();
```

| Bildirim | Etkisi |
|---|---|
| `.TypedBy<TType>()` | Tipe zorunlu, yalnızca oluştururken yazılan bir referans (`DeviceModelId`). Kayıtta boş bırakılan ve tipte aynı adla, aynı türde bulunan alanlar tipten doldurulur (her yazmada, hesaplanan alanlardan önce); alanı sonradan boşaltmak onu yeniden tipe bağlar. Kayıtları olan tip silinemez |
| `.RoleOf<TPlayer>()` | Oyuncuya zorunlu, değişmeyen bir referans. Bir oyuncunun bu rolden en çok bir kaydı olur (ikincisi 409) ve rol oyuncunun kimliğiyle de okunur: `GET /staff-members/by-person-id/{personId}`. Oyuncu silinince rolü de silinir |
| `.SubtypeOf<TBase>()` | Alt tip üst tipin tablosunda, onun anahtarı ve trait'leriyle durur. `/cars` yalnızca otomobilleri, üst tipin alanlarıyla birlikte kendi alanlarıyla sunar; `/vehicles` bütün türleri ortak alanlarıyla döndürür, her kaydın türünü `kind` alanında söyler ve `?kind=car` ile daraltılır. Tür istekle yazılamaz |

Değer tipten **yazılırken** devralınır: tipin varsayılanı sonradan değişirse var olan kayıtlar kendi değerini korur.
Bir üst tip yalnızca bütün alt tipleri için aynı anlama gelen bildirimleri taşır: `MultiTenant`, `Auditable`,
`SoftDeletable`, `Archivable`, `ConcurrencyChecked` ve okuma seçenekleri. Alt tipin zincirinde yalnızca işlemler ve
okuma seçenekleri (`Without`, `Only`, `Sortable`, `Searchable`, `Filterable`) yazılır.

## Detaylar ve hesaplanan alanlar

Bir kaydın satırları (sipariş ve sipariş satırları gibi) sahibinin builder'ından bildirilir:

```csharp
var orders = app.MapCrud<Order>().ConcurrencyChecked();

orders.MapDetail<OrderItem>();                  // siparişin istek ve yanıtına gömülü
orders.MapDetail<OrderNote>().AsSubresource();  // /orders/{orderId}/order-notes
```

Gömülü satırlar sahibiyle tek istekte yazılır. `PUT` satırları eşitler: kimliği verilen satır
güncellenir, verilmeyen eklenir, istekte bulunmayan silinir. Satırlar gönderildikleri sırayla döner.

`.AsCommands()` satırları yanıtta gömülü bırakır ama topluca eşitlemek yerine tek tek komutlarla yazdırır:
`POST /orders/{id}/order-items`, `PUT` ve `DELETE /orders/{id}/order-items/{rowId}`. Her komut sahibin bir
yazmasıdır (sürüm ister, durum kuralına uyar, `Compute()` çalışır) ve sahibi döndürür.

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

### Birden çok durum ekseni

Bir kaydın birbirinden bağımsız ilerleyen birkaç durumu olabilir: siparişin kendi durumu, ödemesi ve sevkiyatı gibi.
Asıl durum `Workflow` ile, ötekiler `.Axis<TStatus>()` ile bildirilir; her eksen kendi enum tipini taşır ve alanı o
tipin adını alır.

```csharp
var orders = app.MapCrud<Order>()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)
    .Axis<PaymentState>()                     // yanıtta "paymentState": "Unpaid"
    .Axis<ShipmentState>();

orders.MapTransition("place", from: OrderStatus.Draft, to: OrderStatus.Placed);
orders.MapTransition("mark-paid", from: PaymentState.Unpaid, to: PaymentState.Paid);
orders.MapTransition("ship", from: ShipmentState.Pending, to: ShipmentState.Shipped);
```

Geçişin hangi eksende olduğunu durumlarının tipi söyler; geçiş yalnızca o ekseni denetler ve yazar. `EditableIn`,
`DeletableIn` ve `Numbered(assignOn:)` yalnızca asıl duruma bakar. Liste her eksene göre ayrı süzülür
(`?paymentState=Paid`). Başka kayıtlardan **türeyen** bir durum için eksen değil, [toplanan alan](#saklama-okundu-takibi-ve-toplanan-alanlar)
ve `Compute()` kullanılır.

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

Alana özgü mantığın yeri de burasıdır. Aksiyon (ve geçiş) kaydı gömülü satırlarıyla alır; eklediği satırlara kimliği
ve sırayı Haldus verir, satırlar kayıtla birlikte kaydedilir:

```csharp
// Seçeneklerin bütün birleşimlerinden varyant üretir: POST /garments/{id}/generate-variants
public sealed class GenerateVariants : IEntityAction<Garment>
{
    public string[] Sizes { get; set; } = [];
    public string[] Colors { get; set; } = [];

    public void Apply(Garment garment)
    {
        foreach (var size in Sizes)
        foreach (var color in Colors)
        {
            garment.GarmentVariants.Add(new GarmentVariant { Size = size, Color = color, Sku = $"{garment.Name}-{size}-{color}" });
        }
    }
}
```

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
| `.KeyStrategy(...)` | Anahtarın biçimi: sıralı GUID (varsayılan), rastgele GUID, önekli kimlik (`ord_01j9…`), ULID (`KeyStrategy.Ulid`; 26 karakterlik, zamana göre sıralı metin) veya istemcinin verdiği kimlik (`KeyStrategy.Client`: `POST /enrollments?id={guid}`; aynı kimlikle ikinci oluşturma 409 alır) |
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

## Kayıtlar arası zincir

```csharp
app.MapAppendOnly<StockMovement>().Reversible();                        // POST /stock-movements/{id}/reverse
app.MapCrud<Quote>().Supersedes();                                      // POST /quotes/{id}/supersede
app.MapCrud<Order>().CreatedFrom<Cart>();                               // POST /orders/from-cart/{cartId}
app.MapAppendOnly<Receipt>().CreatedFrom<Payment>(on: "capture");       // tahsilat geçişiyle doğar
app.MapReadOnly<StockLevel>().DerivedFrom<StockMovement>();             // her hareketle güncellenir
```

| Bildirim | Etkisi |
|---|---|
| `.Reversible()` | Kaydı nötrleyen ters kaydı üretir; ikisi birbirine işaret eder (`ReversesId`, `ReversedById`). Bir kayıt bir kez ters çevrilir |
| `.Supersedes()` | Eskisinin yerine geçen yeni kaydı üretir (`SupersedesId`, `SupersededById`). Eski kayıt kalır ama değiştirilemez |
| `.CreatedFrom<T>()` | Kaydı başka bir kayıttan üretir ve bağı tutar (`CartId`). `on:` verilirse kayıt, kaynağın o geçişiyle doğar |
| `.DerivedFrom<T>()` | Salt-okunur kaynak başka kayıtlardan hesaplanır: her kaynak kayıtla birlikte izdüşüm sınıfın çağrılır |

Karşı kayıt (ters kayıt, yerine geçen) asıl kaydın kopyası olarak doğar; entity `IReversal<T>` veya
`ISupersession<T>` uygularsa yalnızca farkı yazar (ör. tutarın işaretini çevirir). Başka bir kayıttan üretilen
kayıtta aynı adlı alanlar kopyalanır, gerisini `ICreatedFrom<TSource>` doldurur. Türetmeyi
`IDerivation<TSource, TTarget>` uygulayan bir sınıf yazar; Haldus onu kaynak kayıtla aynı transaction'da ve
sırayla çağırır.

## Onay ve belge

```csharp
app.MapCrud<ExpenseClaim>().Approvable();

var requests = app.MapCrud<PurchaseRequest>()
    .Workflow<PurchaseRequestStatus>()
    .Approvable(levels: 2, "order");          // iki farklı kişi; "order" onaysız çalışmaz

app.MapCrud<Contract>().Document();           // taslak → gönder → iptal → düzelt
```

| Bildirim | Etkisi |
|---|---|
| `.Approvable(levels, ...)` | `POST /{id}/approve` ve `/reject` (isteğe bağlı `{ "note": "..." }`), kararların listesi `GET /{id}/approvals`. Kaydın içeriğini yazmış hiç kimse karar veremez (403); içerik hangi yoldan değişirse değişsin toplanan onaylar geçersiz olur; onaylanmış kayıt güncelleme uçlarından değiştirilemez, reddedilmiş kayıt düzenlenince yeniden karar bekler. Sayılan geçişler ve aksiyonlar kayıt onaylanmadan çalışmaz |
| `.ApprovalRoles("manager", "finance")` | Onay zinciri: n. rol, n. düzeyin kararını (onay veya ret) verebilecek kullanıcıları belirler; rolde olmayan 403 alır. Roller `ICurrentUserRoles` servisine sorulur (varsayılanı token'daki rol claim'leri). Kaç düzey gerektiği kayda göre değişecekse entity `IApprovalLevels` uygular (küçük tutar tek onayla geçer) |
| `.Document()` | Sabit durumlar (`DocumentStatus`): taslak düzenlenir ve silinir, gönderilen belge kilitlidir. `POST /{id}/submit` ve `/cancel` kendiliğinden gelir (aynı adla yazılan `MapTransition` onların yerine geçer); `POST /{id}/amend` iptal edilmiş belgenin kopyası olan, ona işaret eden yeni bir taslak oluşturur |

Onay alanları (`ApprovalStatus`, `ApprovalLevel`, `RequestedBy`, `Contributors`, `DecidedBy`, `DecidedAt`, `DecisionNote`) yanıtta
görünür ve istekle yazılamaz. Karar, kararı verenin gördüğü içeriğe bağlıdır: araya giren bir yazma kararı 409 ile
durdurur (sürüm kontrollü kaynakta karar `If-Match` ister).

## Zaman ve sürüm

```csharp
var articles = app.MapCrud<Article>()
    .Revisioned()      // her yazmanın anlık görüntüsü, geri yükleme
    .Publishable()     // taslak düzenlenirken yayındaki hal durur
    .ChangeFeed();     // "şundan beri ne değişti"

app.MapCrud<Rate>().Under<Supplier>().EffectiveDated(rate => rate.Currency);
```

| Bildirim | Etkisi |
|---|---|
| `.Revisioned()` | Kaydın her yazmadan sonraki hali saklanır: `GET /{id}/revisions`, `GET /{id}/revisions/{revisionId}` (kaydın o andaki hali, kendi yanıt biçiminde), `POST /{id}/revisions/{revisionId}/rollback` (alanlar ve gömülü satırlar geri yüklenir; bu da yeni bir sürümdür) |
| `.Publishable()` | Sıradan uçlar taslağı yazar; `POST /{id}/publish` o andaki hali yayına alır, `POST /{id}/unpublish` kaldırır. Yayındaki hal `GET /published` ve `GET /published/{id}` ile okunur. Yayındaki kayıt silinemez |
| `.ScheduledPublishing()` | `Publishable` ile birlikte: `POST /{id}/schedule` (`{ "publishAt": "…" }`) taslağa bir yayın zamanı yazar, `DELETE /{id}/schedule` kaldırır. Zamanı gelen taslağı bakım servisi o andaki haliyle yayına alır; yayın bu yüzden zamanından en çok bir bakım aralığı (`HaldusOptions.MaintenanceInterval`) kadar sonra gerçekleşir. Elle yayın bekleyen zamanlamayı düşürür |
| `.EffectiveDated(...)` | `EffectiveFrom` / `EffectiveTo` alanları; bir kapsamda (kiracı, sahip, ebeveyn, sayılan alanlar) dönemler çakışamaz (409). Açık uçlu dönemin içinde başlayan yeni kayıt öncekini kapatır. `?asOf=2026-06-01` o gün geçerli olanı döndürür |
| `.ChangeFeed()` | `GET /changes?since={imleç}` imleçten sonra değişen kayıtların şimdiki halini ve silinenlerin kimliklerini döndürür; yanıttaki `next` bir sonraki isteğin imlecidir |

Anlık görüntüyü ve değişiklik günlüğünü, kaydı hangi kod yazarsa yazsın (toplu yollar, aksiyonlar, arka plan işleri,
uygulamanın kendi kodu) Haldus kaydederken alır. Değişiklik akışı varsayılan olarak en yeni değişikliklerin iki
saniye gerisinde kalır (`HaldusChanges.SafetyLag`); böylece o sırada bitmemiş bir transaction'ın satırları atlanmaz.

## Dosyalar ve takılabilirler

```csharp
app.MapFile<Attachment>(maxBytes: 1024 * 1024, "image/*", "application/pdf");
app.MapAppendOnly<Comment>().Hierarchical();
app.MapLookup<Label>();

app.MapCrud<Order>().Attachable<Attachment>().Commentable<Comment>().Taggable<Label>();
app.MapCrud<Invoice>().Attachable<Attachment>().Commentable<Comment>();
```

| Bildirim | Etkisi |
|---|---|
| `MapFile<T>(maxBytes, türler)` | İçeriği ayrıca yüklenen bir `MapCrud` kaynağı: `POST` kaydı (adı ve kendi alanlarıyla) oluşturur, `PUT /{id}/content` içeriği yükler (boyut ve tür sınırı: 413, 415), `GET /{id}/content` indirir (parça isteği ve `ETag` ile). Boyut, tür, SHA-256 özeti ve durum yanıtta görünür |
| `.SignedLinks()` | `MapFile` ile birlikte: `POST /{id}/content/link?expiresInSeconds=300` kaydı görebilen birine süreli bir bağlantı (`{ "url", "expiresAt" }`) verir; `GET /shared-content/{token}` içeriği kimlik sormadan gönderir. Bağlantı tahmin edilemez, en çok bir gün yaşar ve üretildiği andaki içeriğe bağlıdır: içerik değişince ya da kayıt silinince geçersiz olur |
| `.AttachedTo<T>()` | Kaynak birkaç farklı kaynağa takılabilir: her hedefin adresi altında sunulur (`/orders/{orderId}/comments`, `/invoices/{invoiceId}/comments`), her hedef için isteğe bağlı gerçek bir yabancı anahtar üretilir ve bir kayıtta tam olarak biri doludur. Takılı kayıt, takıldığı kayıt görünürse görünür ve onunla birlikte silinir |
| `.Attachable<TFile>()`, `.Commentable<TComment>()` | Aynı bildirimin hedef tarafında yazılan biçimi: `TFile` / `TComment` bu kaynağa da takılabilir |
| `.Taggable<TTag>()` | `MapAssociation<TEntity, TTag>()` ile aynıdır; etiketler `PUT /{id}/tags` ile topluca değişir |

Yetkilendirme gibi kurallar her kaynağın kendi `Map` çağrısına eklenir: `/orders/{orderId}/attachments` uçlarını
`app.MapFile<Attachment>()` eşler ve kurallarını da o çağrı taşır. Etiket bağının uçları ise (`Taggable`) etiketlenen
kaynağın kurallarını alır.

Dosya içerikleri varsayılan olarak veritabanında saklanır (ek altyapı gerekmez, içerik ve kayıt birlikte kalıcı
olur). `builder.Services.AddHaldusFileSystemStorage(path)` içerikleri bir klasöre taşır; başka bir depo
`IHaldusFileStorage` uygulanarak eklenir. S3 uyumlu bir nesne deposu (AWS S3, MinIO) için ayrı paket vardır
(`Haldus.Storage.S3`; `AWSSDK.S3` üzerine):

```csharp
builder.Services.AddHaldusS3Storage(options =>
{
    options.Region = "eu-central-1";
    options.Bucket = "uploads";

    // AWS dışındaki S3 uyumlu servisler (MinIO...) için adres ve anahtarlar verilir. İkisi de boş bırakılırsa
    // AWS'nin bölgedeki adresi ve SDK'nın varsayılan kimlik bilgisi zinciri (ortam, profil, rol) kullanılır.
    options.ServiceUrl = new Uri("http://localhost:9000");
    options.AccessKey = builder.Configuration["S3:AccessKey"]!;
    options.SecretKey = builder.Configuration["S3:SecretKey"]!;
});
```

İçerik belleğe bütün olarak alınmaz: uzunluğu bilinen içerik tek istekle akıtılır, bilinmeyen içerik parça parça
(çok parçalı yükleme) gönderilir. Kovayı uygulama oluşturur; yarıda kalan çok parçalı yüklemeler için kovaya bir
yaşam döngüsü kuralı konması önerilir. MinIO'ya karşı sınandı, gerçek AWS S3'e karşı sınanmadı. İndirilen içerik her
zaman ek olarak (`attachment`) ve `nosniff` ile
gönderilir: tür yükleyenin beyanıdır ve içerik tarayıcıda sayfanın içinde açılmamalıdır.

## Özel alanlar ve aktarma

```csharp
app.MapCrud<Machine>().MultiTenant().CustomFields();

app.MapCrud<Product>().Batch();
app.MapJob<ProductExport>().Exports<Product>();
app.MapJob<ProductImport>().Imports<Product>();
```

| Bildirim | Etkisi |
|---|---|
| `.CustomFields()` | Kayda, alanlarını kiracının tanımladığı bir `customFields` çantası ekler. Tanımlar kaynağın adresindedir: `GET /custom-fields`, `PUT` / `DELETE /custom-fields/{key}` (tür: metin, sayı, evet/hayır, gün, seçenek; zorunlu olabilir). Değerler her yazmada tanımlara göre doğrulanır; `PATCH` çantayı birleştirir |
| `MapJob<T>().Exports<TResource>()` | İş, kaynağın işi başlatana görünen bütün kayıtlarını CSV veya JSON dosyasına döker; dosya iş bitince `GET /{id}/content` ile indirilir. Mantık üretilir, handler yazılmaz |
| `MapJob<T>().Imports<TResource>()` | İş, isteğin `content` alanındaki satırları (başlıklı CSV veya JSON dizisi) tekil oluşturmanın bütün kurallarıyla oluşturur. Satırlar tek bir transaction içindedir: biri bile reddedilirse hiçbiri oluşturulmaz ve nedenleri `errors` alanında döner |

CSV yeni bir bağımlılık olmadan okunur ve yazılır (RFC 4180). Dışa aktarılan metinlerden `=`, `+`, `-` veya `@` ile
başlayanların önüne kesme işareti konur; böylece dosya bir tablolama programında açıldığında formül olarak çalışmaz.

## Tekrar serisi

```csharp
app.MapCrud<Meeting>().Recurring();
```

Yinelenen kayıt bir kez saklanır; tek tek oluşları saklanmaz, kuraldan hesaplanır. `.Recurring()` kayda ilk oluşu ve
kuralı taşıyan yazılabilir alanlar ekler:

| Alan | Anlamı |
|---|---|
| `startsAt` | İlk oluşun zamanı |
| `recurrenceFrequency` | `None`, `Daily`, `Weekly`, `Monthly`, `Yearly` |
| `recurrenceInterval` | Kaç aralıkta bir (iki haftada bir için 2); boşsa 1 |
| `recurrenceUntil`, `recurrenceCount` | Serinin bittiği zaman veya en çok kaç oluşu olduğu; ikisi de boşsa seri açık uçludur |
| `recurrenceByDay` | Oluşların düştüğü günler, iCalendar'ın `BYDAY` yazımıyla: `MO,WE,FR`. Aylık ve yıllık seride gün bir sıra alabilir: `2TU` ayın ikinci salısı, `-1FR` ayın son cuması. Boşsa oluşlar ilk oluşun gününe düşer |
| `recurrenceTimeZone` | Serinin saat dilimi (IANA adı, `Europe/Berlin`). Verilirse oluşlar o dilimin duvar saatini korur: yaz saatine geçilince toplantı yine 09.00'dadır. Boşsa oluşlar ilk oluşun UTC farkıyla hesaplanır |

| Uç | Etkisi |
|---|---|
| `GET /meetings/{id}/occurrences?from=&to=` | Aralıktaki oluşlar (en çok 500). Aralık verilmezse ilk oluştan başlayarak bir yıl |
| `PUT /meetings/{id}/occurrences/{start}` | Tek bir oluşu erteler (`{ "start": "…" }`) veya iptal eder (`{ "cancelled": true }`). `{start}` kuralın verdiği zamandır; kuralın vermediği bir zaman 404 alır |
| `DELETE /meetings/{id}/occurrences/{start}` | Oluşu kurala geri döndürür |

Her oluş ilk oluştan sayılarak hesaplanır: ayın 31'inde başlayan aylık seri şubatta ayın son gününe düşer, martta yine
31'ine döner. Kural değişirse oluşlar da değişir; eski kurala ait istisnalar artık hiçbir oluşa uymaz ve yok sayılır.
Günler verildiğinde bir hafta veya ay birden çok oluş verebilir; `recurrenceCount` dönemleri değil oluşları sayar ve
seri ilk oluştan önce başlamaz. Ayda o sıra yoksa (beşinci cuma) o ay oluş yoktur. Bu, iCalendar `RRULE`'unun
tamamı değildir: `BYMONTHDAY`, `BYSETPOS`, `BYMONTH`, `WKST` (hafta pazartesi başlar) ve `EXDATE` / `RDATE` yoktur.

## Olay yayını

```csharp
var tickets = app.MapCrud<Ticket>()
    .Workflow<TicketStatus>()
    .PublishesEvents();

tickets.MapTransition("close", from: TicketStatus.Open, to: TicketStatus.Closed);

// Olayları uygulamanın dışına taşıyan sınıfı uygulama yazar ve kaydeder.
builder.Services.AddSingleton<IHaldusEventPublisher, QueuePublisher>();

public sealed class QueuePublisher(IQueue queue) : IHaldusEventPublisher
{
    public Task PublishAsync(HaldusEvent published, CancellationToken cancellationToken) =>
        queue.SendAsync(published.Type, published.Data, cancellationToken);
}
```

Olayları bir Amazon SQS kuyruğuna (veya SQS uyumlu bir servise) taşımak için ayrı bir paket vardır
(`Haldus.Events.Sqs`; `AWSSDK.SQS` üzerine):

```csharp
builder.Services.AddHaldusSqsPublisher(options =>
{
    options.Region = "eu-central-1";
    options.QueueUrl = new Uri("https://sqs.eu-central-1.amazonaws.com/123456789012/shop-events.fifo");
    options.AccessKey = builder.Configuration["Sqs:AccessKey"]!;
    options.SecretKey = builder.Configuration["Sqs:SecretKey"]!;
    options.EventTypes.Add("ticket.*");   // boş bırakılırsa bütün olaylar
});
```

Her olay bir mesajdır; gövdesi olayın kendisi, öznitelikleri olayın kimliği, türü ve kaynağıdır. FIFO kuyrukta
(`.fifo`) aynı kaydın olayları sıralı kalır (grup kimliği kaynak + kayıt kimliğidir) ve yeniden teslim edilen olay
kuyrukta yinelenmez (SQS'in beş dakikalık yineleme penceresi içinde). Standart kuyruk sırayı korumaz ve yineleneni
ayıklamaz; tüketici olayın kimliğine bakmalıdır. Erişim anahtarı verilmezse kimlik bilgisi SDK'nın varsayılan
zincirinden okunur. Bir SQS öykünücüsüne (ElasticMQ) karşı sınandı; gerçek AWS'ye karşı sınanmadı.

RabbitMQ ve Kafka için de ayrı paketler vardır (`Haldus.Events.RabbitMq`, `RabbitMQ.Client` üzerine;
`Haldus.Events.Kafka`, `Confluent.Kafka` üzerine):

```csharp
builder.Services.AddHaldusRabbitMqPublisher(options =>
{
    options.HostName = "rabbit.internal";
    options.UserName = builder.Configuration["Rabbit:User"]!;
    options.Password = builder.Configuration["Rabbit:Password"]!;
    options.Exchange = "shop.events";   // yönlendirme anahtarı olayın türüdür: ticket.created
});

builder.Services.AddHaldusKafkaPublisher(options =>
{
    options.BootstrapServers = "kafka-1:9092,kafka-2:9092";
    options.Topic = "shop.events";
    options.ConfigureProducer = config => config.SecurityProtocol = SecurityProtocol.Ssl;   // SASL, TLS...
});
```

RabbitMQ'da mesaj kalıcıdır ve aracı onaylayana kadar teslim edilmiş sayılmaz; hiçbir kuyruğa yönlendirilemeyen
olay da teslim edilmemiş sayılır ve kutuda kalır (bağı unutulmuş bir kuyruk olayları sessizce kaybettirmez).
Kafka'da mesajın anahtarı kayıttır (`ticket:{id}`): bir kaydın olayları aynı bölüme düşer ve sırayla tüketilir;
mesaj eşlenik kopyaların hepsi yazana kadar (`acks=all`) teslim edilmiş sayılmaz. Exchange'i, kuyrukları ve konuyu
uygulama oluşturur. İkisi de kapsayıcıda çalışan gerçek bir aracıya karşı sınandı; TLS ve SASL yolları sınanmadı.

Olayları başka bir sisteme HTTP ile taşımak için de hazır bir yayıncı vardır:

```csharp
builder.Services.AddHaldusWebhookPublisher(options =>
    options.Endpoints.Add(new HaldusWebhookEndpoint(
        new Uri("https://example.com/hooks/haldus"),
        builder.Configuration["Webhooks:Secret"]!,
        "ticket.*")));   // boş bırakılırsa bütün olaylar

// Alıcı tarafında: imza, paylaşılan anahtarla doğrulanır.
var valid = HaldusWebhookSignature.Verify(
    secret,
    request.Headers["Webhook-Id"]!, request.Headers["Webhook-Timestamp"]!, body,
    request.Headers["Webhook-Signature"]!, DateTimeOffset.UtcNow, tolerance: TimeSpan.FromMinutes(5));
```

İstek imzalıdır (HMAC-SHA256; Standard Webhooks düzeni), yönlendirme izlenmez ve adres `https` olmalıdır. Adres 2xx
dışında yanıt verirse olay yeniden denenir; alıcı yinelenen olayı `Webhook-Id` başlığından tanır. Adresler
uygulamanın yapılandırmasından gelmelidir: kullanıcıların tanımladığı adreslere istek göndermek ayrıca korunmalıdır
(SSRF).

Kayıt değiştikçe olay yazılır: `ticket.created`, `ticket.updated`, `ticket.deleted` ve her geçiş için ayrıca
`ticket.close` gibi, geçişin adıyla. Olay, kaydın o andaki halini kaynağın yanıt biçiminde taşır; yanıtta olmayan
(ör. hassas) bir alan olayda da yoktur.

- **Olay kayıtla aynı kaydetmede yazılır** (bekleme tablosu, `HaldusOutbox`). Kayıt kalıcı olmadıysa olay da yoktur;
  olduysa olay kaybolmaz. Kaydı hangi kod yazarsa yazsın (uç, iş, uygulamanın kendi kodu) olay yazılır.
- **Teslimat ayrıdır ve en az bir kezdir.** Arka plandaki dağıtıcı olayları yazıldıkları sırayla yayıncılara verir ve
  teslim edileni siler. Yayıncı hata fırlatırsa olay artan aralıklarla yeniden denenir ve arkasındakiler onu bekler
  (sıra bozulmaz); on denemeden sonra olaydan vazgeçilir, nedeniyle birlikte tabloda kalır ve sıradakilere geçilir.
  Aynı olay birden çok kez gelebilir; yayıncı `HaldusEvent.Id` ile yineleneni ayırt etmelidir.
- Yayıncı kaydedilmediyse olaylar yazılır ama teslim edilmez; tabloda birikir (uygulama açılırken uyarı yazılır).

## Hassas alanlar, ölçüte göre silme, işin yeniden çalıştırılması

```csharp
app.MapCrud<Contact>()
    .Sensitive(contact => contact.NationalId)
    .Filterable<ContactFilter>()
    .Batch()
    .Purgeable();

app.MapJob<ReportExport>().Rerunnable();
```

| Bildirim | Etkisi |
|---|---|
| `.Sensitive(alanlar...)` | Sayılan alanlar yalnızca yazılır: oluşturma ve güncelleme isteklerinde yer alırlar; hiçbir yanıtta, dışa aktarmada ve saklanan sürümde (`Revisioned`) yer almazlar. Böyle bir alana göre aranamaz, sıralanamaz; alan doğal anahtar veya çevrilebilir olamaz ve kaynak `ChangeSets` ile birleşmez. Gömülü satırlarda da yazılır: `orders.MapDetail<OrderItem>().Sensitive(...)` |
| `.Encrypted(alanlar...)` | Sayılan metin alanları veritabanında şifreli saklanır (ASP.NET Data Protection) ve, `Sensitive` alanlar gibi, yalnızca yazılır. Sunucunun kendi kodu alanı açık haliyle okur. Anahtarların kalıcı ve sunucular arasında ortak olması uygulamanın işidir; yoksa veri okunamaz. Kendi anahtar yönetimini kullanmak isteyen uygulama `IHaldusFieldProtector` servisini değiştirir |
| `.Purgeable()` | `POST /contacts/purge?source=import-2024` ölçüte uyan kayıtları sayar ve ilk yüzünün anahtarını gösterir; `&force=true` ile siler. Ölçüt, liste ucunun filtresidir ve en az biri verilmelidir (boş metin ölçüt sayılmaz). Ölçüt görünen bütün kayıtlara uyuyorsa silme ancak `&all=true` ile yapılır. Her kayıt tekil silmenin kurallarından geçer; biri bile silinemiyorsa hiçbiri silinmez. Bir istek en çok 1000 kayıt siler; `matched` > `purged` ise istek yinelenir |
| `MapJob<T>().Rerunnable()` | `POST /{id}/rerun` bitmiş (başarılı, başarısız veya iptal edilmiş) bir işi aynı girdilerle yeniden çalıştırır: yeni bir iş kaydı oluşur (202) ve `rerunOfId` ile asıl işe işaret eder. Bekleyen veya çalışan iş için 409 döner |

## Değişiklik kümeleri

Verilmiş bir sipariş doğrudan düzenlenemez (`EditableIn` yalnızca taslak), ama düzeltilmesi gerekebilir. Değişiklik
kümesi bunu bir düzenleme oturumu olarak sunar: değişiklikler adım adım biriktirilir, sonucu önizlenir ve hepsi tek
seferde uygulanır ya da vazgeçilir.

```csharp
var orders = app.MapCrud<Order>()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)
    .ChangeSets(OrderStatus.Placed);          // verilmiş sipariş küme ile düzenlenir
```

| Uç | Etkisi |
|---|---|
| `POST /orders/{id}/edits` | Boş bir küme açar (201) |
| `PATCH /orders/{id}/edits/{editId}` | Değişiklik ekler. Gövde, kaydın `PATCH` gövdesiyle aynı biçimdedir ve aynı kurallardan geçer; aynı alan yeniden gönderilirse sonuncusu geçerlidir |
| `GET /orders/{id}/edits/{editId}/preview` | Küme şimdi uygulansaydı kaydın nasıl olacağını, hesaplanan alanlarıyla döndürür; hiçbir şey kaydetmez |
| `POST /orders/{id}/edits/{editId}/commit` | Değişiklikleri kayda uygular ve kümeyi kapatır; ikisi tek kaydetmededir |
| `DELETE /orders/{id}/edits/{editId}` | Kümeden vazgeçer |
| `GET /orders/{id}/edits`, `GET /orders/{id}/edits/{editId}` | Kaydın kümeleri ve biriken değişiklikler |

Uygulama, kaydın kısmi güncellemesinin bütün kurallarından geçer (doğrulama, referanslar, benzersizlik, hesaplanan
alanlar, `If-Match`, onaylanmış veya kapanmış kayıt); yalnızca `EditableIn` kuralının yerini `ChangeSets(...)` ile
sayılan durumlar alır. Kaynağın durum makinesi varsa bu durumların sayılması zorunludur. Küme, kaydın uygulama
anındaki haline uygulanır: hazırlanırken kayıt başka yoldan değiştiyse değişiklikler onun üzerine yazılır. Aynı küme
iki kez uygulanamaz; birlikte gelen iki uygulama isteğinden biri 409 alır.

## Bakiyeler

Stok seviyesi veya izin bakiyesi gibi bir nicelik `PUT` ile yazılmaz: aynı anda gelen iki yazmadan biri ötekini ezerdi
ve neyin neden değiştiği kaybolurdu. `MapBalance` bakiyeyi yalnızca komutlarla değiştirir ve her değişimi deftere yazar.

```csharp
app.MapBalance<Inventory>()
    .MultiTenant()
    .Buckets("available", "reserved", "damaged")
    .Idempotent();

public partial class Inventory;   // Key, Available, Reserved, Damaged alanlarını Haldus üretir
```

| Uç | Etkisi |
|---|---|
| `POST /inventories/{key}/adjust` | `{ "delta": -3, "bucket": "available", "reason": "…" }`: bakiyeyi değişim kadar artırır veya azaltır. Hiç yazılmamış bir anahtarın ilk düzeltmesi kaydı oluşturur |
| `POST /inventories/{key}/set` | `{ "quantity": 40, "expected": 38 }`: mutlak değere çeker (sayım). `expected` verildiyse ve bakiye o değerde değilse 409 döner |
| `POST /inventories/{key}/move` | `{ "from": "available", "to": "reserved", "quantity": 2 }`: miktarı bir kovadan ötekine tek işlemde taşır. Yalnızca birden çok kova varsa üretilir |
| `GET /inventories/{key}/entries` | Defter: her değişimin kovası, miktarı, sonraki bakiyesi, açıklaması, zamanı ve yapanı; en yeni başta. `?bucket=` ile süzülür |
| `GET /inventories`, `GET /inventories/{key}` | Bakiyeler; kova alanları süzülebilir ve sıralanabilir |
| `POST /inventories/{key}/holds` | `.Holds(from: "available", to: "reserved")` bildirildiyse: `{ "quantity": 4, "expiresInSeconds": 900 }` miktarı eldeki kovadan ayrılan kovaya taşır ve bir tutma açar (rezervasyon, ön provizyon) |
| `POST /inventories/{key}/holds/{holdId}/capture`, `…/release` | Tutmayı tahsil eder (miktar bakiyeden düşer) veya bırakır (eldeki kovaya döner). Kapanmış ya da süresi geçmiş tutma tahsil edilemez (409) |
| `GET /inventories/{key}/holds` | Açık tutmalar. Ömrü verilen tutmayı, süresi dolunca bakım servisi kendiliğinden bırakır |

Hiçbir kova sıfırın altına düşemez: yetersiz bakiye 409 döner ve hiçbir şey yazılmaz (`.AllowNegative()` bu kuralı
kaldırır). Kontrol ve yazma bakiye başına bir kilidin altındadır; aynı anda gelen yirmi düşüşten bakiyenin karşıladığı
kadarı geçer. `.Buckets(...)` verilmezse bakiyenin tek kovası vardır (`quantity`). `.Idempotent()` komutları
`Idempotency-Key` ile tekrara karşı korur; ağ hatasından sonra yinelenen bir düşüş iki kez uygulanmaz. Bekleyen
talepler ayrı bir kovayla ifade edilir: izin talebi açılınca gün `available` kovasından `pending` kovasına taşınır.

## Çevrilebilen alanlar

```csharp
app.MapCrud<Article>()
    .Publishable()
    .Translatable(article => article.Title, article => article.Body);
```

Sayılan metin alanları başka dillere çevrilebilir. Alanın kendisi kaynak dildeki metni taşımaya devam eder; çeviriler
kayıtla birlikte saklanır ve yanıtta `translations` altında görünür (`{ "tr": { "title": "…" } }`).

| Uç | Etkisi |
|---|---|
| `PUT /articles/{id}/translations/{locale}` | O dilin çevirilerini gövdedekilerle değiştirir. Alanlar, çevirdikleri alanın kurallarını (uzunluk vb.) taşır ama hiçbiri zorunlu değildir; boş bırakılan alan çevrilmemiş sayılır |
| `DELETE /articles/{id}/translations/{locale}` | O dilin çevirilerini kaldırır |
| `GET /articles/{id}?locale=tr`, `GET /articles?locale=tr` | Çevrilmiş alanları o dildeki metinle döndürür. Tam eşleşme yoksa daha genel dile düşülür (`pt-BR` → `pt`); çevirisi olmayan alan kaynak dildeki metinle döner |

Okuma, `?locale=` verilmedikçe kaynak dildedir; `Accept-Language` başlığı veriyi değiştirmez. Böylece okuduğunu geri
yazan bir istemci (ör. Türkçe tarayıcıda açılmış bir yönetim paneli) kaynak metnin üzerine çeviri yazmaz. Çeviri yazmak
kaydın bir yazmasıdır: güncelleme ucunun korumalarından (`If-Match`, düzenlenebilir durum, onaylanmış kayıt) geçer,
sürümü saklanır ve onayı düşürür. Kopya ve yayındaki hal çevirileri taşır; `GET /articles/published?locale=tr` de
çalışır. Bir kayıt en çok 50 dile çevrilebilir.

## Saklama, okundu takibi ve toplanan alanlar

```csharp
app.MapReadOnly<Notification>()
    .OwnedByCurrentUser()
    .ReadTracking()
    .Retention(TimeSpan.FromDays(90));

app.MapCrud<Discussion>()
    .Counts<Reply>()
    .Sums<Reply>(reply => reply.Score, into: "Score");

app.MapCrud<Reply>().Under<Discussion>();
```

| Bildirim | Etkisi |
|---|---|
| `.Retention(süre)` | Oluşturulalı verilen süreden uzun olmuş kayıtlar bakım sırasında silinir. `Auditable` kendiliğinden eklenir |
| `.ReadTracking()` | Kullanıcıya ait kayda `ReadAt` alanı ekler: `POST /{id}/read`, `POST /{id}/unread`, `POST /read-all` (okunmamışların hepsi), `GET /unread-count`; liste `?unread=true` ile süzülür. `OwnedByCurrentUser` ister |
| `.ReadReceipts()` | Paylaşılan kayıtların (duyuru, ortak belge) kullanıcı başına okundu bilgisi; kayıtta değil ayrı bir tabloda durur ve kaydın kendisini değiştirmez. `POST /{id}/read` ve `/unread`, `GET /{id}/read-state`, `POST /read-all` (istek başına en çok 5000 kayıt), `GET /unread-count`; liste `?unread=true` ile süzülür. Uçlar kullanıcısı çözülemeyen isteği reddeder (401) |
| `.Counts<TChild>(into:)` | Kayda, kendisine işaret eden `TChild` kayıtlarının sayısını tutan bir alan ekler (varsayılan adı `ReplyCount`) |
| `.Sums<TChild>(child => child.Alan, into:)` | Aynısı, bir sayısal alanın toplamı için (varsayılan adı `ReplyScoreSum`) |

Toplanan alanlar yalnızca yanıtta görünür, süzülebilir ve sıralanabilir. Alt kayıt hangi koddan yazılırsa yazılsın
(uç, toplu işlem, iş, uygulamanın kendi kodu) üst kaydın alanı aynı transaction içinde **baştan sayılır**; artırıp
azaltılmaz, bu yüzden kaçırılmış bir yazma kalıcı bir yanlış bırakmaz. Silinmiş ve süresi dolmuş kayıtlar sayılmaz.
Kiracıya ait kayıtlar genel bir kayıtta toplanamaz; kullanıcılara ait kayıtların ortak bir kayıtta toplanması (beğeni
sayısı gibi) `acrossOwners: true` ile açıkça istenmelidir, çünkü toplam o kaydı gören herkese açılır.
Üst kayıt `IComputed` uygularsa toplamdan sonra `Compute()` yeniden çağrılır; "ödenen tutardan ödeme durumu" gibi
türetilmiş durumlar böyle yazılır:

```csharp
public partial class Bill : IComputed
{
    public decimal Amount { get; set; }
    public BillState State { get; private set; }

    // PaidAmount üretilen alandır: ödemelerin toplamı.
    public void Compute() => State = PaidAmount >= Amount ? BillState.Paid : PaidAmount > 0 ? BillState.PartiallyPaid : BillState.Unpaid;
}

app.MapCrud<Bill>().Sums<BillPayment>(payment => payment.Amount, into: "PaidAmount");
app.MapAppendOnly<BillPayment>().References<Bill>();
```

## Sırlar

```csharp
app.MapCrud<ApiKey>().SecretOnce(prefix: "sk").Rotatable(overlap: TimeSpan.FromHours(1));
app.MapCrud<WebhookEndpoint>().SecretOnce(prefix: "whsec", recoverable: true).Rotatable();

var invitations = app.MapCrud<Invitation>().Workflow<InvitationStatus>().TokenAccess(prefix: "inv", "accept");
invitations.MapTransition("accept", from: InvitationStatus.Pending, to: InvitationStatus.Accepted);
```

| Bildirim | Etkisi |
|---|---|
| `.SecretOnce(prefix)` | Sır sunucuda üretilir ve yalnızca oluşturma yanıtında görünür; veritabanında SHA-256 özeti, listede son dört karakteri durur |
| `.SecretOnce(prefix, recoverable: true)` | Sır ayrıca ASP.NET Data Protection ile şifrelenir; sunucu onu açıp kullanabilir (`HaldusSecretProtection.Reveal`) |
| `.Rotatable(overlap)` | `POST /{id}/rotate` yeni sır üretir; eski sır hemen ya da örtüşme süresi sonunda geçersiz olur |
| `.TokenAccess(prefix, ...)` | Kayıt, oturumu olmayan biri tarafından `X-Access-Token` başlığındaki token ile okunur (`GET /by-token`) ve yalnızca sayılan geçişler çalıştırılır (`POST /by-token/accept`) |
| `.TokenOnly()` | `TokenAccess` ile birlikte: kayda **yalnızca** token ile ulaşılır. Kimlikle çalışan hiçbir uç üretilmez (okuma, liste, güncelleme, silme, geçişlerin `/{id}/…` adresi, `/{id}/expire`); geriye oluşturma ile `/by-token` uçları kalır. Sahibi bir kullanıcı olmayan kayıtlar (misafir sepeti) içindir |

Sunulan bir sırrın kime ait olduğu `FindBySecretAsync` ile bulunur:

```csharp
var key = await db.ApiKeys
    .IgnoreQueryFilters([HaldusFilters.Tenant])     // kimlik doğrulanırken kiracı henüz bilinmez
    .FindBySecretAsync(request.Headers["X-Api-Key"], now, cancellationToken);
```

Token uçları kimlik doğrulama istemez (`AllowAnonymous`): isteği yapanı token tanıtır. Geri okunabilen sırlar için
Data Protection anahtarlarının kalıcı ve sunucular arasında ortak olması uygulamanın sorumluluğundadır.

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

## Frontend ve mobil

Haldus bir backend framework'üdür; frontend ve mobil için kendi kod üretimi yoktur. O tarafa verdiği şey sözleşmedir:
her derlemede üretilen ve commit'lenen `openapi.json`. İstemci tipleri ve [TanStack Query](https://tanstack.com/query)
hook'ları o dosyadan [orval](https://orval.dev) ile üretilir; React (web) ve React Native (Expo) aynı çıktıyı kullanır.

```ts
// orval.config.ts
export default defineConfig({
  shop: {
    input: '../Haldus.Sample.Shop/openapi.json',   // backend'in ürettiği dosya; kopyalanmaz
    output: { mode: 'tags-split', target: 'src/api', schemas: 'src/api/model', client: 'react-query', httpClient: 'fetch', clean: true },
  },
});
```

Bunun çalışan hali [samples/Haldus.Sample.Shop.Client](samples/Haldus.Sample.Shop.Client) klasöründedir: örnek mağazanın
bütün uçları için üretilmiş istemci (bir uygulama değil), `pnpm generate:api` ile yeniden üretilir ve `pnpm typecheck`
ile üretilen kodun derlendiği doğrulanır. Üretilen uçların adları (`operationId`) kaynağın ve işlemin adından gelir
(`ListCars`, `CreateCar`, `CloseTicket`), bu yüzden hook adları öngörülebilirdir (`useListCars`, `useCreateCar`).

Sayıların istemcide `number` olması için uygulamanın JSON ayarında sayılar katı okunmalıdır
(`JsonNumberHandling.Strict`); ASP.NET Core'un varsayılanı şemaya `integer | string` yazar.

Sözleşmeyi tüketen iki örnek arayüz vardır; ikisi de kendi istemcisini aynı `openapi.json` dosyasından, yalnızca
kullandığı kaynaklar için üretir:

| Örnek | Ne ile yazıldı | Ne gösterir |
|---|---|---|
| [Yönetim paneli](samples/Haldus.Sample.Shop.Web) | Vite, React, shadcn/ui, Tailwind | Ürünler (liste, arama, sıralama, yazma), kategori ağacı, siparişlerin iş akışı (`If-Match`, `Idempotency-Key`) |
| [Mobil uygulama](samples/Haldus.Sample.Shop.Mobile) | Expo, Expo Router, React Native Reusables, NativeWind | Sayfa sayfa yüklenen ürün listesi, favoriler (`/me/favorites`), kullanıcının sepeti (`/me/baskets`) |

İkisi de doğrulamayı backend'e bırakır (alan hataları yanıttan okunur) ve arayüzün dilini `Accept-Language` ile
backend'e taşır; backend'in mesajları o dilde döner. Mobil uygulama Expo'nun web hedefinde ve Android'de (emülatör)
doğrulandı; iOS'ta çalıştırılmadı.

Kimlik doğrulama örnek mağazanın kendisindedir (`Features/Auth`): e-posta ve parola ile giriş, ASP.NET Core'un
yerleşik taşıyıcı token şeması, sunucuda tutulan oturumlar (çıkış ve parola değişimi token'ları hemen geçersiz kılar;
parola değişimi bütün oturumları kapatıp isteği yapana yenisini açar),
e-postayla gönderilen kodla parola sıfırlama ve e-posta doğrulama, hesap kilidi ve adres başına hız sınırı. Haldus
bunun yalnızca sonucunu görür: kullanıcıyı ve kiracıyı token'daki claim'lerden okur; claim'i hangi şemanın
ürettiğine bakmaz. İsteyen bir kimlik sağlayıcı (OpenID Connect) ekler; örnekte iki ayrı, birbirinden bağımsız yol
vardır ve ikisi de varsayılan olarak kapalıdır:

```jsonc
"Shop": { "Auth": {
  // API, sağlayıcının JWT erişim token'ını kabul eder; panel ve mobil uygulama sağlayıcıyla girişi kendileri yapar.
  "Jwt":  { "Authority": "https://idp.example", "Audience": "shop-api", "ClientId": "shop-app" },
  // Backend tarayıcıyı sağlayıcıya yönlendirir (/auth/oidc/sign-in) ve çerezle oturum açar.
  "Oidc": { "Authority": "https://idp.example", "ClientId": "shop-server", "ClientSecret": "…" }
} }
```

Sağlayıcının tanıttığı kişi ilk gelişinde yerel bir hesaba (ve kendi kiracısına) çevrilir; var olan bir hesaba adres
üzerinden bağlanılmaz. Sağlayıcı testlerde testin içinde kurulur; gerçek bir sağlayıcıya karşı sınanmadı. Giriş yapılmadan hiçbir kaynağa ulaşılmaz; açık kalması gereken uçlar (misafir sepeti, token ile
erişilen kayıtlar) bunu `AllowAnonymous` ile kendileri söyler. Kimseye ait olmayan, ortak kaynakları (ürünler,
kategoriler) herkes okur ama yalnızca personel yazar (`Shop:Auth:Staff`); kural, kaynakları tek tek saymak yerine
Haldus'un uçlara yazdığı kapsam metadatasını okur (aşağıda). Yenileme token'ı bir kez kullanılır; eskisi yeniden
sunulursa oturum kapatılır. İki arayüz de token'ı `Authorization` başlığında taşır ve süresi dolunca yeniler. Reddedilen bir
token sunan istek, vardığı kaynak ne olursa olsun 401 alır (`UseSessionValidation`); bu olmadan aynı istek
kullanıcıya ait kaynakta 401, kiracıya ait kaynakta 403, herkese açık kaynakta 200 alırdı ve istemci oturumunu
yenilemesi gerektiğini anlayamazdı. Entegrasyon testleri kimliği başlıklardan okuyan eski yolu kullanmaya devam eder
(`Shop:DemoIdentity`; varsayılan olarak kapalıdır).

### Uçların kapsamı

Üretilen her uç, kaynağının kayıtlarının kime ait olduğunu metadata olarak taşır. Uygulama yetki kurallarını
kaynakları tek tek saymadan buna göre yazabilir; sonradan eklenen bir kaynak kendiliğinden doğru sınıfa düşer:

```csharp
// Örnek mağazanın kuralı: kimseye ait olmayan kaynakları yalnızca personel yazar.
var endpoint = httpContext.GetEndpoint();
var reads = HttpMethods.IsGet(httpContext.Request.Method) || endpoint?.Metadata.GetMetadata<HaldusReadOnly>() is not null;
var scoped = endpoint?.Metadata.GetMetadata<HaldusResourceScope>() is { IsShared: false };

if (!reads && !scoped && !user.IsInRole("staff"))
{
    // 403: kapsamı bilinmeyen uç da ortak sayılır (kural kapalı başlar)
}
```

Gövde taşıdığı için `POST` olan ama yazmayan uçlar (arama, toplu okuma, önizleme) `HaldusReadOnly` ile işaretlidir;
okumayı adresin sonundan tanımak güvenilir değildir.

`TenantScoped`, kaynak `MultiTenant` ise veya öyle bir kaynağın altındaysa (`Under`) doğrudur; `OwnerScoped`, kaynak
`OwnedByCurrentUser` ise veya öyle bir kaynağın altındaysa. İkisi de yanlışsa kayıtlar ortaktır (`IsShared`). Çalışan
hali örnek mağazadadır (`Features/Auth/SharedWrites.cs`).

## Durum

Üç plan da tamamlandı: ilk sürümün on aşaması, araştırmanın ikinci sürüm listesini kapatan on bir aşama ve üçüncü
sürüm listesiyle sınamada kalan satırları kapatan on bir aşama. Model, araştırmadaki 59 gerçek varlığa karşı sınandı:
59'unun tamamı eksiksiz ifade ediliyor (ilk sınamada 19 eksiksiz, 29 kısmi, 11 karşılıksızdı; ikincisinde 47, 10, 2).
Ayrıntısı ve bilerek yazılmayanlar [taksonomi sınaması](reports/Haldus%20taksonomi%20sınaması.md) belgesindedir.

Planlardan sonra, o belgenin "Bilinen sınırlar" tablosundan yeni bağımlılık gerektirmeyen kalemler de yazıldı (tekrar
serisinde haftanın günleri ve saat dilimi, şifreli alan, onay zinciri, kullanıcı başına okundu bilgisi, zamanlanmış
yayın, ULID ve istemcinin verdiği kimlik, bakiyeye karşı tutma, multipart yükleme ve süreli indirme bağlantısı), örnek
mağaza gerçek kimlik doğrulamaya geçirildi ve kilide dayanan özellikler yük altında ölçüldü. Yapılmayanlar aynı
belgededir; başlıcaları: mobil uygulama iOS'ta çalıştırılmadı (Android'de emülatörde denendi), mobil uygulamanın
otomatik ekran testleri yalnızca web hedefinde çalışır, kimlik sağlayıcı (OpenID Connect) girişi henüz yazılmadı
(paketleri kararlaştırıldı). S3 uyumlu dosya deposu ile SQS, RabbitMQ ve Kafka olay yayıncıları resmi istemci
paketleriyle, ayrı paketler olarak yazıldı; webhook yayıncısı çekirdektedir.

Tasarım kararları için [CLAUDE.md](CLAUDE.md), kararların dayandığı araştırma için [reports](reports/)
klasörüne bakın. İki örnek backend var: tek projeli [mağaza](samples/Haldus.Sample.Shop) ve
[modüler](samples/Haldus.Sample.Modular) uygulama. Mağazanın üretilmiş istemcisi
[ayrı bir klasördedir](samples/Haldus.Sample.Shop.Client); onu tüketen [yönetim paneli](samples/Haldus.Sample.Shop.Web)
ve [mobil uygulama](samples/Haldus.Sample.Shop.Mobile) da öyle.

## Geliştirme

```bash
dotnet build
```

```bash
dotnet test
```

Entegrasyon testleri Docker gerektirir. Örnek arayüzlerin komutları (`pnpm dev`, `pnpm generate:api`, `pnpm typecheck`)
kendi README dosyalarındadır.

Kilide dayanan özelliklerin (boşluksuz numara, sıra, bakiye, toplanan alan, türetme, dönem) yük altındaki davranışı
[Haldus.LoadTest](tools/Haldus.LoadTest) ile ölçülür; araç çalışan bir örnek mağazaya HTTP üzerinden yüklenir. Bir
ölçümün sonuçları ve nasıl okunacağı [taksonomi sınamasının](reports/Haldus%20taksonomi%20sınaması.md#yük-altında-ölçüm)
"Yük altında ölçüm" bölümündedir. Özeti: aynı kilidi bekleyen yazmalar sıraya girer (ölçülen ortamda saniyede ~150),
kilidi anahtar başına olan özellikler (`MapBalance`, toplanan alanlar) farklı anahtarlarda eşzamanlılıkla ölçeklenir,
`DerivedFrom` ve `Orderable` ise kaynağın tamamını tek kilide bağlar.
