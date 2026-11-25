# İş mantığı

Haldus bir isteğin çevresindeki her şeyi üretir: kaydı bulmak, görünürlüğünü ve durumunu denetlemek, gövdeyi doğrulamak,
sürüm kontrolü, kaydetmek ve yanıtı yazmak. Alana özgü karar ise sizindir: "satırı olmayan sipariş verilemez",
"kuponun kullanım hakkı bitti", "kargo fiyatını hesapla". Bu sayfa o kararları nereye yazacağınızı anlatır: geçişler,
bir kayıt üzerinde çalışan aksiyonlar ve hiçbir kayda bağlı olmayan komut ve sorgular.

Bu sayfadaki örneklerde web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs` içindedir;
`builder` ve `app` satırları kısalık için yazılmamıştır. Hata yanıtları problem details biçimindedir
(`application/problem+json`); örneklerde yalnızca `title`, `status` ve `detail` gösterilir.

## İçindekiler

- [Mantığın yeri](#mantığın-yeri)
- [Geçiş mantığı](#geçiş-mantığı)
- [Reddetmek](#reddetmek)
- [Entity aksiyonları](#entity-aksiyonları)
- [Mantığın eklediği satırlar](#mantığın-eklediği-satırlar)
- [Hesaplanan alanlarla ilişkisi](#hesaplanan-alanlarla-ilişkisi)
- [Serbest aksiyonlar ve sorgular](#serbest-aksiyonlar-ve-sorgular)
- [Handler sınıflarının bulunması](#handler-sınıflarının-bulunması)
- [Mantığın yazmaması gerekenler](#mantığın-yazmaması-gerekenler)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Mantığın yeri

| Ne | Bildirim | Mantığın sözleşmesi |
|---|---|---|
| Durum geçişi | `orders.MapTransition<TRequest>(from, to)` | `ITransition<TEntity>`, `ITransitionHandler<TEntity, TRequest>` veya lambda |
| Kayıt üzerinde aksiyon | `coupons.MapAction<TRequest>()` | `IEntityAction<TEntity>`, `IEntityActionHandler<TEntity, TRequest>` veya lambda |
| Sonuç döndüren aksiyon | `products.MapAction<TRequest, TResponse>()` | `IEntityActionHandler<TEntity, TRequest, TResponse>` veya lambda |
| Kayda bağlı olmayan komut | `app.MapAction<TRequest, TResponse>()`, `app.MapAction<TRequest>()` | `IActionHandler<TRequest, TResponse>`, `IActionHandler<TRequest>` veya lambda |
| Kayda bağlı olmayan sorgu | `app.MapQuery<TRequest, TResponse>()` | `IQueryHandler<TRequest, TResponse>` veya lambda |
| Hesaplanan alan | Entity `IComputed` uygular | `void Compute()`; bkz. [Detaylar ve hesaplanan alanlar](details-and-computed-fields.md) |
| Arka plan işi | `app.MapJob<TJob>()` | `IJobHandler<TJob>` veya lambda; bkz. [Arka plan işleri](jobs.md) |
| Liste filtresi | `.Filterable<TFilter>()` | `IFilter<TEntity>`; bkz. [Listeleme](querying.md) |

Her sözleşmenin üç biçimi aynı fikri izler:

- **İstek tipi kendi etkisini uygular.** Bağımlılık gerekmeyen, kısa mantık için.
- **Handler sınıfı.** Bağımlılıkları constructor'dan alır (bir servis, `ILogger`, `IStringLocalizer`). Haldus sınıfı
  derleme sırasında bulur ve bağımlılık enjeksiyonuna kendisi kaydeder.
- **Lambda.** `Map*` çağrısına verilir. Bağımlılık gerekiyorsa `IServiceProvider` alan biçimi kullanılır.

## Geçiş mantığı

Bir geçişin durum kontrolünü, kaydetmeyi ve yanıtı Haldus üretir; geçişin kayıt üzerindeki etkisini siz yazarsınız.
Durum makinesinin kendisi [Durum makineleri](state-machines.md) sayfasındadır.

<!-- example: transition-logic -->
```csharp
var orders = app.MapCrud<Order>()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft);

orders.MapDetail<OrderItem>();

// 1. İstek tipi kendi etkisini uygular: POST /orders/{id}/cancel
orders.MapTransition<CancelOrder>(from: [OrderStatus.Draft, OrderStatus.Placed], to: OrderStatus.Cancelled);

