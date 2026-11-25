# Durum makineleri

Bir siparişin taslak, verilmiş, gönderilmiş ve iptal edilmiş halleri vardır; her halde başka şeylere izin verilir.
Haldus bu kuralları bildirimden üretir: kaydın `Status` alanını, durumu değiştiren uçları (geçişleri), yanlış durumdaki
isteğin reddini ve aynı anda gelen iki geçişin çakışmasını. Siz yalnızca durumları, kimin hangi durumdan hangisine
geçtiğini ve varsa geçişin iş mantığını yazarsınız.

Bu sayfadaki örneklerde web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs` içindedir;
`builder` ve `app` satırları kısalık için yazılmamıştır. Hata yanıtları problem details biçimindedir
(`application/problem+json`); örneklerde yalnızca `title`, `status` ve `detail` gösterilir.

## İçindekiler

- [Durum makinesi](#durum-makinesi)
- [Düzenleme ve silme kuralları](#düzenleme-ve-silme-kuralları)
- [Geçişler](#geçişler)
- [Eşzamanlı geçişler](#eşzamanlı-geçişler)
- [Numara ve durum](#numara-ve-durum)
- [Ek durum eksenleri](#ek-durum-eksenleri)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Durum makinesi

`.Workflow<TStatus>()` kayda bir durum makinesi ekler. Durumlar sizin yazdığınız bir `enum`'dur. Haldus entity'ye
`Status` alanını ekler; bu alan istek gövdesinden yazılamaz ve yalnızca geçişlerle değişir.

<!-- example: orders -->
```csharp
var orders = app.MapCrud<Order>()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)
    .DeletableIn(OrderStatus.Draft, OrderStatus.Cancelled);

orders.MapTransition("place", from: OrderStatus.Draft, to: OrderStatus.Placed);
orders.MapTransition("ship", from: OrderStatus.Placed, to: OrderStatus.Shipped);
orders.MapTransition<CancelOrder>(from: [OrderStatus.Draft, OrderStatus.Placed], to: OrderStatus.Cancelled);
```

Durumlar, entity ve geçişin istek tipi:

<!-- example: orders -->
```csharp
public enum OrderStatus
{
    Draft,
    Placed,
    Shipped,
    Cancelled,
}

public partial class Order
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";

    // Herkese açık setter'ı olmayan alan yalnızca yanıtta görünür; onu geçiş yazar.
    public string? CancellationReason { get; private set; }

    public void Cancel(string reason) => CancellationReason = reason;
}

/// <summary>POST /orders/{id}/cancel. İstek, kayıt üzerindeki etkisini kendisi uygular.</summary>
public sealed record CancelOrder : ITransition<Order>
{
    [Required, MaxLength(500)]
    public string Reason { get; init; } = "";

    public void Apply(Order order) => order.Cancel(Reason);
}
```

`Status` alanını siz yazmazsınız; yazarsanız (aynı enum tipinde) Haldus sizinkini kullanır ve yine isteklere koymaz.

| Üretilen | Ne yapar |
|---|---|
| `Status` alanı | Yanıtta adıyla görünür (`"status": "Draft"`), veritabanında da adıyla saklanır. İsteklerde yer almaz. |
| `GET /orders?status=Placed` | Liste duruma göre süzülür. Kendi filtre sınıfınız (`Filterable`) `Status` adlı bir alan taşıyorsa bu parametre üretilmez; süzme sizin filtrenizdedir. |
| `POST /orders/{id}/{geçiş}` | Her `MapTransition` için bir uç; bkz. [Geçişler](#geçişler). |

Yeni kayıt her zaman başlangıç durumunda doğar. Gövdede bir `status` gönderilse de yok sayılır:

```http
POST /orders
Content-Type: application/json

