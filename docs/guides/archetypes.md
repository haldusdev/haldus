# Archetype'lar

Her kaynak tam olarak bir archetype ile eşlenir. Archetype, kaynağın hangi uçlara sahip olduğunu ve anahtarını kimin
verdiğini seçer: `MapCrud` her şeyi yapan bir koleksiyondur, `MapAppendOnly` yazıldıktan sonra değişmeyen kayıtlar
içindir, `MapSingleton` tek bir ayar kaydıdır. Trait'ler, ilişkiler ve durum makinesi bu temelin üzerine zincirlenir.
Bu sayfa altı temel archetype'ı ayrıntısıyla, kendi rehberi olan archetype'ları kısaca anlatır.

Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. `builder` ve `app` değişkenlerini kuran satırlar da
yazılmış sayılır; örnekler yalnızca konuyla ilgili satırları gösterir.

## İçindekiler

- [Hangisini seçmeli](#hangisini-seçmeli)
- [Ortak kurallar](#ortak-kurallar)
- [`MapCrud`](#mapcrud)
- [`MapReadOnly`](#mapreadonly)
- [`MapAppendOnly`](#mapappendonly)
- [`MapStore`](#mapstore)
- [`MapLookup`](#maplookup)
- [`MapSingleton`](#mapsingleton)
- [Kendi rehberi olan archetype'lar](#kendi-rehberi-olan-archetypelar)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Hangisini seçmeli

| Kaynağınız... | Archetype | Anahtar |
|---|---|---|
| İstemcinin oluşturduğu, değiştirdiği ve sildiği sıradan bir koleksiyon (ürün, müşteri, sipariş) | [`MapCrud`](#mapcrud) | `Id`, sunucu üretir |
| İstemcinin yalnızca okuduğu, kayıtlarını sizin kodunuzun yazdığı bir koleksiyon (stok seviyesi, kur) | [`MapReadOnly`](#mapreadonly) | `Id`, sunucu üretir |
| Bir kez yazılıp hiç değişmeyen kayıtlar (hareket, yorum, ödeme işlemi) | [`MapAppendOnly`](#mapappendonly) | `Id`, sunucu üretir |
| Anahtarını istemcinin bildiği anahtar-değer kayıtları (özellik bayrağı, kullanıcı tercihi) | [`MapStore`](#mapstore) | `Key`, istemci verir |
| Kod ve addan oluşan küçük bir referans listesi (ülke, para birimi) | [`MapLookup`](#maplookup) | `Code`, istemci verir |
| Uygulama, kiracı veya ebeveyn başına tek bir kayıt (mağaza ayarları) | [`MapSingleton`](#mapsingleton) | Yok |
| Başka bir kaydın parçası olan satırlar (sipariş satırları) | [`MapDetail`](#mapdetail) | `Id` |
| İçeriği ayrıca yüklenen dosya | [`MapFile`](#mapfile) | `Id` |
| İsteği aşan, arka planda çalışan iş (rapor üretme) | [`MapJob`](#mapjob) | `Id` |
| Yalnızca komutlarla değişen nicelik (stok, izin bakiyesi) | [`MapBalance`](#mapbalance) | `Key`, istemci verir |
| İki kaynak arasındaki çoktan-çoğa bağ (ürün ve etiket) | [`MapAssociation`](#mapassociation) | İki ucun anahtarı |
| Bir kayda bağlı olmayan komut veya hesap | [`MapAction` ve `MapQuery`](#mapaction-ve-mapquery) | Yok |

Emin değilseniz `MapCrud` ile başlayın. İşlemlerden bazılarını istemiyorsanız archetype değiştirmek yerine
`.Without(...)` veya `.Only(...)` ile kaldırabilirsiniz; bkz.
[standart işlemleri seçmek](record-operations.md#standart-işlemleri-seçmek).

## Ortak kurallar

Bütün archetype'larda aynıdır:

- **Adres** entity adının kebab-case çoğuludur: `Product` → `/products`, `StockMovement` → `/stock-movements`.
  `MapSingleton` tekildir: `StoreSettings` → `/store-settings`, `MaintenanceNotice` → `/maintenance-notice`.
- **Entity** üst düzey bir `partial class`'tır. Siz yalnızca kendi alanlarınızı yazarsınız; anahtarı ve bildirimlerin
  eklediği alanları Haldus üretir. Hiç sınıf yazmazsanız Haldus sınıfı `Map*` çağrısının bulunduğu namespace'e üretir.
- **İstek alanları** herkese açık setter'ı olan skaler alanlardır. Setter'ı olmayan (`{ get; private set; }`) alanlar
  ve Haldus'un ürettiği alanlar yalnızca yanıtta yer alır. Entity'deki DataAnnotations istek DTO'larına kopyalanır.
- **JSON** alan adları camelCase'dir: `Price` → `price`.
- **Üretilmeyen bir işlem** yönlendirmede eşleşmez: örneğin `MapAppendOnly` kaynağına gönderilen `PUT` isteği 405
  alır.

Durum kodlarının, hata gövdesinin ve başlıkların tamamı [HTTP sözleşmesi](../reference/http.md) sayfasındadır.

## `MapCrud`

Tam bir koleksiyon: liste, tekil okuma, oluşturma, tam değiştirme (`PUT`), kısmi güncelleme (`PATCH`) ve silme.
İstemcinin kayıtları oluşturup değiştirdiği her kaynak için varsayılan seçimdir.

<!-- example: crud -->
```csharp
app.MapCrud<Product>();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [Range(0, 1_000_000)]
    public decimal Price { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }
}
```

| Yöntem | Adres | Ne yapar | Başarı |
|---|---|---|---|
| `GET` | `/products` | Sayfalanmış liste (`?page=1&pageSize=20`) | 200 |
| `GET` | `/products/{id}` | Tek kayıt | 200 |
| `POST` | `/products` | Oluşturur | 201 ve `Location` |
| `PUT` | `/products/{id}` | Yazılabilir alanların hepsini değiştirir | 200 |
| `PATCH` | `/products/{id}` | Yalnızca gönderilen alanları değiştirir | 200 |
| `DELETE` | `/products/{id}` | Siler | 204 |

**Anahtar:** `Id`. Yazmazsanız `Guid` (v7) olarak üretilir ve sunucu atar. Kendiniz `Guid`, `int` veya `long` tipinde
bir `Id` yazabilirsiniz; `int` ve `long` anahtarı veritabanı üretir. Adresteki anahtar tipine göre kısıtlanır
(`{id:guid}`, `{id:int}`, `{id:long}`); tipe uymayan bir adres hiçbir uçla eşleşmez. Anahtarın biçimini (önekli kimlik,
ULID, istemcinin verdiği kimlik) değiştirmek için bkz. [anahtar biçimi](fields.md#anahtar-biçimi).

Oluşturma:

```http
POST /products
Content-Type: application/json

{ "name": "Keyboard", "price": 1250.50 }
```

```http
HTTP/1.1 201 Created
Location: /products/01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11
Content-Type: application/json; charset=utf-8

{ "id": "01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11", "name": "Keyboard", "price": 1250.50, "description": null }
```

Liste:

```http
GET /products?page=1&pageSize=2
```

```json
{
  "items": [
    { "id": "01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11", "name": "Keyboard", "price": 1250.50, "description": null },
    { "id": "01937c6e-7d21-7b44-8e0a-6f1d2c3b4a55", "name": "Mouse", "price": 300, "description": null }
  ],
  "page": 1,
  "pageSize": 2,
  "totalCount": 14
}
```

Kısmi güncelleme yalnızca gönderilen alanlara dokunur:

```http
PATCH /products/01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11
Content-Type: application/json

{ "description": "Mechanical, TR layout" }
```

Kurallar ve hata yanıtları:

| Durum | Yanıt |
|---|---|
| Alan kuralına uymayan istek (uzunluk, aralık, zorunlu alan) | 400, `errors` içinde alan adıyla |
| Kayıt yok veya isteği yapan göremiyor | 404 |
| Başka kayıtların referans verdiği kaydı silmek | 409 |
| `PATCH`'te, boş bırakılamayan bir alana `null` göndermek | 400 |

`PUT` yazılabilir alanların hepsini ister; `PATCH`'te gönderilmeyen alan değişmez, `null` gönderilen boş bırakılabilir
alan boşaltılır. Ayrıntısı: [HTTP sözleşmesi](../reference/http.md).

## `MapReadOnly`

İstemcinin yalnızca okuduğu koleksiyon: liste ve tekil okuma. Kayıtları sizin kodunuz, bir arka plan işi veya bir
türetme ([`DerivedFrom`](record-chains.md#türetilmiş-kaynak)) yazar. Stok seviyesi, döviz kuru veya bir kullanıcıya
gelen bildirimler gibi, istemcinin göstermekle yetindiği veriler içindir.

<!-- example: readonly -->
```csharp
app.MapReadOnly<ExchangeRate>();

public partial class ExchangeRate
{
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "";

    public decimal Rate { get; set; }
}
```

| Yöntem | Adres | Ne yapar | Başarı |
|---|---|---|---|
| `GET` | `/exchange-rates` | Sayfalanmış liste | 200 |
| `GET` | `/exchange-rates/{id}` | Tek kayıt | 200 |

**Anahtar:** `MapCrud` ile aynı (`Id`; `Guid`, `int` veya `long`).

Kayıtları kendi kodunuzda bağlam üzerinden yazarsınız; Haldus'un trait alanlarını dolduran interceptor'ı bu yazmada da
çalışır:

<!-- example: readonly-write -->
```csharp
app.MapReadOnly<ExchangeRate>();

app.MapPost("/admin/rates/refresh", async (AppDbContext db, CancellationToken cancellationToken) =>
{
    db.Set<ExchangeRate>().Add(new ExchangeRate { Currency = "USD", Rate = 34.12m });
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});

public partial class ExchangeRate
{
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "";

    public decimal Rate { get; set; }
}
```

```http
GET /exchange-rates/01937d02-1c44-7e1a-b5a3-0e9d7c6f2b18
```

```json
{ "id": "01937d02-1c44-7e1a-b5a3-0e9d7c6f2b18", "currency": "USD", "rate": 34.12 }
```

`POST`, `PUT`, `PATCH` ve `DELETE` istekleri 405 alır.

## `MapAppendOnly`

Bir kez yazılan, sonra değişmeyen ve silinmeyen kayıtlar: liste, tekil okuma ve oluşturma. Stok hareketi, ödeme
işlemi, yorum veya denetim kaydı gibi geçmişi tutan veriler içindir. Yanlış bir kaydı düzeltmek gerekiyorsa onu
nötrleyen bir ters kayıt yazılır; bkz. [ters kayıt](record-chains.md#ters-kayıt).

<!-- example: appendonly -->
```csharp
app.MapAppendOnly<StockMovement>()
    .Auditable();

public partial class StockMovement
{
    public Guid ProductId { get; set; }

    [Range(-1_000_000, 1_000_000)]
    public int Quantity { get; set; }

    [MaxLength(200)]
    public string? Reason { get; set; }
}
```

| Yöntem | Adres | Ne yapar | Başarı |
|---|---|---|---|
| `GET` | `/stock-movements` | Sayfalanmış liste | 200 |
| `GET` | `/stock-movements/{id}` | Tek kayıt | 200 |
| `POST` | `/stock-movements` | Oluşturur | 201 ve `Location` |

**Anahtar:** `MapCrud` ile aynı (`Id`; `Guid`, `int` veya `long`).

```http
POST /stock-movements
Content-Type: application/json

{ "productId": "01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11", "quantity": 5, "reason": "Initial" }
```

```http
HTTP/1.1 201 Created
Location: /stock-movements/01937d10-4f2b-7c61-a0d4-8b3e5f1a2c77

{
  "id": "01937d10-4f2b-7c61-a0d4-8b3e5f1a2c77",
  "productId": "01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11",
  "quantity": 5,
  "reason": "Initial",
  "createdAt": "2026-11-25T09:14:03.512345+00:00",
  "updatedAt": null
}
```

`createdAt` ve `updatedAt` alanlarını `.Auditable()` ekler. `PUT`, `PATCH` ve `DELETE` istekleri 405 alır.

## `MapStore`

Anahtarını istemcinin verdiği anahtar-değer kayıtları: liste, anahtarla okuma, anahtarla ekle-veya-değiştir (`PUT`)
ve silme. Özellik bayrağı, kullanıcı tercihi veya dış bir sistemin kimliğiyle tutulan kayıt gibi, istemcinin adresi
önceden bildiği veriler içindir. `PUT` aynı anahtarla tekrarlandığında aynı sonucu verir; ayrıca bir oluşturma ucu
yoktur.

<!-- example: store -->
```csharp
app.MapStore<FeatureFlag>();

public partial class FeatureFlag
{
    public bool Enabled { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }
}
```

| Yöntem | Adres | Ne yapar | Başarı |
|---|---|---|---|
| `GET` | `/feature-flags` | Sayfalanmış liste, anahtara göre sıralı | 200 |
| `GET` | `/feature-flags/{key}` | Tek kayıt | 200 |
| `PUT` | `/feature-flags/{key}` | Anahtar yoksa oluşturur, varsa yazılabilir alanların hepsini değiştirir | 201 ve `Location` (yeni) veya 200 |
| `DELETE` | `/feature-flags/{key}` | Siler | 204 |

**Anahtar:** `Key`. Yazmazsanız en çok 200 karakterlik bir `string` olarak üretilir ve istemci adreste verir. Kendiniz
`string`, `Guid`, `int` veya `long` tipinde bir `Key` yazabilirsiniz. Anahtar istek gövdesinde yer almaz.

```http
PUT /feature-flags/new-checkout
Content-Type: application/json

{ "enabled": true, "description": "New checkout flow" }
```

```http
HTTP/1.1 201 Created
Location: /feature-flags/new-checkout

{ "key": "new-checkout", "enabled": true, "description": "New checkout flow" }
```

Aynı anahtara ikinci `PUT` kaydı değiştirir ve 200 döner. Aynı yeni anahtara aynı anda gelen yazmaların hepsi
başarılı olur; yalnızca biri 201 alır, kayıt bir kez oluşur. `.MultiTenant()` ile aynı anahtar her kiracıda ayrı bir
kayıttır.

`PATCH` ve `POST` yoktur (405).

## `MapLookup`

Kod ve addan oluşan küçük referans listeleri: ülkeler, para birimleri, birimler. Liste sayfalanmaz; her zaman bütün
kayıtları koda göre sıralı döndürür. `Code` ve `Name` alanlarını Haldus üretir; entity'yi hiç yazmanız gerekmez.

<!-- example: lookup -->
```csharp
app.MapLookup<Country>();
```

İsterseniz kendi alanlarınızı ekleyebilirsiniz:

<!-- example: lookup-fields -->
```csharp
app.MapLookup<Currency>();

public partial class Currency
{
    [Range(0, 4)]
    public int Decimals { get; set; } = 2;
}
```

| Yöntem | Adres | Ne yapar | Başarı |
|---|---|---|---|
| `GET` | `/countries` | Bütün liste, koda göre sıralı; sayfalama yoktur | 200 |
| `GET` | `/countries/{code}` | Tek kayıt | 200 |
| `PUT` | `/countries/{code}` | Kod yoksa oluşturur, varsa değiştirir | 201 ve `Location` (yeni) veya 200 |
| `DELETE` | `/countries/{code}` | Siler | 204 |

**Anahtar:** `Code`, istemcinin adreste verdiği en çok 200 karakterlik bir metin. `Name` zorunludur ve en çok 200
karakterdir; boş veya yalnızca boşluktan oluşan ad 400 alır.

```http
PUT /countries/TR
Content-Type: application/json

{ "name": "Türkiye" }
```

```http
HTTP/1.1 201 Created
Location: /countries/TR

{ "code": "TR", "name": "Türkiye" }
```

```http
GET /countries
```

```json
[
  { "code": "DE", "name": "Germany" },
  { "code": "TR", "name": "Türkiye" }
]
```

Liste bir dizi döndürür, sayfalanmış zarf (`items`, `totalCount`) değil. Bu yüzden `Filterable`, `Searchable` ve
`Sortable` lookup'ta kullanılamaz (`HALDUS007`). Başka kayıtların referans verdiği bir kod silinemez (409).

## `MapSingleton`

Kapsamı başına tam olarak bir kayıt: uygulama ayarları, bakım duyurusu, kiracının mağaza ayarları. Kayıt örtük olarak
vardır: hiç yazılmadıysa `GET` entity'nin varsayılan değerlerini döndürür. Oluşturma ve silme yoktur.

<!-- example: singleton -->
```csharp
app.MapSingleton<StoreSettings>()
    .MultiTenant()
    .Auditable();

public partial class StoreSettings
{
    [MaxLength(200)]
    public string StoreName { get; set; } = "";

    // Hiç yazılmamış ayarlar bu varsayılanlarla okunur.
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "TRY";

    [Range(0.0, 100.0)]
    public decimal TaxRate { get; set; } = 20m;
}
```

| Yöntem | Adres | Ne yapar | Başarı |
|---|---|---|---|
| `GET` | `/store-settings` | Kaydı, hiç yazılmadıysa varsayılanları döndürür | 200 |
| `PUT` | `/store-settings` | Yazılabilir alanların hepsini yazar; kayıt yoksa oluşturur | 200 |
| `PATCH` | `/store-settings` | Yalnızca gönderilen alanları yazar; kayıt yoksa varsayılanlardan başlar | 200 |

**Anahtar:** yoktur; yanıtta ve adreste kimlik görünmez. Kapsam zincirden gelir:

| Zincir | Kapsam | Adres |
|---|---|---|
| `app.MapSingleton<T>()` | Bütün uygulamada tek kayıt | `/store-settings` |
| `.MultiTenant()` | Kiracı başına tek kayıt; kiracısı çözülemeyen istek 403 alır | `/store-settings` |
| `.Under<TParent>()` | Ebeveyn başına tek kayıt; ebeveyn yoksa 404 | `/suppliers/{supplierId}/supplier-terms` |

```http
GET /store-settings
```

```json
{ "storeName": "", "currency": "TRY", "taxRate": 20, "createdAt": "0001-01-01T00:00:00+00:00", "updatedAt": null }
```

```http
PATCH /store-settings
Content-Type: application/json

{ "storeName": "Ada Store" }
```

```json
{ "storeName": "Ada Store", "currency": "TRY", "taxRate": 20, "createdAt": "2026-11-25T09:20:41.104233+00:00", "updatedAt": null }
```

Aynı kapsama aynı anda gelen ilk yazmaların hepsi başarılı olur ve tek bir kayıt kalır. `POST` ve `DELETE` 405 alır.
Ebeveyn altındaki singleton `MultiTenant` alamaz (kiracı ebeveynden gelir) ve `shallow: true` ile kısa adrese
taşınamaz; bkz. [ebeveyn altında](relationships.md#ebeveyn-altında).

## Kendi rehberi olan archetype'lar

Aşağıdaki archetype'ların kendi kuralları ve uçları vardır; burada yalnızca ne zaman seçildikleri yazılıdır.

### `MapDetail`

Başka bir kaydın parçası olan satırlar (sipariş satırları, fatura kalemleri). `app` üzerinden değil, sahibin
builder'ından çağrılır. Satırlar varsayılan olarak sahibin isteğine ve yanıtına gömülüdür; `.AsSubresource()` onları
sahibin adresi altında kendi uçlarına, `.AsCommands()` tek tek yazılan komutlara taşır.

<!-- example: detail -->
```csharp
var orders = app.MapCrud<Order>();
orders.MapDetail<OrderItem>();

public partial class Order
{
    [MaxLength(200)]
    public string Customer { get; set; } = "";
}

public partial class OrderItem
{
    [MaxLength(50)]
    public string Sku { get; set; } = "";

    public int Quantity { get; set; }
}
```

Ayrıntısı: [Detaylar ve hesaplanan alanlar](details-and-computed-fields.md).

### `MapFile`

İçeriği ayrıca yüklenen bir `MapCrud` kaynağı: `POST` kaydı oluşturur, `PUT /{id}/content` içeriği yükler,
`GET /{id}/content` indirir. En büyük boyut varsayılan olarak 10 MB'dir; türler verilmezse her tür kabul edilir.

<!-- example: file -->
```csharp
app.MapFile<Attachment>(maxBytes: 5 * 1024 * 1024, "application/pdf", "image/*");

public partial class Attachment;
```

Ayrıntısı: [Dosyalar](files.md).

### `MapJob`

İsteği aşan iş (rapor üretme, içe aktarma): `POST` işi oluşturur ve hemen 202 döner, iş arka planda çalışır, `GET`
durumunu ve ilerlemesini gösterir, `POST /{id}/cancel` iptal eder. Bir iş altyapısı paketi gerekir.

Ayrıntısı: [Arka plan işleri](jobs.md).

### `MapBalance`

Yalnızca komutlarla değişen ve her değişimi deftere yazılan nicelik (stok, izin bakiyesi, puan):
`POST /{key}/adjust`, `/set`, `/move` ve `GET /{key}/entries`. Anahtarı (`Key`) istemci verir; bakiye varsayılan
olarak sıfırın altına düşemez.

Ayrıntısı: [Bakiyeler](balances.md).

### `MapAssociation`

İki kaynak arasında çoktan-çoğa bağ: `PUT /products/{id}/tags/{tagId}` bağı kurar, `DELETE` kaldırır, bağlı kayıtlar
iki yönden listelenir. Bağ entity'sini (`ProductTag`) Haldus üretir; iki uç da bir `Map*` çağrısıyla eşlenmiş
olmalıdır.

<!-- example: association -->
```csharp
app.MapCrud<Product>();
app.MapCrud<Tag>();
app.MapAssociation<Product, Tag>();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class Tag
{
    [MaxLength(50)]
    public string Name { get; set; } = "";
}
```

Ayrıntısı: [İlişkiler](relationships.md#çoktan-çoğa-bağ).

### `MapAction` ve `MapQuery`

Bir kayda bağlı olmayan işlemler. `MapAction` yan etkili bir komutu `POST` ile, `MapQuery` yan etkisiz bir hesabı
`GET` ile sunar. Adres istek tipinin adından gelir (`ShippingQuoteRequest` → `/shipping-quote`); mantık bir handler
sınıfında veya çağrıya verilen lambda'dadır.

<!-- example: query -->
```csharp
app.MapQuery<ShippingQuoteRequest, ShippingQuote>((request, _) =>
    Task.FromResult(new ShippingQuote(request.Weight * 12.5m)));

public sealed class ShippingQuoteRequest
{
    [Range(0.1, 1000)]
    public decimal Weight { get; set; }
}

public sealed record ShippingQuote(decimal Price);
```

Bir kaydın üzerinde çalışan aksiyonlar (`POST /coupons/{id}/redeem`) ise kaynağın builder'ından bildirilir; bkz.
[entity aksiyonları](business-logic.md#entity-aksiyonları). Ayrıntısı:
[serbest aksiyonlar ve sorgular](business-logic.md#serbest-aksiyonlar-ve-sorgular).

## Birlikte kullanım

Archetype'ın anlamıyla çelişen bir bildirim sessizce yok sayılmaz, derleme hatası olur (`HALDUS007`). En sık
karşılaşılanlar aşağıdadır; "Hayır" yazan bileşim `HALDUS007` verir:

| Bildirim | `MapCrud` | `MapReadOnly` | `MapAppendOnly` | `MapStore` | `MapLookup` | `MapSingleton` |
|---|---|---|---|---|---|---|
| `ConcurrencyChecked` | Evet | Hayır | Hayır | Hayır | Hayır | Hayır |
| `SoftDeletable` | Evet | Evet | Hayır | Hayır | Hayır | Hayır |
| `References<T>` | Evet | Evet | Evet | Evet | Evet | Hayır |
| `Under<T>` | Evet | Evet | Evet | Hayır | Hayır | Evet |
| `Hierarchical` | Evet | Evet | Evet | Hayır | Hayır | Hayır |
| `Workflow`, `Axis` | Evet | Evet | Evet | Hayır | Hayır | Hayır |
| `Numbered`, `Expiring` | Evet | Hayır | Evet | Hayır | Hayır | Hayır |
| `Idempotent`, `Batch`, `KeyStrategy`, `Metadata` | Evet | Evet | Evet | Hayır | Hayır | Hayır |
| `Filterable`, `Searchable`, `Sortable` | Evet | Evet | Evet | Evet | Hayır | Hayır |
| `OwnedByCurrentUser` | Evet | Evet | Evet | Hayır | Hayır | Hayır |

Bir trait'in başka bir trait'le birleşip birleşmediği o trait'in kendi rehberindedir. Hatanın mesajı her zaman iki
bildirimi ve nedeni söyler; bkz. [tanılar](../reference/diagnostics.md#haldus007).

## Sınırlar

- Bir entity yalnızca bir `Map*` çağrısıyla eşlenir (`HALDUS002`). Aynı veriyi iki ayrı archetype ile sunmak için iki
  ayrı entity gerekir.
- `MapStore`'da `PATCH` ve ayrı bir oluşturma ucu yoktur; `PUT` her zaman kaydın tamamını yazar.
- `MapLookup` listesi sayfalanmaz, süzülmez ve sıralaması değiştirilemez; büyük listeler için `MapCrud` veya
  `MapStore` kullanın.
- `MapSingleton`'ın silme ucu yoktur. Kaydı varsayılanlara döndürmek için varsayılan değerleri `PUT` ile yazın.
- `MapSingleton` kullanıcı başına olamaz (`/me` altında tek kayıt yoktur).
- Çoğullaştırma basittir: sondaki `y` → `ies`, `s`/`x`/`z`/`ch`/`sh` → `es`, geri kalanı `s` alır (`Person` →
  `/persons`).

## Ayrıca bakın

- [Bildirim dizini](../reference/declarations.md)
- [Kurallar ve adlandırma](../concepts/conventions.md)
- [HTTP sözleşmesi](../reference/http.md)
- [Standart işlemleri seçmek](record-operations.md#standart-işlemleri-seçmek)
- [İlişkiler](relationships.md)