// 2. Mantık bir handler sınıfındadır (PlaceOrderHandler): POST /orders/{id}/place
orders.MapTransition<PlaceOrder>(from: OrderStatus.Draft, to: OrderStatus.Placed);

// 3. Mantık lambda'dadır: POST /orders/{id}/reopen
orders.MapTransition<ReopenOrder>(from: OrderStatus.Cancelled, to: OrderStatus.Draft, (order, request) => order.Reopen());
```

Entity ve satırları:

<!-- example: transition-logic -->
```csharp
public enum OrderStatus
{
    Draft,
    Placed,
    Cancelled,
}

public partial class Order : IComputed
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";

    // Herkese açık setter'ı olmayan alanlar yalnızca yanıtta görünür; onları mantık yazar.
    public decimal Total { get; private set; }

    public string? PlacementNote { get; private set; }

    public string? CancellationReason { get; private set; }

    public void Compute() => Total = OrderItems.Sum(item => item.Quantity * item.UnitPrice);

    public void Place(string? note) => PlacementNote = note;

    public void Cancel(string reason) => CancellationReason = reason;

    public void Reopen() => CancellationReason = null;
}

public partial class OrderItem
{
    [MaxLength(200)]
    public string ProductName { get; set; } = "";

    [Range(1, 10_000)]
    public int Quantity { get; set; }

    [Range(0.0, 1_000_000.0)]
    public decimal UnitPrice { get; set; }
}
```

### İstek kendi etkisini uygular

İstek tipi `ITransition<TEntity>` uygular. `Apply`, kaydın durumu denetlendikten sonra, yüklenmiş kayıtla çağrılır.

<!-- example: transition-logic -->
```csharp
public sealed record CancelOrder : ITransition<Order>
{
    [Required, MaxLength(500)]
    public string Reason { get; init; } = "";

    public void Apply(Order order) => order.Cancel(Reason);
}
```

### Handler sınıfı

Mantık bir servise ihtiyaç duyuyorsa `ITransitionHandler<TEntity, TRequest>` uygulayan bir sınıf yazın. Haldus onu
bulur ve kaydeder; `IInventory` gibi kendi servislerinizi siz kaydedersiniz
(`builder.Services.AddScoped<IInventory, WarehouseInventory>()`).

<!-- example: transition-logic -->
```csharp
public sealed record PlaceOrder
{
    [MaxLength(500)]
    public string? Note { get; init; }
}

public interface IInventory
{
    Task<bool> HasStockAsync(string productName, int quantity, CancellationToken cancellationToken);
}

public sealed class PlaceOrderHandler(IInventory inventory) : ITransitionHandler<Order, PlaceOrder>
{
    public async Task HandleAsync(Order order, PlaceOrder request, CancellationToken cancellationToken)
    {
        if (order.OrderItems.Count == 0)
        {
            throw new TransitionRejectedException("An order without items cannot be placed.");
        }

        foreach (var item in order.OrderItems)
        {
            if (!await inventory.HasStockAsync(item.ProductName, item.Quantity, cancellationToken))
            {
                throw new TransitionRejectedException($"'{item.ProductName}' is out of stock.");
            }
        }

        order.Place(request.Note);
    }
}
```

Kayıt handler'a gömülü satırlarıyla (`MapDetail`) birlikte yüklenmiş gelir. Handler kaydı değiştirir, kaydetmez.

### Lambda

Kısa işler için mantık doğrudan `MapTransition` çağrısına verilir. İstek tipinin alanı yoksa bile bir tip yazılır:

<!-- example: transition-logic -->
```csharp
public sealed record ReopenOrder;
```

Gövdesiz geçişe de lambda verilebilir; o zaman lambda yalnızca kaydı alır:

<!-- fragment -->
```csharp
orders.MapTransition("ship", from: OrderStatus.Placed, to: OrderStatus.Shipped, order => order.MarkShipped());
```

Lambda bağımlılık enjeksiyonu almaz. Bir servise ihtiyaç varsa `IServiceProvider` ve `CancellationToken` alan,
`Task` döndüren biçim kullanılır; servisler isteğin kapsamından çözülür:

<!-- example: transition-services -->
```csharp
var shipments = app.MapCrud<Shipment>()
    .Workflow<ShipmentStatus>(initial: ShipmentStatus.Packed);

shipments.MapTransition(
    "dispatch",
    from: ShipmentStatus.Packed,
    to: ShipmentStatus.Dispatched,
    async (shipment, services, cancellationToken) =>
    {
        var carrier = services.GetRequiredService<ICarrier>();
        shipment.Dispatch(await carrier.BookAsync(shipment.Address, cancellationToken));
    });