{ "customerName": "Ada Lovelace", "status": "Shipped" }
```

```http
HTTP/1.1 201 Created
Location: /orders/0196f3c2-7d4e-7a51-9a3b-2f6c1d8e4a10
```

```json
{
  "id": "0196f3c2-7d4e-7a51-9a3b-2f6c1d8e4a10",
  "customerName": "Ada Lovelace",
  "cancellationReason": null,
  "status": "Draft"
}
```

### Seçenekler

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `initial` | Enum'un `0` değerli üyesi | Yeni kaydın durumu. Enum üyesi olarak yazılmalıdır; değişkenden okunamaz (`HALDUS010`). Enum'da `0` değerli üye yoksa ve `initial` verilmediyse `HALDUS010` olur. |

Durum tipi bir `enum` olmalıdır (`Workflow<int>()` `HALDUS010` verir). Bir kaynağın en fazla bir durum makinesi
vardır; birbirinden bağımsız ilerleyen başka durumlar [ek durum eksenleri](#ek-durum-eksenleri) olarak bildirilir.

`Workflow`, `MapCrud`, `MapReadOnly` ve `MapAppendOnly` ile kullanılır. `MapReadOnly` kaynakta geçişler kaydı
değiştiren tek yoldur: kayıtları uygulamanızın kodu oluşturur, istemci yalnızca okur ve geçiş ister.

## Düzenleme ve silme kuralları

`.EditableIn(...)` güncellemeye (`PUT`, `PATCH`), `.DeletableIn(...)` silmeye yalnızca sayılan durumlarda izin verir.
İkisi de isteğe bağlıdır; yazılmazsa o işlem her durumda serbesttir.

| Bildirim | Etkilediği uçlar | Durum uymazsa |
|---|---|---|
| `EditableIn(...)` | `PUT /orders/{id}`, `PATCH /orders/{id}` | 409 |
| `DeletableIn(...)` | `DELETE /orders/{id}` | 409 |

Verilmiş bir siparişi değiştirmeye çalışmak:

```http
PATCH /orders/0196f3c2-7d4e-7a51-9a3b-2f6c1d8e4a10
Content-Type: application/json

{ "customerName": "Grace Hopper" }
```

```http
HTTP/1.1 409 Conflict
Content-Type: application/problem+json
```

```json
{
  "title": "Not allowed in the current state",
  "status": 409,
  "detail": "'update' is not allowed while the record is in the 'Placed' state."
}
```

Silme reddedildiğinde `detail` içinde işlem adı `delete`'tir. Mesajlar isteğin diline göre (`Accept-Language`)
İngilizce veya Türkçe döner.

Kurallar:

- Geçişler `EditableIn` kuralına bağlı değildir; her geçiş kendi `from:` durumlarına bakar.
- Sahibin parçası olan kayıtlar da sahibin kuralına uyar: ayrı uçlu detaylar (`MapDetail<T>().AsSubresource()`) ve
  komutla yazılan satırlar (`AsCommands()`) yalnızca sahip düzenlenebilir bir durumdayken oluşturulur, değiştirilir ve
  silinir; değilse 409 döner. Okumaları her durumda serbesttir. Ayrıntı:
  [Detaylar ve hesaplanan alanlar](details-and-computed-fields.md).
- `EditableIn` ve `DeletableIn` bir durum makinesi ister; `Workflow` (veya `Document`) olmadan yazılırsa `HALDUS007`
  olur. Sayılan durum durum tipinin üyesi değilse `HALDUS010` olur.
- Düzenlenemeyen bir kaydı denetimli biçimde değiştirmek gerekiyorsa (verilmiş bir siparişin düzeltilmesi) bkz.
  [değişiklik kümeleri](../reference/declarations.md#kayıt-işlemleri).

## Geçişler

Bir geçiş, kaydı bir durumdan ötekine taşıyan `POST` ucudur: `POST /orders/{id}/ship`. Geçişler `Map*` çağrısının
döndürdüğü builder'dan bildirilir; bu yüzden builder bir değişkende tutulur (`var orders = app.MapCrud<Order>()...`).

`MapTransition` üç biçimde yazılır:

| Biçim | Adres | Gövde | İş mantığı |
|---|---|---|---|
| `orders.MapTransition("ship", from, to)` | `POST /orders/{id}/ship` | Yok | Yok; yalnızca durum değişir. |
| `orders.MapTransition("ship", from, to, order => ...)` | `POST /orders/{id}/ship` | Yok | Lambda. |
| `orders.MapTransition<CancelOrder>(from, to)` | `POST /orders/{id}/cancel` | `CancelOrder` | İstek tipi, handler sınıfı veya lambda. |

İş mantığının üç yolu ve hangisinin önce çalıştığı [İş mantığı](business-logic.md) sayfasındadır.

### Kaynak durumlar

`from:` tek bir durum ya da birkaç durumdan oluşan bir koleksiyon ifadesidir:

<!-- fragment -->
```csharp
orders.MapTransition<CancelOrder>(from: [OrderStatus.Draft, OrderStatus.Placed], to: OrderStatus.Cancelled);
```

Durumlar enum üyesi olarak yazılmalıdır; derleme sırasında okunurlar ve değişkenden gelemezler (`HALDUS010`).
Kaynağın durum tipine ait olmayan bir durum da `HALDUS010` verir.

### Adres

Gövdesiz geçişin adresi verdiğiniz addır. Ad küçük harfle başlar ve yalnızca küçük harf, rakam ve tire içerir
(`mark-paid`); değilse `HALDUS010` olur. Ad bir sabit olmalıdır.

İstek tipli geçişte adres, verilmediyse istek tipinin adından türetilir:

1. Sondaki `Request` veya `Command` atılır.
2. Ad entity'nin adıyla bitiyorsa o atılır; bitmiyor ama onunla başlıyorsa baştaki atılır.
3. Kalan ad kebab-case yazılır.

| İstek tipi | Entity | Adres |
|---|---|---|
| `CancelOrder` | `Order` | `/orders/{id}/cancel` |
| `PlaceOrderCommand` | `Order` | `/orders/{id}/place` |
| `OrderApprovalRequest` | `Order` | `/orders/{id}/approval` |
| `BillTicket` | `Ticket` | `/tickets/{id}/bill` |

Adı kendiniz vermek için `name:` kullanılır:

<!-- fragment -->
```csharp
orders.MapTransition<CancelOrder>(from: OrderStatus.Placed, to: OrderStatus.Cancelled, name: "void");   // POST /orders/{id}/void
```

Aynı kaynakta iki geçiş (veya bir geçiş ile bir [entity aksiyonu](business-logic.md#entity-aksiyonları)) aynı adı
taşıyamaz; bir trait'in ürettiği uçla (`archive`, `restore`, `approve`, `amend`...) çakışan ad da kullanılamaz. İkisi de
`HALDUS007`'dir.

### İstek ve yanıt

Gövdesiz geçiş:

```http
POST /orders/0196f3c2-7d4e-7a51-9a3b-2f6c1d8e4a10/ship
```

İstek tipli geçiş; gövde istek tipinin alanlarını camelCase taşır ve istek tipindeki DataAnnotations ile doğrulanır:

```http
POST /orders/0196f3c2-7d4e-7a51-9a3b-2f6c1d8e4a10/cancel
Content-Type: application/json

