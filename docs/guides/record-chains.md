# Kayıtlar arası zincir

Bazı kayıtlar başka bir kayıttan doğar. Muhasebede kaydedilmiş bir hareket silinmez, onu nötrleyen bir ters kayıtla
düzeltilir. Gönderilmiş bir teklif değiştirilmez, yerine yenisi geçer. Sepet siparişe dönüşür, tahsil edilen ödeme bir
makbuz doğurur. Stok seviyesi tek tek hareketlerden hesaplanır. Bu sayfa bu dört ilişkiyi anlatır: Haldus yeni kaydı
kurar, iki kaydı birbirine bağlar, gerekli kuralları uygular ve hepsini tek bir kaydetmede yazar. Siz yalnızca farkı
yazarsınız.

> [!NOTE]
> Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
> `System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs` içindedir;
> `var builder = WebApplication.CreateBuilder(args);` ve `var app = builder.Build();` satırları yazılmamışsa yazılmış
> sayılır. Hata gövdeleri kısaltılmıştır.

## İçindekiler

- [Ters kayıt](#ters-kayıt)
- [Yerine geçen kayıt](#yerine-geçen-kayıt)
- [Kapanmış kayıtlar](#kapanmış-kayıtlar)
- [Başka bir kayıttan üretmek](#başka-bir-kayıttan-üretmek)
  - [Geçişle doğan kayıt](#geçişle-doğan-kayıt)
- [Türetilmiş kaynak](#türetilmiş-kaynak)
- [Ortak kurallar](#ortak-kurallar)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Ters kayıt

`.Reversible()` bir kaydı, onu nötrleyen karşı kayıtla düzeltmenin yolunu açar. Ters kayıt asıl kaydın kopyası olarak
doğar; entity `IReversal<T>` uygularsa Haldus kopyadan sonra onu çağırır ve siz yalnızca farkı yazarsınız (tutarın
işaretini çevirmek gibi). İki kayıt birbirine işaret eder.

<!-- example: reverse -->
```csharp
app.MapAppendOnly<StockMovement>()
    .Auditable()
    .Reversible();

public partial class StockMovement : IReversal<StockMovement>
{
    public Guid ProductId { get; set; }

    [Range(-1_000_000, 1_000_000)]
    public int Quantity { get; set; }

    [MaxLength(200)]
    public string? Reason { get; set; }

    // Haldus ters hareketi bu hareketin kopyası olarak kurar; burada yalnızca miktarın yönü çevrilir.
    public void Reverse(StockMovement original) => Quantity = -original.Quantity;
}
```

<!-- fragment -->
```csharp
public interface IReversal<in TEntity>
{
    void Reverse(TEntity original);
}
```

Haldus iki alan ekler: `ReversesId` (ters kayıtta, asıl kaydı gösterir) ve `ReversedById` (asıl kayıtta, ters kaydı
gösterir). İkisi de yalnızca yanıtta görünür; istekle yazılamaz.

**Üretilen uçlar**

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/stock-movements/{id}/reverse` | Ters kaydı oluşturur ve döndürür (`201`). |

Gövde isteğe bağlıdır ve kısmi güncelleme biçimindedir: gönderilen alanlar ters kayıtta kopyanın ve sözleşmenin
yazdıklarının yerine geçer.

```http
POST /stock-movements/0199a3f0-0a00-7c10-8a10-0a1b2c3d4e50/reverse
Content-Type: application/json

{ "reason": "Sayım hatası" }
```

```http
HTTP/1.1 201 Created
Location: /stock-movements/0199a3f0-1b00-7d20-9b20-1b2c3d4e5f60

{
  "id": "0199a3f0-1b00-7d20-9b20-1b2c3d4e5f60",
  "productId": "0199a3ef-0000-7000-8000-000000000001",
  "quantity": -10,
  "reason": "Sayım hatası",
  "createdAt": "2026-11-25T10:15:00.000000+00:00",
  "updatedAt": null,
  "reversesId": "0199a3f0-0a00-7c10-8a10-0a1b2c3d4e50",
  "reversedById": null
}
```

Asıl kayıt artık `"reversedById": "0199a3f0-1b00-7d20-9b20-1b2c3d4e5f60"` taşır ve miktarı değişmez.

**Kurallar ve hata yanıtları**

- Bir kayıt yalnızca bir kez ters çevrilir. İkinci istek `409 Conflict` döner; aynı anda gelen istekler de yalnızca bir
  ters kayıt bırakır, ötekiler `409` alır.
- Ters kaydın kendisi ters çevrilemez (`409`): ters kayıt zincirin sonudur.

  ```http
  HTTP/1.1 409 Conflict

  {
    "title": "Record can no longer be changed",
    "status": 409,
    "detail": "StockMovement '0199a3f0-0a00-7c10-8a10-0a1b2c3d4e50' has been reversed and can no longer be changed or reversed again."
  }
  ```

- Kayıt yoksa veya görünmüyorsa `404` döner.
- Ters çevrilmiş kayıt ve ters kaydın kendisi kapanır; bkz. [Kapanmış kayıtlar](#kapanmış-kayıtlar).

**Durum makinesi olan kaynakta**

Ters çevirme, kaydın düzenlenemediği durumlarda da çalışan bir yazmadır. Bu yüzden kaynağın durum makinesi varsa, ters
çevirmenin yapılabildiği durumlar açıkça sayılır; başka bir durumdaki kayıt `409` alır:

<!-- example: reverse-states -->
```csharp
var payments = app.MapCrud<Payment>()
    .Workflow<PaymentStatus>(initial: PaymentStatus.Pending)
    .EditableIn(PaymentStatus.Pending)
    .Reversible(PaymentStatus.Captured);

payments.MapTransition("capture", from: PaymentStatus.Pending, to: PaymentStatus.Captured);

public enum PaymentStatus
{
    Pending,
    Captured,
}

public partial class Payment : IReversal<Payment>
{
    [Range(-1_000_000.0, 1_000_000.0)]
    public decimal Amount { get; set; }

    public void Reverse(Payment original) => Amount = -original.Amount;
}
```

Durum makinesi olan bir kaynakta durumlar sayılmazsa veya durum makinesi olmayan bir kaynakta sayılırsa `HALDUS010`
verilir. Ters kayıt, her yeni kayıt gibi başlangıç durumunda doğar ve geçişlerle ilerletilir.

**Seçenekler**

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `states` (`params Enum[]`) | yok | Ters çevirmenin yapılabildiği durumlar. Durum makinesi olan kaynakta zorunlu, olmayanda yasaktır. |

## Yerine geçen kayıt

`.Supersedes()` bir kaydın yerine yenisini koyar: gönderilmiş bir teklif düzeltilmez, yerine yeni bir teklif geçer.
Eski kayıt kalır, listede görünmeye devam eder ve artık değiştirilemez. Yeni kayıt eskisinin kopyası olarak doğar;
entity `ISupersession<T>` uygularsa Haldus kopyadan sonra onu çağırır. İstekte gönderilen alanlar en son yazılır.

<!-- example: supersede -->
```csharp
var quotes = app.MapCrud<Quote>()
    .Workflow<QuoteStatus>(initial: QuoteStatus.Draft)
    .EditableIn(QuoteStatus.Draft)
    .Supersedes(QuoteStatus.Sent);

quotes.MapTransition("send", from: QuoteStatus.Draft, to: QuoteStatus.Sent);
quotes.MapTransition("withdraw", from: QuoteStatus.Sent, to: QuoteStatus.Withdrawn);

public enum QuoteStatus
{
    Draft,
    Sent,
    Withdrawn,
}

public partial class Quote : ISupersession<Quote>
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";

    [Range(0.0, 10_000_000.0)]
    public decimal Amount { get; set; }

    [MaxLength(500)]
    public string? Terms { get; set; }

    public int Revision { get; private set; } = 1;

    public void Supersede(Quote previous) => Revision = previous.Revision + 1;
}
```

<!-- fragment -->
```csharp
public interface ISupersession<in TEntity>
{
    void Supersede(TEntity previous);
}
```

Haldus iki alan ekler: `SupersedesId` (yeni kayıtta) ve `SupersededById` (eski kayıtta).

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/quotes/{id}/supersede` | Yerine geçen kaydı oluşturur ve döndürür (`201`). Gövde isteğe bağlıdır ve kısmi güncelleme biçimindedir. |

```http
POST /quotes/0199a3f1-0a00-7c10-8a10-0a1b2c3d4e50/supersede
Content-Type: application/json

{ "amount": 900 }
```

```http
HTTP/1.1 201 Created
Location: /quotes/0199a3f1-1b00-7d20-9b20-1b2c3d4e5f60

{
  "id": "0199a3f1-1b00-7d20-9b20-1b2c3d4e5f60",
  "customerName": "Ada Yılmaz",
  "amount": 900,
  "terms": "30 gün vadeli",
  "revision": 2,
  "status": "Draft",
  "supersedesId": "0199a3f1-0a00-7c10-8a10-0a1b2c3d4e50",
  "supersededById": null
}
```

**Kurallar ve hata yanıtları**

- Yeni kayıt başlangıç durumunda (`Draft`) doğar; düzenlenir, gönderilir ve onun da yerine geçilebilir. Zincir böylece
  uzar: yalnızca yerine geçilmiş olan kayıt kapanır.
- Yerine geçme yalnızca sayılan durumlarda yapılır; başka bir durumdaki kayıt `409` alır. Yukarıda taslak ve geri
  çekilmiş teklifin yerine geçilemez.
- Bir kaydın yerine yalnızca bir kez geçilir; ikinci istek `409` döner.
- Eski kayıt kapanır; bkz. [Kapanmış kayıtlar](#kapanmış-kayıtlar).

**Seçenekler**

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `states` (`params Enum[]`) | yok | Yerine geçmenin yapılabildiği durumlar. Durum makinesi olan kaynakta zorunlu, olmayanda yasaktır (`HALDUS010`). |

## Kapanmış kayıtlar

Ters çevrilmiş veya yerine geçilmiş bir kayıt kapanır. Kapanmış kayıtta hiçbir yazma çalışmaz:

| İşlem | Kapanmış kayıtta |
|---|---|
| `PUT`, `PATCH` | `409 Conflict` |
| Geçişler ve entity aksiyonları | `409 Conflict` |
| İkinci kez ters çevirmek veya yerine geçmek | `409 Conflict` |
| Silmek | `409 Conflict` |

Yanıtın başlığı `Record can no longer be changed`, Türkçede `Kayıt artık değiştirilemez` olur; ayrıntı nedenini söyler.

Ters kaydın kendisi de `PUT` ve `PATCH` ile değiştirilemez: alanları asıl kayıttan türemiştir. Ama geçişlerden geçebilir,
çünkü başlangıç durumunda doğar ve ilerletilmesi gerekir.

Bir çiftin parçası olan kayıt silinemez; ikisinden biri silinseydi öteki hiçbir şeye işaret etmez ya da sonsuza dek
kapalı kalırdı. Bu yüzden bir teklifin yerine geçen yeni kayıt da silinemez. Hiçbir çifte girmemiş kayıt kaynağın
kendi kurallarıyla silinir.

## Başka bir kayıttan üretmek

`.CreatedFrom<TSource>()` bir kaydın başka tipten bir kayıttan üretildiğini bildirir: sepetten sipariş, faturadan alacak
dekontu. Haldus kaynağa işaret eden alanı (`CartId`) ve hedefin koleksiyonunda bir üretme ucu ekler. Aynı adlı ve aynı
tipli alanlar kaynaktan kopyalanır; entity `ICreatedFrom<TSource>` uygularsa gerisini o doldurur.

<!-- example: created-from -->
```csharp
var carts = app.MapCrud<Cart>();
carts.MapDetail<CartItem>();

var orders = app.MapCrud<Order>().CreatedFrom<Cart>(onDelete: OnDelete.SetNull);
orders.MapDetail<OrderItem>();

public partial class Cart
{
    [MaxLength(200)]
    public string? Note { get; set; }
}

public partial class CartItem
{
    [MaxLength(200)]
    public string ProductName { get; set; } = "";

    [Range(1, 1_000)]
    public int Quantity { get; set; }
}

public partial class Order : ICreatedFrom<Cart>
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";

    // Sepette de aynı adla ve tiple bulunduğu için kendiliğinden kopyalanır.
    [MaxLength(200)]
    public string? Note { get; set; }

    // Adı farklı alanlar ve satırlar burada taşınır. Satırların kimliğini ve sırasını Haldus verir.
    public void CreateFrom(Cart source)
    {
        foreach (var item in source.CartItems)
        {
            OrderItems.Add(new OrderItem { ProductName = item.ProductName, Quantity = item.Quantity });
        }
    }
}

public partial class OrderItem
{
    [MaxLength(200)]
    public string ProductName { get; set; } = "";

    [Range(1, 10_000)]
    public int Quantity { get; set; }
}
```

<!-- fragment -->
```csharp
public interface ICreatedFrom<in TSource>
{
    void CreateFrom(TSource source);
}
```

**Üretilen uçlar**

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/orders/from-cart/{cartId}` | Sepetten sipariş üretir ve döndürür (`201`). |

Gövde isteğe bağlıdır ve kısmi güncelleme biçimindedir: kaynakta karşılığı olmayan alanları doldurur veya kaynaktan
gelenlerin yerine geçer.

```http
POST /orders/from-cart/0199a3f2-0a00-7c10-8a10-0a1b2c3d4e50
Content-Type: application/json

{ "customerName": "Ada Yılmaz" }
```

```http
HTTP/1.1 201 Created
Location: /orders/0199a3f2-1b00-7d20-9b20-1b2c3d4e5f60

{
  "id": "0199a3f2-1b00-7d20-9b20-1b2c3d4e5f60",
  "customerName": "Ada Yılmaz",
  "note": "Kapıya bırakın",
  "cartId": "0199a3f2-0a00-7c10-8a10-0a1b2c3d4e50",
  "orderItems": [
    {
      "id": "0199a3f2-1b01-7d21-9b21-1b2c3d4e5f61",
      "productName": "Klavye",
      "quantity": 2,
      "orderId": "0199a3f2-1b00-7d20-9b20-1b2c3d4e5f60"
    }
  ]
}
```

**Kayıt nasıl kurulur**

1. Kaynak kendi sorgu filtrelerinden geçerek, satırlarıyla okunur. Görünmeyen bir kayıttan kayıt üretilemez: kaynak
   yoksa veya isteği yapana görünmüyorsa `404` döner.
2. Yeni kayıt, yeni bir kayıt gibi kurulur (kimlik, başlangıç durumu, zaman damgaları) ve kaynağa işaret eden alan
   yazılır.
3. Hedefte yazılabilir olan ve kaynakta aynı ad ve tiple bulunan alanlar kopyalanır. İki kaynakta da `Metadata` varsa o
   da kopyalanır.
4. Entity `ICreatedFrom<TSource>` uygularsa `CreateFrom(source)` çağrılır. Satırlar sözleşmeye kendi sıralarıyla
   verilir; sözleşmenin eklediği satırlara kimlik ve sıra verilir.
5. İsteğin gönderdiği alanlar yazılır.
6. Oluşturma ucunun kontrolleri çalışır (bkz. [Ortak kurallar](#ortak-kurallar)), `Compute()` çağrılır ve kayıt
   kaydedilir.

**Kurallar ve hata yanıtları**

- Kaynağa işaret eden alan (`cartId`) yanıtta görünür ama hiçbir istekle yazılamaz; yalnızca üretme yolu doldurur.
  `POST /orders` veya `PATCH /orders/{id}` gövdesinde gönderilse de yok sayılır.
- Aynı kaynaktan birden çok kayıt üretilebilir; tekilliği zorlayan bir seçenek yoktur.
- Kaynak silindiğinde ne olacağını `onDelete` belirler. Varsayılan `Restrict`'tir: kendisinden kayıt üretilmiş bir
  sepet silinemez (`409`). Yukarıdaki `SetNull` sepetin silinmesine izin verir; siparişin `cartId` alanı boşalır.
- Üretilen kaynak (burada `Order`) `Idempotent` ise üretme ucu da `Idempotency-Key` başlığına uyar;
  `ConcurrencyChecked` ise yanıt `ETag` taşır.

**Seçenekler**

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `on` | `null` | Verilirse kayıt, kaynağın bu adlı geçişi veya aksiyonuyla doğar; bkz. [Geçişle doğan kayıt](#geçişle-doğan-kayıt). |
| `onDelete` | `OnDelete.Restrict` | Kaynak silindiğinde bu kayda ne olacağı. |

### Geçişle doğan kayıt

`on:` verilirse kayıt, kaynağın o adlı geçişi veya entity aksiyonu çalıştığında ve onunla aynı kaydetmede kendiliğinden
üretilir: ödeme tahsil edilince makbuz doğar. Bu durumda açık bir `from-…` ucu üretilmez; aksi halde geçişin koşullarını
atlayarak aynı kaydı üretmenin bir yolu olurdu.

<!-- example: created-on -->
```csharp
var payments = app.MapCrud<Payment>()
    .MultiTenant()
    .Workflow<PaymentStatus>(initial: PaymentStatus.RequiresCapture);

payments.MapTransition("capture", from: PaymentStatus.RequiresCapture, to: PaymentStatus.Captured);
payments.MapTransition("void", from: PaymentStatus.RequiresCapture, to: PaymentStatus.Voided);

// Makbuzu istemci oluşturamaz; yalnızca tahsilat geçişi doğurur.
app.MapAppendOnly<Receipt>()
    .MultiTenant()
    .Without(CrudOperation.Create)
    .CreatedFrom<Payment>(on: "capture");

public enum PaymentStatus
{
    RequiresCapture,
    Captured,
    Voided,
}

public partial class Payment
{
    [Range(0.01, 1_000_000.0)]
    public decimal Amount { get; set; }
}

public partial class Receipt
{
    // Ödemede de aynı adla bulunduğu için kopyalanır.
    public decimal Amount { get; set; }
}
```

```http
POST /payments/0199a3f3-0a00-7c10-8a10-0a1b2c3d4e50/capture
```

Geçiş başarılı olursa `GET /receipts` listesinde, `"amount"` alanı ödemeden kopyalanmış ve `"paymentId"` alanı ödemeyi
gösteren yeni bir makbuz bulunur. `void` geçişi makbuz üretmez. `POST /receipts` uçu yoktur (`405`).

- `on:` bir metin sabiti olmalıdır (`HALDUS010`) ve kaynağın bir geçişini veya aksiyonunu adlandırmalıdır; yoksa
  `HALDUS007` verilir.
- Geçiş reddedilirse (durum uymuyorsa, iş mantığı reddederse) kayıt da üretilmez.
- `on:` verildiğinde hedefin `Create` işlemi kaldırılabilir; verilmediyse kaldırılamaz (`HALDUS007`), çünkü `from-…` ucu
  bir oluşturmadır.
- Geçişle doğan kayıt bir ebeveynin altında olamaz, oluştururken numara (`Numbered`, `assignOn` olmadan) veya sıra
  (`Orderable`) alamaz ve kendisinden başka bir kaynak türetilemez. Bunlar gerekiyorsa `on:` vermeden `from-…` ucunu
  kullanın.

## Türetilmiş kaynak

`.DerivedFrom<TSource>()` salt-okunur bir kaynağın başka kayıtlardan hesaplandığını bildirir: stok seviyesi
hareketlerden, hesap bakiyesi defter kayıtlarından. Her kaynak kayıt oluşturulduğunda, aynı transaction'da, sizin
yazdığınız `IDerivation<TSource, TTarget>` sınıfı çağrılır.

<!-- example: derived -->
```csharp
app.MapAppendOnly<StockMovement>().Reversible();
app.MapReadOnly<StockLevel>().DerivedFrom<StockMovement>();

public partial class StockMovement : IReversal<StockMovement>
{
    public Guid ProductId { get; set; }

    [Range(-1_000_000, 1_000_000)]
    public int Quantity { get; set; }

    public void Reverse(StockMovement original) => Quantity = -original.Quantity;
}

public partial class StockLevel
{
    public Guid ProductId { get; set; }

    public int Quantity { get; set; }
}

// Haldus bu sınıfı bulur ve kaydeder; her hareket oluşturulurken, hareketle aynı transaction'da çağırır.
public sealed class StockLevelProjection : IDerivation<StockMovement, StockLevel>
{
    public async Task ApplyAsync(StockMovement source, IDerivedSet<StockLevel> targets, CancellationToken cancellationToken)
    {
        var level = await targets.Records.FirstOrDefaultAsync(item => item.ProductId == source.ProductId, cancellationToken);

        if (level is null)
        {
            level = new StockLevel { ProductId = source.ProductId };
            targets.Add(level);
        }

        level.Quantity += source.Quantity;
    }
}
```

<!-- fragment -->
```csharp
public interface IDerivation<in TSource, TTarget>
    where TTarget : class
{
    Task ApplyAsync(TSource source, IDerivedSet<TTarget> targets, CancellationToken cancellationToken);
}

public interface IDerivedSet<TTarget>
    where TTarget : class
{
    IQueryable<TTarget> Records { get; }   // izlenen kayıtlar: değiştirdiğiniz kayıt kaydedilir

    void Add(TTarget record);

    void Remove(TTarget record);
}
```

`source`, oluşturulmakta olan ve henüz kaydedilmemiş kaynak kayıttır. `targets.Records` üzerinden okuduğunuz kayıtlarda
yaptığınız değişiklikler, `Add` ve `Remove` ile verdikleriniz kaynakla birlikte kaydedilir. `SaveChangesAsync`
çağırmayın.

Hedefin uçları `MapReadOnly`'nin uçlarıdır: `GET /stock-levels` ve `GET /stock-levels/{id}`. İstemci seviyeyi yazamaz.

**Kurallar**

- İzdüşüm, kaynağın her oluşturma yolunda çağrılır: tekil oluşturma, toplu oluşturma, ters kayıt, başka bir kayıttan
  üretme. Yukarıda bir hareketin ters kaydı da seviyeyi günceller.
- Kaynak kayıt reddedilirse (doğrulama, kural) izdüşüm çalışmaz veya yazdıkları geri alınır; seviye değişmez.
- İzdüşümler bir kilidin altında, sırayla çalışır (kaynak tipi başına tek kilit). Okuyup değiştiren iki izdüşüm aynı anda
  çalışsaydı biri ötekinin yazdığını ezerdi; kilit sayesinde eşzamanlı hareketlerin hiçbiri kaybolmaz.
- Kaynak `MapAppendOnly`, hedef `MapReadOnly` ile eşlenmiş olmalıdır; değilse `HALDUS007` verilir. Kaynak eşlenmemişse
  `HALDUS009`. İkisi aynı veritabanı bağlamında durmalıdır.
- İzdüşüm sınıfı handler'lar gibi bulunur ve DI'a kaydedilir; kurucusundan servis alabilir. Sınıf yoksa hata uygulama
  açılırken değil, kaynağa yapılan ilk yazmada verilir.

## Ortak kurallar

Ters kayıt, yerine geçen kayıt ve başka bir kayıttan üretilen kayıt, alanlarını istekten değil başka bir kayıttan alır.
Hepsi oluşturma ucunun yaptığı her kontrolden geçer ve aynı sırayı izler:

1. kopya (aynı tipteki karşı kayıtta yazılabilir alanlar ve gömülü satırlar; farklı tipte aynı adlı alanlar),
2. entity'nin sözleşmesi (`IReversal<T>`, `ISupersession<T>`, `ICreatedFrom<TSource>`),
3. isteğin gönderdiği alanlar,
4. kontroller ve `Compute()`.

Geri kalanı yeni bir kayıt gibi atanır: yeni kimlik, başlangıç durumu, numara (kendi kuralıyla), zaman damgaları,
kiracı ve sahip.

- **Referanslar kayıttan okunarak doğrulanır.** Kopyanın kaynaktan devraldığı referanslar da dahil. Kaynağı görebilmek,
  onun referans verdiği kaydı görebilmek demek değildir; isteği yapanın göremediği bir kayda referansla kayıt doğmaz
  (`400`, alan düzeyinde).
- **Doğal anahtar** yeni kaydın değeri üzerinden denetlenir; çakışırsa `409` döner.
- Ters çevrilen veya yerine geçilen kaynak `ConcurrencyChecked` ise bu uçlar `If-Match` ister ve asıl kayıt o sürümde
  olmalıdır (eksikse `428`, uyuşmazsa `412`). Asıl kayıt da değişir (karşı kaydına işaret eder); bu yüzden onu aynı
  anda değiştiren başka bir istekle çakışma yakalanır.
- Kaynak `Idempotent` ise bu uçlar da `Idempotency-Key` başlığına uyar.
- Aynı tipteki karşı kaydın (ters, yerine geçen) anahtarı kaydetmeden önce bilinmelidir. Veritabanının ürettiği `int` veya
  `long` anahtarla birleşmez (`HALDUS007`); varsayılan `Guid` ve öteki anahtar biçimleri olur.
- `reverse` ve `supersede` adresleri ayrılmıştır; aynı adla bir geçiş veya aksiyon `HALDUS007` verir.

## Birlikte kullanım

| Bildirim | Archetype | Birleşmez (`HALDUS007`) |
|---|---|---|
| `Reversible(...)` | `MapCrud`, `MapAppendOnly` | `MapReadOnly`, `MapStore` ve ötekiler; `Create` işlemi kaldırılmış kaynak; veritabanının ürettiği anahtar; `Publishable`; `SecretOnce`, `TokenAccess`; `KeyStrategy.Client`. |
| `Supersedes(...)` | `MapCrud` | `MapAppendOnly` ve ötekiler; aynı kısıtlar. |
| `CreatedFrom<T>()` | `MapCrud`, `MapAppendOnly` | `MapStore` ve ötekiler; aynı kaynağa `References` veya `Under`; eşlenmemiş, `MapSingleton` veya detay kaynak; `SecretOnce`, `TokenAccess`; `KeyStrategy.Client`. |
| `DerivedFrom<T>()` | Hedef `MapReadOnly`, kaynak `MapAppendOnly` | `MapCrud` hedef; `MapAppendOnly` olmayan kaynak. |

`.Document()` belgelerinin düzeltmesi (`POST /{id}/amend`, `IAmendment<T>`) aynı zincir kurallarını kullanır; bkz.
[Onay ve belge](approvals.md#belge). Kopyalama (`Cloneable`) bir zincir değildir: kopya kaynağına işaret etmez ve
kaynağı kapatmaz; bkz. [Kayıt işlemleri](record-operations.md#kopyalama).

## Sınırlar

- Ters kayıt zincirin sonudur; ters kaydın tersi yoktur. Ters çevirme geri alınamaz.
- Bir kaydın yalnızca bir ters kaydı ve yalnızca bir yerine geçeni olur.
- Başka bir kayıttan üretmede kaynak aynı projede eşlenmiş olmalıdır. Aynı kaynaktan üretilebilecek kayıt sayısı
  sınırlanamaz.
- Geçişle doğan kayıt bir ebeveynin altında olamaz, oluştururken numara veya sıra alamaz.
- Türetmede baştan yeniden kurma (bütün kaynakları yeniden oynatma) yoktur. İzdüşüm yalnızca kaynak kayıt
  oluşturulurken çağrılır; kaynak değiştirilemediği ve silinemediği için başka bir olay yoktur.
- Türetme kaynak tipi başına tek bir kilit kullanır; yazma hacmi yüksek kaynaklarda bu bir darboğazdır. Kilit üst kayıt
  başına olan bir sayım yeterliyse [toplanan alanları](rollups.md) kullanın.

## Ayrıca bakın

- [Durum makineleri](state-machines.md): geçişler ve `EditableIn`.
- [Detaylar ve hesaplanan alanlar](details-and-computed-fields.md): üretilen kayda satır eklemek.
- [Toplanan alanlar](rollups.md): bağlı kayıtların sayısı ve toplamı.
- [Kayıt işlemleri](record-operations.md#kopyalama)
- [Onay ve belge](approvals.md#belge)
- [Bildirim dizini](../reference/declarations.md)