public enum ShipmentStatus
{
    Packed,
    Dispatched,
}

public partial class Shipment
{
    [MaxLength(500)]
    public string Address { get; set; } = "";

    public string? TrackingNumber { get; private set; }

    public void Dispatch(string trackingNumber) => TrackingNumber = trackingNumber;
}

public interface ICarrier
{
    Task<string> BookAsync(string address, CancellationToken cancellationToken);
}
```

İstek tipli geçişin aynı biçimi `(entity, request, services, cancellationToken)` alır.

### Hangisi çalışır

Bir geçişte üç yol birlikte de bulunabilir. Haldus onları şu sırayla çalıştırır:

1. İstek tipi `ITransition<TEntity>` uyguluyorsa önce onun `Apply` metodu.
2. Çağrıya lambda verildiyse lambda.
3. Lambda verilmediyse ve kayıtlı bir `ITransitionHandler<TEntity, TRequest>` varsa handler.

Lambda verildiğinde handler çağrılmaz. Üçünden hiçbiri yoksa (istek `ITransition` uygulamıyor, lambda yok, handler
yok) uygulama açılırken, `MapTransition` çağrısı çalıştığında hata verir:

```text
System.InvalidOperationException: MapTransition<PlaceOrder> needs its logic, but 'PlaceOrder' does not implement
ITransition<Order>, no lambda was passed and no 'Haldus.ITransitionHandler`2[Order,PlaceOrder]' is registered. ...
```

Gövdesiz geçiş (`MapTransition("ship", from, to)`) mantık istemez; yalnızca durumu değiştirir.

## Reddetmek

Mantık işlemi kabul etmiyorsa `TransitionRejectedException` fırlatır. Hiçbir şey kaydedilmez (durum, satırlar, numara)
ve istemci 422 alır; `detail` istisnanın mesajıdır.

```http
POST /orders/0196f3c2-7d4e-7a51-9a3b-2f6c1d8e4a10/place
Content-Type: application/json

{}
```

```http
HTTP/1.1 422 Unprocessable Entity
Content-Type: application/problem+json
```

```json
{
  "title": "The operation was rejected",
  "status": 422,
  "detail": "An order without items cannot be placed."
}
```

`title` Haldus'un mesajıdır ve isteğin diline çevrilir. `detail` ise olduğu gibi gider: son kullanıcıya gösterilecek
metni, isteğin dilinde, siz üretirsiniz. Handler bunun için `IStringLocalizer<T>` alabilir:

<!-- fragment -->
```csharp
throw new TransitionRejectedException(messages["OrderHasNoItems"]);
```

Kurallar:

- Reddedilen geçiş numara harcamaz: `Numbered(assignOn:)` ile ayrılan numara geri verilir.
- İstisna geçişlerde, entity aksiyonlarında ve [serbest aksiyon ve sorgularda](#serbest-aksiyonlar-ve-sorgular) 422'ye
  çevrilir.
- Başka bir istisna fırlatmak 422 değildir; isteğin bir hatasıdır.
- Mantığın içinde durum kontrolü yazmayın: yanlış durum `from:` ile 409 olarak zaten reddedilir.

## Entity aksiyonları

Bir kayıt üzerinde çalışan ama durumunu değiştirmeyen işlemler (kuponu kullanmak, süresini uzatmak, indirimi
hesaplamak) kaynağın builder'ından `MapAction` ile bildirilir. Durum makinesi gerekmez. Haldus kaydı bulur, sürümünü
denetler, mantığı çalıştırır, kaydeder ve yanıtı döndürür.

<!-- example: coupons -->
```csharp
var coupons = app.MapCrud<Coupon>();

// Gövdesiz; mantık lambda'dadır: POST /coupons/{id}/redeem
coupons.MapAction("redeem", coupon =>
{
    if (!coupon.TryRedeem())
    {
        throw new TransitionRejectedException("This coupon has no redemptions left.");
    }
});

// İstek tipli; mantık istek tipindedir: POST /coupons/{id}/extend
coupons.MapAction<ExtendCoupon>();

// Kendi sonucunu döndürür; mantık handler sınıfındadır: POST /coupons/{id}/estimate-discount
coupons.MapAction<EstimateDiscount, DiscountEstimate>();
```

Entity, istekler ve handler:

<!-- example: coupons -->
```csharp
public partial class Coupon
{
    [Required, MaxLength(40)]
    public string Code { get; set; } = "";