{ "reason": "Changed my mind" }
```

Başarılı geçiş kaydın güncel halini döndürür:

```http
HTTP/1.1 200 OK
```

```json
{
  "id": "0196f3c2-7d4e-7a51-9a3b-2f6c1d8e4a10",
  "customerName": "Ada Lovelace",
  "cancellationReason": "Changed my mind",
  "status": "Cancelled"
}
```

İstek tipinin hiç alanı yoksa da (`public sealed record ReopenOrder;`) bir JSON gövdesi gönderin: `{}`.

### Kurallar ve hata yanıtları

Haldus geçişi şu sırayla işler: kaydı bulur, durumunu denetler, iş mantığını çalıştırır, durumu yazar, hesaplanan
alanları günceller, gerekiyorsa numara verir ve kaydeder.

| Durum | Yanıt |
|---|---|
| Kayıt yok veya isteği yapana görünmüyor (başka kiracı, başka sahip) | 404 |
| Gövde doğrulanamadı | 400, alan düzeyinde hatalarla |
| Kayıt `from:` durumlarından birinde değil | 409 |
| Kaynak `ConcurrencyChecked` ve `If-Match` yok | 428 |
| Kaynak `ConcurrencyChecked` ve `If-Match` eski | 412 |
| İş mantığı `TransitionRejectedException` fırlattı | 422; hiçbir şey kaydedilmez |
| Kayıt bu sırada başka bir istekle değişti | 409 (sürüm kontrollü kaynakta 412); bkz. [Eşzamanlı geçişler](#eşzamanlı-geçişler) |

Yanlış durumdaki kayıt:

```http
HTTP/1.1 409 Conflict
Content-Type: application/problem+json
```

```json
{
  "title": "Not allowed in the current state",
  "status": 409,
  "detail": "'ship' is not allowed while the record is in the 'Draft' state."
}
```

Durum kontrolünü iş mantığına yazmayın: `from:` bunu zaten yapar ve yazmayla birlikte yineler. `Status` alanına
mantığın içinden değer atamayın; geçişin hedef durumunu Haldus yazar.

`MapTransition` bir durum makinesi ister: kaynakta ne `Workflow` (ya da `Document`) ne de bir eksen varsa
`HALDUS007` olur. Geçiş builder'dan çağrılır; `Workflow`, `EditableIn` ve `DeletableIn` ise zincirdedir, bir
değişken üzerinden sonradan çağrılırlarsa `HALDUS004` olur.

`MapTransition` bir `RouteHandlerBuilder` döndürür; yalnızca o geçişe kural eklemek için kullanılır
(`.RequireAuthorization("managers")`). Kaynağın builder'ına eklenen kurallar geçişlere de uygulanır.

## Eşzamanlı geçişler

Durum kontrolü ile kaydetme arasında başka bir istek durumu değiştirebilir. Haldus bu yüzden durumu yazmanın koşulu
yapar: kayıt, okunduğu andaki durumundan başka bir duruma geçmişse yazma gerçekleşmez.

- Sürüm kontrolü olmayan kaynakta çakışan istek 409 alır:

  ```json
  {
    "title": "Not allowed in the current state",
    "status": 409,
    "detail": "The state of the record changed while this request was being processed. Reload it and try again."
  }
  ```

- `ConcurrencyChecked` kaynakta aynı işi satır sürümü görür; çakışan istek 412 alır.

Bir rezervasyona aynı anda gelen `confirm` ve `release` isteklerinden yalnızca biri geçer; ötekiler 409 alır ve kayıt
kazanan geçişin durumunda kalır. Entegrasyon testleri bunu sekiz eşzamanlı istekle doğrular. Aynı koruma `PUT`,
`PATCH`, silme ve entity aksiyonları için de geçerlidir: durumu okuyup ona göre karar veren her yazma, durum
değişmişse kaydetmez.

## Numara ve durum

`Numbered(assignOn:)` kayda okunabilir numarasını kayıt oluşturulurken değil, belirli bir duruma geçerken verir.
Taslaklar numara harcamaz; numara sipariş verilirken alınır.

<!-- example: numbered -->
```csharp
var orders = app.MapCrud<Order>()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)
    .Numbered("ORD-{yyyy}-{seq:6}", assignOn: OrderStatus.Placed);