    [Range(1, 90)]
    public int Percent { get; set; }

    [Range(1, 10_000)]
    public int MaxRedemptions { get; set; } = 1;

    public int TimesRedeemed { get; private set; }

    public DateTimeOffset ValidUntil { get; set; }

    public bool TryRedeem()
    {
        if (TimesRedeemed >= MaxRedemptions)
        {
            return false;
        }

        TimesRedeemed++;
        return true;
    }
}

public sealed record ExtendCoupon : IEntityAction<Coupon>
{
    [Range(1, 365)]
    public int Days { get; init; }

    public void Apply(Coupon coupon) => coupon.ValidUntil = coupon.ValidUntil.AddDays(Days);
}

public sealed record EstimateDiscount
{
    [Range(0.0, 1_000_000.0)]
    public decimal BasketTotal { get; init; }
}

public sealed record DiscountEstimate(decimal Discount, decimal Total);

public sealed class EstimateDiscountHandler : IEntityActionHandler<Coupon, EstimateDiscount, DiscountEstimate>
{
    public Task<DiscountEstimate> HandleAsync(Coupon coupon, EstimateDiscount request, CancellationToken cancellationToken)
    {
        var discount = Math.Round(request.BasketTotal * coupon.Percent / 100m, 2);
        return Task.FromResult(new DiscountEstimate(discount, request.BasketTotal - discount));
    }
}
```

| Bildirim | Uç | Gövde | Yanıt |
|---|---|---|---|
| `MapAction("redeem", ...)` | `POST /coupons/{id}/redeem` | Yok | 200, kaydın güncel hali |
| `MapAction<ExtendCoupon>()` | `POST /coupons/{id}/extend` | `ExtendCoupon` | 200, kaydın güncel hali |
| `MapAction<EstimateDiscount, DiscountEstimate>()` | `POST /coupons/{id}/estimate-discount` | `EstimateDiscount` | 200, `DiscountEstimate` |

Aksiyonun adresi geçişlerdeki kuralla türetilir: sondaki `Request` / `Command` ve entity'nin adı atılır, kalan ad
kebab-case yazılır (`ExtendCoupon` → `extend`). `name:` ile değiştirilir. Gövdesiz aksiyonun adı küçük harf, rakam ve
tireden oluşan bir sabittir.

```http
POST /coupons/0196f3d0-1b2a-7c44-8e15-6a9d3f0b7c21/extend
Content-Type: application/json

{ "days": 30 }
```

```http
HTTP/1.1 200 OK
```

```json
{
  "id": "0196f3d0-1b2a-7c44-8e15-6a9d3f0b7c21",
  "code": "SPRING",
  "percent": 10,
  "maxRedemptions": 100,
  "timesRedeemed": 4,
  "validUntil": "2026-12-31T00:00:00+00:00"
}
```

Sonuç döndüren aksiyonun yanıtı kaydın kendisi değil, mantığın ürettiği sonuçtur. Kayıtta yapılan değişiklikler yine
kaydedilir.

```http
POST /coupons/0196f3d0-1b2a-7c44-8e15-6a9d3f0b7c21/estimate-discount
Content-Type: application/json