orders.MapTransition("place", from: OrderStatus.Draft, to: OrderStatus.Placed);
orders.MapTransition("cancel", from: OrderStatus.Placed, to: OrderStatus.Cancelled);
orders.MapTransition("reopen", from: OrderStatus.Cancelled, to: OrderStatus.Draft);

public enum OrderStatus
{
    Draft,
    Placed,
    Cancelled,
}

public partial class Order
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";
}
```

```http
POST /orders/0196f3c2-7d4e-7a51-9a3b-2f6c1d8e4a10/place
```

```json
{
  "id": "0196f3c2-7d4e-7a51-9a3b-2f6c1d8e4a10",
  "customerName": "Ada Lovelace",
  "status": "Placed",
  "number": "ORD-2026-000001"
}
```

Kurallar:

- Numara, `assignOn` durumuna giren geçişte, kayıtla aynı transaction'da ayrılır. Taslak halindeki kaydın `number`
  alanı `null`'dır.
- Numara bir kez verilir. İptal edilip yeniden taslağa dönen ve yeniden verilen sipariş eski numarasını korur.
- İş mantığı geçişi reddederse (422) numara harcanmaz; boşluksuz sayaçta sıradaki sipariş aynı numarayı alır.
- `assignOn` yalnızca asıl duruma (`Workflow`) bakar; [ek eksenlerdeki](#ek-durum-eksenleri) geçişler numara ayırmaz.
- `assignOn` bir durum makinesi gerektirir.

Desenin yazımı, boşluksuz ve sequence modları, sayacın kapsamı: [Alanlar](fields.md).

## Ek durum eksenleri

Bir kaydın birbirinden bağımsız ilerleyen birkaç durumu olabilir: siparişin kendi durumu, ödemesi ve sevkiyatı gibi.
"En fazla bir durum makinesi" kuralı korunur: asıl durum `Workflow` ile bildirilir, ötekiler `.Axis<TStatus>()` ile.
Her eksen kendi enum tipini taşır ve alanı o tipin adını alır.

<!-- example: axes -->
```csharp
var orders = app.MapCrud<Order>()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)
    .Axis<PaymentState>(initial: PaymentState.Unpaid)
    .Axis<ShipmentState>(initial: ShipmentState.Pending);