{ "basketTotal": 250 }
```

```json
{ "discount": 25, "total": 225 }
```

### Mantığın üç biçimi

| Aksiyon | İstek tipi | Handler sınıfı | Lambda |
|---|---|---|---|
| Gövdesiz | Yok | Yok | `MapAction("ad", entity => ...)` veya `MapAction("ad", (entity, services, ct) => ...)` |
| İstek tipli | `IEntityAction<TEntity>` | `IEntityActionHandler<TEntity, TRequest>` | `MapAction<TRequest>((entity, request) => ...)` veya `(entity, request, services, ct)` |
| Sonuç döndüren | Yok | `IEntityActionHandler<TEntity, TRequest, TResponse>` | `MapAction<TRequest, TResponse>((entity, request) => sonuç)` veya `(entity, request, services, ct)` |

Sıra geçişlerdekiyle aynıdır: önce isteğin `Apply` metodu, sonra lambda, lambda yoksa kayıtlı handler. Mantığı hiçbir
yerde olmayan aksiyon uygulama açılırken hata verir. Sonuç döndüren aksiyonda mantık zorunludur: bir handler sınıfı veya
lambda.

### Kurallar ve hata yanıtları

Aksiyon kaydı değiştirdiği için kaynağın yazma kurallarına uyar:

| Durum | Yanıt |
|---|---|
| Kayıt yok veya isteği yapana görünmüyor | 404 |
| Gövde doğrulanamadı | 400 |
| Kaynak `ConcurrencyChecked` ve `If-Match` yok / eski | 428 / 412 |
| Mantık `TransitionRejectedException` fırlattı | 422; hiçbir şey kaydedilmez |
| Kaynağın durumu bu sırada başka bir istekle değişti | 409 |
| Aksiyon `Approvable(..., "ad")` ile onaya bağlı ve kayıt onaylı değil | 409 |
| Kayıt ters çevrilmiş veya yerine geçilmiş (kapanmış) | 409 |

- Aksiyon durumu değiştirmez ve `EditableIn` kuralına bağlı değildir: taslak olmayan bir siparişte de çalışır.
  Reddetmesi gerekiyorsa mantığı `TransitionRejectedException` fırlatır.
- Kaynak `Idempotent()` ise aksiyon da `Idempotency-Key` ile tekrara karşı korunur.
- Aksiyon bir kaydı anahtarıyla bulur. `MapSingleton` ve gömülü detaylarda kullanılamaz (`HALDUS007`); `MapStore`
  kaynakta adres `/{key}/ad` olur.
- Bir kaynağın builder'ında çağrılan `MapAction` her zaman o kaynağın bir kaydı üzerinde çalışır; `app.MapAction`
  ise kayda bağlı değildir (bkz. [Serbest aksiyonlar ve sorgular](#serbest-aksiyonlar-ve-sorgular)).
- Aynı kaynakta bir aksiyon bir geçişle veya bir trait'in ucuyla (`archive`, `approve`, `move`...) aynı adı taşıyamaz
  (`HALDUS007`).
- Aksiyon ayrı bir olay yazmaz; `PublishesEvents` kaynağında kayıt değiştiyse `.updated` olayı yazılır.

## Mantığın eklediği satırlar

Geçiş ve aksiyon kaydı gömülü satırlarıyla birlikte alır; satırlar istekte gönderildikleri sırayla dizilmiş olarak
gelir. Mantık satır ekleyip çıkarabilir. Eklenen satırlara kimliği ve sırayı Haldus verir; satırlar kayıtla birlikte,
tek kaydetmede yazılır.

<!-- example: variants -->
```csharp
var garments = app.MapCrud<Garment>();

garments.MapDetail<GarmentVariant>();

// POST /garments/{id}/generate-variants  { "sizes": ["S", "M"], "colors": ["red", "blue"] }
garments.MapAction<GenerateVariants>();

public partial class Garment
{
    [Required, MaxLength(100)]
    public string Name { get; set; } = "";
}

public partial class GarmentVariant
{
    [Required, MaxLength(100)]
    public string Sku { get; set; } = "";

    [MaxLength(20)]
    public string Size { get; set; } = "";

    [MaxLength(20)]
    public string Color { get; set; } = "";
}

public sealed class GenerateVariants : IEntityAction<Garment>
{
    [Required, MinLength(1), MaxLength(10)]
    public string[] Sizes { get; set; } = [];

    [Required, MinLength(1), MaxLength(10)]
    public string[] Colors { get; set; } = [];

    public void Apply(Garment garment)
    {
        foreach (var size in Sizes)
        {
            foreach (var color in Colors)
            {
                // Var olan birleşim korunur; yalnızca eksik olanlar eklenir.
                if (!garment.GarmentVariants.Any(variant => variant.Size == size && variant.Color == color))
                {
                    garment.GarmentVariants.Add(new GarmentVariant { Size = size, Color = color, Sku = $"{garment.Name}-{size}-{color}" });
                }
            }
        }
    }
}
```

Yeni satırın `Id` alanını boş bırakın; Haldus doldurur. Satırların sırası, koleksiyondaki yerleridir.

## Hesaplanan alanlarla ilişkisi

Entity `IComputed` uyguluyorsa Haldus `Compute()` metodunu her yazmada, kaydetmeden hemen önce çağırır. Geçişte ve
aksiyonda sıra şudur:

1. Kayıt satırlarıyla yüklenir, durumu denetlenir.
2. Mantık çalışır (istek, lambda veya handler).
3. Mantığın eklediği satırlara kimlik ve sıra verilir.
4. Geçişin hedef durumu yazılır.
5. `Compute()` çağrılır: kayıt artık yeni durumundadır ve mantığın eklediği satırları içerir.
6. Gerekiyorsa numara ayrılır ve kayıt kaydedilir.

Bu yüzden toplamları ve sayıları mantığın içinde hesaplamayın; `Compute()` her yoldan (oluşturma, güncelleme, geçiş,
aksiyon, toplu yol) aynı biçimde çağrılır. Ayrıntı: [Detaylar ve hesaplanan alanlar](details-and-computed-fields.md).

## Serbest aksiyonlar ve sorgular

Bir kayda bağlı olmayan işlemler `app` üzerinden bildirilir. `MapAction` yan etkili bir komutu `POST` ile, `MapQuery`
yan etkisiz bir hesabı `GET` ile sunar.

<!-- example: operations -->
```csharp
app.MapCrud<Product>();