// Geçişin hangi eksende olduğunu durumlarının tipi söyler.
orders.MapTransition("place", from: OrderStatus.Draft, to: OrderStatus.Placed);
orders.MapTransition("mark-paid", from: PaymentState.Unpaid, to: PaymentState.Paid);
orders.MapTransition("refund", from: PaymentState.Paid, to: PaymentState.Refunded);
orders.MapTransition("ship", from: ShipmentState.Pending, to: ShipmentState.Shipped);

public enum OrderStatus
{
    Draft,
    Placed,
    Closed,
}

public enum PaymentState
{
    Unpaid,
    Paid,
    Refunded,
}

public enum ShipmentState
{
    Pending,
    Shipped,
}

public partial class Order
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";
}
```

| Üretilen | Ne yapar |
|---|---|
| `PaymentState`, `ShipmentState` alanları | Yanıtta adıyla görünür (`"paymentState": "Unpaid"`), istekle yazılamaz, veritabanında adıyla saklanır. |
| `GET /orders?paymentState=Paid` | Liste her eksene göre ayrı ayrı süzülür; asıl durumla birleştirilebilir (`?paymentState=Paid&status=Placed`). |
| `POST /orders/{id}/mark-paid` | Yalnızca `PaymentState` eksenini denetler ve yazar. |

Yeni sipariş her eksende başlangıç durumunda doğar:

```json
{
  "id": "0196f3c2-7d4e-7a51-9a3b-2f6c1d8e4a10",
  "customerName": "Ada Lovelace",
  "paymentState": "Unpaid",
  "shipmentState": "Pending",
  "status": "Draft"
}
```

Kurallar:

- Bir geçiş tek bir eksene aittir; `from:` ve `to:` durumlarının tipi ekseni belirler. Durumları iki farklı tipten
  gelen geçiş `HALDUS010`'dur.
- Geçiş yalnızca kendi ekseninin durumunu denetler: ödenmemiş siparişte `refund` 409 alır, ama sipariş taslakta da
  olsa, kapalı da olsa ödeme ekseni ilerler.
- `EditableIn`, `DeletableIn` ve `Numbered(assignOn:)` yalnızca asıl duruma bakar. Kapalı bir sipariş düzenlenemez ama
  iadesi yapılabilir.
- Eksen de eşzamanlılığa karşı korunur: aynı geçişi birlikte isteyen iki istekten biri geçer, öteki 409 alır
  (sürüm kontrollü kaynakta 412). Bütün durum alanları her yazmanın koşulu olduğu için, farklı eksenlerde tam aynı
  anda yapılan iki geçişten biri de 409 alabilir; istemci yeniden dener.
- Asıl durum olmadan yalnızca eksen de bildirilebilir: `app.MapCrud<Invoice>().Axis<PaymentState>()`.
- `initial` verilmezse enum'un `0` değerli üyesi kullanılır; öyle bir üye yoksa `HALDUS010` olur.

Hatalı bildirimler:

| Bildirim | Tanı |
|---|---|
| `.Workflow<OrderStatus>().Axis<OrderStatus>()` | `HALDUS007`: eksen asıl durumu yineliyor. |
| `.Axis<PaymentState>().Axis<PaymentState>()` | `HALDUS010`: aynı tipte ikinci eksen. |
| Durumları iki tipten gelen geçiş | `HALDUS010` |
| `MapStore`, `MapSingleton`... üzerinde `Axis` | `HALDUS007` |

<!-- example: axis-mixed, error: HALDUS010 -->
```csharp
var orders = app.MapCrud<Order>()
    .Workflow<OrderStatus>()
    .Axis<PaymentState>();

// Asıl durumdan ödeme eksenine giden geçiş tek bir eksene ait değildir.
orders.MapTransition("mix", from: OrderStatus.Draft, to: PaymentState.Paid);