app.MapQuery<ShippingQuoteRequest, ShippingQuote>();          // GET /shipping-quote
app.MapAction<ApplyDiscountRequest, ApplyDiscountResult>();   // POST /apply-discount

// Sonuç döndürmeyen komut; mantık lambda'dadır: POST /clear-maintenance-notice
app.MapAction<ClearMaintenanceNotice>(async (_, services, cancellationToken) =>
{
    var notices = services.GetRequiredService<IMaintenanceNotices>();
    await notices.ClearAsync(cancellationToken);
});
```

Sorgunun isteği ve handler'ı:

<!-- example: operations -->
```csharp
/// <summary>GET /shipping-quote?country=TR&amp;weight=2.5&amp;express=true</summary>
public sealed class ShippingQuoteRequest
{
    [Required, StringLength(2, MinimumLength = 2)]
    public string? Country { get; set; }

    [Range(0.01, 1000.0)]
    public decimal Weight { get; set; } = 1m;

    public bool Express { get; set; }
}

public sealed record ShippingQuote(decimal Price);

public sealed class ShippingQuoteHandler : IQueryHandler<ShippingQuoteRequest, ShippingQuote>
{
    public Task<ShippingQuote> HandleAsync(ShippingQuoteRequest request, CancellationToken cancellationToken)
    {
        var price = 10m + (request.Weight * (request.Country == "TR" ? 2m : 5m));
        return Task.FromResult(new ShippingQuote(request.Express ? price * 2 : price));
    }
}
```

Komutun isteği ve handler'ı:

<!-- example: operations -->
```csharp
/// <summary>POST /apply-discount: adı verilen önekle başlayan ürünlere indirim uygular.</summary>
public sealed record ApplyDiscountRequest
{
    [Required, MinLength(3)]
    public string NamePrefix { get; init; } = "";

    [Range(1, 90)]
    public int Percent { get; init; }
}

public sealed record ApplyDiscountResult(int ProductsChanged);

public sealed class ApplyDiscountHandler(AppDbContext db) : IActionHandler<ApplyDiscountRequest, ApplyDiscountResult>
{
    public async Task<ApplyDiscountResult> HandleAsync(ApplyDiscountRequest request, CancellationToken cancellationToken)
    {
        var factor = 1m - (request.Percent / 100m);

        var changed = await db.Set<Product>()
            .Where(product => product.Name.StartsWith(request.NamePrefix))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(product => product.Price, product => product.Price * factor),
                cancellationToken);

        return new ApplyDiscountResult(changed);
    }
}

public sealed record ClearMaintenanceNotice;