public enum OrderStatus
{
    Draft,
    Placed,
}

public enum PaymentState
{
    Unpaid,
    Paid,
}

public partial class Order
{
    public string CustomerName { get; set; } = "";
}
```

Başka kayıtlardan **türeyen** bir durum (ödemelerden hesaplanan "kısmen ödendi") eksen değildir: o, toplanan bir
alan ve entity'nin `Compute()` metoduyla yazılır; bkz. [Toplanan alanlar](rollups.md).

## Birlikte kullanım

| Bildirim | Durum makinesiyle ilişkisi |
|---|---|
| `Numbered(assignOn:)` | Numara belirli bir duruma girerken verilir; bkz. [Numara ve durum](#numara-ve-durum). |
| `ConcurrencyChecked()` | Geçişler de `If-Match` ister; çakışma 412 olur. |
| `Idempotent()` | Geçişler de `Idempotency-Key` ile tekrara karşı korunur. |
| `MapDetail<T>().AsSubresource()`, `AsCommands()` | Satırlar sahibin `EditableIn` kuralına uyar. |
| `Searchable(order => order.Status)` | Durum arama gövdesinde koşul olabilir. |
| `Approvable(levels, "place")` | Sayılan geçişler kayıt onaylanana kadar 409 alır; bkz. [Onay ve belge](approvals.md). |
| `Document()` | Hazır durum makinesi (`Draft`, `Submitted`, `Cancelled`); `Workflow`, `EditableIn` ve `DeletableIn` ile birlikte yazılamaz (`HALDUS007`). Bkz. [Belge](approvals.md#belge). |
| `ChangeSets(...)` | Düzenlenemeyen durumdaki kaydı bir değişiklik kümesiyle düzeltir; durumları açıkça sayılır. |
| `Reversible(...)`, `Supersedes(...)` | Durum makinesi olan kaynakta işlemin yapılabildiği durumlar sayılmalıdır; bkz. [Kayıtlar arası zincir](record-chains.md). |
| `CreatedFrom<T>(on: "capture")` | Bir geçiş başka bir kaynakta kayıt doğurabilir; bkz. [Kayıtlar arası zincir](record-chains.md). |
| `PublishesEvents()` | Her geçiş kendi olayını yazar (`order.ship`); bkz. [Olay yayını](events.md). |
| `Publishable()` | `Workflow` ve `Axis` ile birleşmez (`HALDUS007`). |
| `MapStore`, `MapSingleton`, `MapLookup` | Durum makinesi taşımaz (`HALDUS007`). |

Çoktan-çoğa bağların da durumu olabilir (`MapAssociation<Supplier, Product>().Workflow<SupplyStatus>()`); bkz.
[İlişkiler](relationships.md).

## Sınırlar

- Bir kaynağın tek bir asıl durumu vardır; ek durumlar eksen olarak bildirilir.
- Eksenlerin `EditableIn` / `DeletableIn` benzeri kuralı yoktur; düzenleme ve silme yalnızca asıl duruma bakar.
- Bir kaydın o anda hangi geçişleri yapabileceğini söyleyen bir alan veya uç (`supportedActions` gibi) üretilmez.
- Geçişlerin toplu biçimi yoktur: her geçiş tek bir kayıt üzerinde çalışır.
- Durum, kayıt oluşturulurken seçilemez; her kayıt başlangıç durumunda doğar.
- Bağlarda (`MapAssociation`) eksen yoktur; bağın geçişleri iş mantığı taşımaz.
- Ayrı uçlu bir detayın sahibinin durumunu denetlemesi sahibin satırını kilitlemez: sahip tam o anda durum
  değiştirirse detay yazması yine de geçebilir.

## Ayrıca bakın

- [İş mantığı](business-logic.md): geçişin ve aksiyonun mantığı, reddetme, handler sınıfları.
- [Onay ve belge](approvals.md): onaya bağlı geçişler ve hazır belge durum makinesi.
- [Alanlar](fields.md): numaraların deseni ve sayaçlar.
- [Yaşam döngüsü](lifecycle.md): eşzamanlılık denetimi ve tekrara karşı koruma.
- [Bildirim dizini](../reference/declarations.md#durum-ve-iş-mantığı)
- [Tanılar](../reference/diagnostics.md)