public interface IMaintenanceNotices
{
    Task ClearAsync(CancellationToken cancellationToken);
}

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}
```

| Bildirim | Uç | İstek | Yanıt |
|---|---|---|---|
| `app.MapQuery<TRequest, TResponse>()` | `GET /{ad}` | Sorgu parametreleri | 200, `TResponse` |
| `app.MapAction<TRequest, TResponse>()` | `POST /{ad}` | JSON gövde | 200, `TResponse` |
| `app.MapAction<TRequest>()` | `POST /{ad}` | JSON gövde | 204 |

Adres, verilmediyse istek tipinin adından türetilir: sondaki `Request`, `Command` veya `Query` atılır ve kalan ad
kebab-case yazılır (`ShippingQuoteRequest` → `/shipping-quote`). Adresi kendiniz vermek için ilk argüman kullanılır:
`app.MapQuery<ShippingQuoteRequest, ShippingQuote>("shipping/quote")`.

### Sorgu

Sorgunun istek tipinin herkese açık, yazılabilir özellikleri camelCase sorgu parametresi olur. Dizi özellikleri
parametrenin yinelenmesiyle bağlanır (`?numbers=1&numbers=2`). Gönderilmeyen parametre, sınıftaki varsayılan değeri
korur (yukarıda `weight` verilmezse `1`).

```http
GET /shipping-quote?country=TR&weight=2&express=true
```

```json
{ "price": 28 }
```

İstek tipinin parametresiz bir constructor'ı ve yazılabilir özellikleri olmalıdır; konumsal bir `record` (`record
QuoteRequest(string Country)`) `HALDUS008` verir. Sorgu ucu derleme sırasında üretilir; bu yüzden `MapQuery`
generator gerektirir, `MapAction` gerektirmez (gövdesi çalışma zamanında bağlanır).

### Doğrulama

İsteğin DataAnnotations'ı her iki türde de doğrulanır. Doğrulanamayan istek 400 alır; hata alanları camelCase'tir ve
mesajlar isteğin dilindedir:

```http
GET /shipping-quote?weight=5000
Accept-Language: tr
```

```json
{
  "title": "Bir veya daha fazla doğrulama hatası oluştu.",
  "status": 400,
  "errors": {
    "country": ["Country alanı zorunludur."],
    "weight": ["Weight alanı 0,01 ile 1000 arasında olmalıdır."]
  }
}
```

### Komut

```http
POST /apply-discount
Content-Type: application/json

{ "namePrefix": "Desk", "percent": 25 }
```

```json
{ "productsChanged": 2 }
```

Yanlış yöntemle gelen istek (`GET /apply-discount`, `POST /shipping-quote`) 405 alır.

### Lambda

Handler sınıfı yerine lambda verilebilir. Lambda bağımlılık enjeksiyonu almaz; iki biçimi vardır:

<!-- fragment -->
```csharp
// Yalnızca istek ve iptal belirteci.
app.MapQuery<EchoRequest, EchoResponse>((request, cancellationToken) =>
    Task.FromResult(new EchoResponse(request.Message ?? "pong")));

// Servisler isteğin kapsamından çözülür.
app.MapAction<ApplyDiscountRequest, ApplyDiscountResult>(async (request, services, cancellationToken) =>
{
    var db = services.GetRequiredService<AppDbContext>();
    // ...
    return new ApplyDiscountResult(0);
});
```

### Kurallar

- Handler sınıfı olmadan, lambda da verilmeden çağrılan `app.MapAction` / `app.MapQuery` uygulama açılırken hata verir:
  `MapAction needs a handler, but no 'Haldus.IActionHandler`2[...]' is registered.`
- Bu uçlar bir kaynağa ait değildir: kiracı, sahiplik ve durum kuralları uygulanmaz. Mantığın okuduğu ve yazdığı
  kayıtlar yine de bağlamın sorgu filtrelerinden geçer (kiracı filtresi isteğin kiracısını okur).
- Yetkilendirme dönen builder'a eklenir: `app.MapAction<ApplyDiscountRequest, ApplyDiscountResult>().RequireAuthorization("staff")`.
- Mantık `TransitionRejectedException` fırlatırsa istemci 422 alır; `detail` istisnanın mesajıdır. Kayda bağlı
  olmadıkları için geri alınacak bir kaydetme yoktur: mantığın istisnadan önce yazdıkları mantığın kendi işidir.
- `Idempotent()` bir kaynağın bildirimidir; serbest aksiyonlar tekrara karşı korunmaz.

## Handler sınıflarının bulunması

Haldus, projedeki (ve projenin başvurduğu Haldus modüllerindeki) şu arayüzleri uygulayan sınıfları derleme sırasında
bulur:

| Arayüz | Kullanıldığı yer |
|---|---|
| `ITransitionHandler<TEntity, TRequest>` | `MapTransition<TRequest>` |
| `IEntityActionHandler<TEntity, TRequest>` | `MapAction<TRequest>` (kaynağın builder'ında) |
| `IEntityActionHandler<TEntity, TRequest, TResponse>` | `MapAction<TRequest, TResponse>` (kaynağın builder'ında) |
| `IActionHandler<TRequest, TResponse>`, `IActionHandler<TRequest>` | `app.MapAction` |
| `IQueryHandler<TRequest, TResponse>` | `app.MapQuery` |
| `IJobHandler<TJob>` | `app.MapJob`; bkz. [Arka plan işleri](jobs.md) |

Kurallar:

- Sınıflar `AddHaldus<TContext>()` çağrısı sırasında, her arayüz için ayrı olarak, **scoped** kaydedilir. Bir sınıf
  birden çok arayüz uygulayabilir.
- Kayıt `TryAdd` ile yapılır: aynı arayüze `AddHaldus`'tan önce kendi uygulamanızı kaydederseniz sizinki kullanılır.
- Soyut, statik ve generic sınıflar kaydedilmez.
- Handler'ın bağımlılıkları constructor'dan gelir; onları (`IInventory`, `ICarrier`...) siz kaydedersiniz.
- Mantığı bulunamayan bir geçiş, aksiyon veya sorgu ilk istekte değil, uygulama açılırken (ilgili `Map*` çağrısı
  çalıştığında) `InvalidOperationException` verir. Mesaj eksik arayüzü ve eklenebilecek yolları söyler.

## Mantığın yazmaması gerekenler

- **`SaveChangesAsync` çağırmayın.** Haldus kaydı, mantık döndükten sonra durumu, satırların sırasını, hesaplanan
  alanları ve numarayı yazıp tek kaydetmede saklar. Mantığın ortasında yapılan bir kaydetme bu sırayı böler; ardından
  gelen bir ret o yazmayı geri alamaz. Aynı kural `Compute()` için de geçerlidir.
- **`Status` alanına değer atamayın.** Hedef durumu geçiş yazar; durum yalnızca geçişlerle değişir.
- **Durum kontrolünü mantığa yazmayın.** `from:` ve `EditableIn` bunu yapar ve yazmayla birlikte yineler.
- **Haldus'un yönettiği alanları yazmayın** (`Number`, `CreatedAt`, `TenantId`, onay alanları...).
- **Kaydı yeniden yüklemeyin.** Mantığa gelen kayıt, isteği yapanın görebildiği ve satırlarıyla yüklenmiş kayıttır;
  geçişte durumu da denetlenmiştir.

## Birlikte kullanım

| Bildirim | Mantıkla ilişkisi |
|---|---|
| `Workflow(...)` | Geçişlerin durum makinesi; bkz. [Durum makineleri](state-machines.md). |
| `ConcurrencyChecked()` | Geçiş ve aksiyon `If-Match` ister (428 / 412). |
| `Idempotent()` | Geçiş ve aksiyon `Idempotency-Key` ile korunur. |
| `Numbered(assignOn:)` | Numara mantık başarılı olursa ayrılır; ret numara harcamaz. |
| `Approvable(levels, "ad")` | Sayılan geçiş ve aksiyonlar onaydan önce çalışmaz; onaya bağlı olmayan bir aksiyon içeriği değiştirirse toplanan onaylar düşer. Bkz. [Onay ve belge](approvals.md). |
| `Reversible`, `Supersedes`, `Document` | Kapanmış (ters çevrilmiş, yerine geçilmiş, düzeltilmiş) kayıtta geçiş ve aksiyon 409 alır; bkz. [Kayıtlar arası zincir](record-chains.md). |
| `CreatedFrom<T>(on: "capture")` | Geçiş başka bir kaynakta, aynı transaction'da kayıt doğurur. |
| `PublishesEvents()` | Geçiş kendi olayını (`order.place`) yazar; aksiyon yazmaz. |
| `MapDetail<T>()` | Satırlar mantığa yüklenmiş gelir; mantık satır ekleyip çıkarabilir. |

## Sınırlar

- Geçişlerin ve aksiyonların toplu biçimi yoktur.
- Lambda bağımlılık enjeksiyonu almaz; servisler `IServiceProvider` alan biçimle çözülür.
- Serbest aksiyon ve sorgular kayda bağlı değildir: kiracı, sahiplik ve durum kurallarından ve tekrara karşı korumadan
  yararlanmaz. Yetkilendirme kuralı da kaynak kapsamı bilinmediği için sizin yazmanız gerekir.
- Sorgu isteği yalnızca sorgu parametrelerinden doldurulur; gövde almaz ve her özellik ayrı bir parametredir.
- Generic handler sınıfları bulunmaz.

## Ayrıca bakın

- [Durum makineleri](state-machines.md)
- [Onay ve belge](approvals.md)
- [Detaylar ve hesaplanan alanlar](details-and-computed-fields.md)
- [Arka plan işleri](jobs.md)
- [Bildirim dizini](../reference/declarations.md#durum-ve-iş-mantığı)
- [HTTP sözleşmesi](../reference/http.md)
