# Kayıt işlemleri

Haldus her kaynak için standart uçları (liste, tekil okuma, oluşturma, `PUT`, `PATCH`, silme) üretir. Bu sayfa o
uçların seçilmesini ve standart uçların ötesindeki kayıt işlemlerini anlatır: toplu işlemler, kopyalama, sıralı
kayıtlar, kaydetmeyen önizleme, ölçüte göre silme ve düzenlenemeyen bir kaydı adım adım düzelten değişiklik kümeleri.
Hepsi `Map*` çağrısının zincirinde bildirilir; uçları, istek ve yanıt tiplerini Haldus derleme sırasında üretir.

Örneklerde web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs` içindedir; `builder` ve
`app` değişkenleri [kurulum](../getting-started/installation.md) sayfasındaki gibi tanımlıdır.

## İçindekiler

- [Standart işlemleri seçmek](#standart-işlemleri-seçmek)
- [Toplu işlemler](#toplu-işlemler)
- [Kopyalama](#kopyalama)
- [Sıralı kayıtlar](#sıralı-kayıtlar)
- [Önizleme](#önizleme)
- [Ölçüte göre silme](#ölçüte-göre-silme)
- [Değişiklik kümeleri](#değişiklik-kümeleri)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Standart işlemleri seçmek

Bir archetype'ın standart işlemleri `CrudOperation` enum'uyla adlandırılır:

| Üye | Uç |
|---|---|
| `List` | `GET /{kaynak}` |
| `Get` | `GET /{kaynak}/{id}` |
| `Create` | `POST /{kaynak}` |
| `Update` | `PUT /{kaynak}/{id}` |
| `Patch` | `PATCH /{kaynak}/{id}` |
| `Delete` | `DELETE /{kaynak}/{id}` |

`Without(...)` sayılan işlemlerin uçlarını üretmez; `Only(...)` yalnızca sayılanları üretir. İkisi de aynı zincirde
yazılabilir ve yazıldıkları sırayla uygulanır: `Only` seçimi baştan kurar, `Without` o ana kadarki seçimden çıkarır.

<!-- example: operations -->
```csharp
// Marka silinmez; yalnızca arşivlenir.
app.MapCrud<Brand>()
    .Archivable()
    .Without(CrudOperation.Delete);

// API anahtarı oluşturulur, okunur ve silinir; değiştirilmez.
app.MapCrud<ApiClient>()
    .Only(CrudOperation.List, CrudOperation.Get, CrudOperation.Create, CrudOperation.Delete);

public partial class Brand
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}

public partial class ApiClient
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}
```

Üretilmeyen bir işlemin adresine gelen istek, aynı adreste başka bir yöntem varsa `405 Method Not Allowed`, yoksa
`404 Not Found` alır. Geçişler, aksiyonlar ve `archive` gibi trait uçları seçimden etkilenmez; yukarıdaki örnekte
marka silinemez ama arşivlenir. Toplu uçlar ise seçilen işlemleri izler (bkz. [toplu işlemler](#toplu-işlemler)).

### Kurallar

- Archetype'ın sahip olmadığı bir işlemi saymak `HALDUS007`'dir. Her archetype'ın işlemleri:

  | Archetype | İşlemler |
  |---|---|
  | `MapCrud` | altısı da |
  | `MapReadOnly` | `List`, `Get` |
  | `MapAppendOnly` | `List`, `Get`, `Create` |
  | `MapStore`, `MapLookup` | `List`, `Get`, `Update` (`PUT /{key}`), `Delete` |
  | `MapSingleton` | `Get`, `Update`, `Patch` |

- Bir işlemi gerektiren bildirim, işlem kaldırıldıysa `HALDUS007` verir. Örnekler: `Cloneable`, `Previewable`,
  `SecretOnce` ve `Publishable` `Create` ister; `Purgeable` `Delete` ister; `ChangeSets` `Patch` ister; `Filterable`,
  `Searchable` ve `Sortable` `List` ister.

## Toplu işlemler

`Batch()` kaynağın sahip olduğu işlemlerin toplu biçimlerini ekler. Her öğe, tekil ucun bütün kurallarından geçer:
doğrulama, referansların varlığı, benzersizlik, durum kuralları, hesaplanan alanlar. Ebeveyn altındaki bir kaynakta
ebeveyn adresten gelir ve görünürlüğü korunur.

<!-- example: batch -->
```csharp
// Varsayılan: atomik. Bir öğe reddedilirse hiçbir şey kaydedilmez.
app.MapCrud<Product>()
    .Batch();

// Kısmi: geçerli öğeler kaydedilir, yanıt öğe başına sonuç taşır.
app.MapCrud<Tag>()
    .Batch(atomic: false, maxItems: 50);

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [Range(0, 1_000_000)]
    public decimal Price { get; set; }
}

public partial class Tag
{
    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = "";
}
```

### Üretilen uçlar

| Yöntem ve adres | Gerektirdiği işlem | Gövde | Başarılı yanıt |
|---|---|---|---|
| `POST /products/batch-get` | `Get` | `{ "ids": [...] }` | 200: `{ "items": [...], "notFound": [...] }` |
| `POST /products/batch-create` | `Create` | `{ "items": [ {oluşturma gövdesi}, ... ] }` | 200: `{ "items": [...] }` |
| `POST /products/batch-update` | `Update` | `{ "items": [ { "id": ..., ...PUT gövdesi }, ... ] }` | 200: `{ "items": [...] }` |
| `POST /products/batch-delete` | `Delete` | `{ "ids": [...] }` | 204 |

Kaynağın sahip olmadığı işlemin toplu ucu üretilmez; `MapReadOnly` bir kaynakta yalnızca `batch-get` vardır. Toplu
güncelleme `PUT` gibidir: her öğe bütün yazılabilir alanları taşır. Toplu yama (`PATCH`) yoktur.

Toplu okuma her zaman kısmidir: bulunanlar `items` içinde, bulunamayan veya isteği yapanın göremediği kimlikler
`notFound` içinde döner.

### Atomik mod

Varsayılan moddur. Öğeler istekteki sırayla işlenir ve hepsi tek bir transaction içindedir. Bir öğe reddedilirse
istek o öğenin hatasıyla döner ve daha önce işlenen öğeler de geri alınır.

```http
POST /products/batch-create
Content-Type: application/json

{
  "items": [
    { "name": "Keyboard", "price": 100 },
    { "name": "Mouse", "price": 40 },
    { "name": "Monitor", "price": 900 }
  ]
}
```

```json
{
  "items": [
    { "id": "0193a5c2-7d4e-7b3a-9f1e-2c4d5e6f7a01", "name": "Keyboard", "price": 100 },
    { "id": "0193a5c2-7d4e-7b3a-9f1e-2c4d5e6f7a02", "name": "Mouse", "price": 40 },
    { "id": "0193a5c2-7d4e-7b3a-9f1e-2c4d5e6f7a03", "name": "Monitor", "price": 900 }
  ]
}
```

Hata, hangi öğeye ait olduğunu iki yoldan söyler: `index` alanı öğenin istekteki sırasıdır (0'dan başlar) ve alan
düzeyindeki hataların adları `items[2].` önekini taşır. Durum kodu, öğe tek başına gönderilseydi dönecek koddur.

```http
POST /products/batch-create
Content-Type: application/json

{
  "items": [
    { "name": "Keyboard", "price": 100 },
    { "name": "Mouse", "price": 40 },
    { "name": "Monitor", "price": -1 }
  ]
}
```

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "items[2].price": ["The price field must be between 0 and 1000000."]
  },
  "index": 2
}
```

Bulunamayan bir kayıt (`batch-update`, `batch-delete`) `404` ve o öğenin `index` değeriyle döner; hiçbir şey
değişmez.

### Kısmi mod

`Batch(atomic: false)` ile her öğe kendi sınırı içinde işlenir: geçerli öğeler kaydedilir, reddedilenler
kaydedilmez. Yanıt `207 Multi-Status`'tur ve istekteki her öğe için bir sonuç taşır. `status`, öğe tek başına
gönderilseydi dönecek durum kodudur (oluşturmada `201`, güncellemede `200`, silmede `204`); `problem`, o durumda
dönecek hatanın aynısıdır.

```http
POST /tags/batch-create
Content-Type: application/json

{
  "items": [
    { "name": "summer" },
    { "name": "" },
    null,
    { "name": "sale" }
  ]
}
```

```json
{
  "results": [
    { "index": 0, "status": 201, "item": { "id": "0193a5c2-80a1-7c10-8d2e-3f4a5b6c7d01", "name": "summer" }, "problem": null },
    {
      "index": 1,
      "status": 400,
      "item": null,
      "problem": {
        "title": "One or more validation errors occurred.",
        "status": 400,
        "errors": { "name": ["The name field is required."] }
      }
    },
    {
      "index": 2,
      "status": 400,
      "item": null,
      "problem": { "title": "One or more validation errors occurred.", "status": 400, "detail": "An item of the batch is empty." }
    },
    { "index": 3, "status": 201, "item": { "id": "0193a5c2-80a1-7c10-8d2e-3f4a5b6c7d02", "name": "sale" }, "problem": null }
  ]
}
```

Kısmi modda `batch-delete` da `207` döner; başarılı öğenin `item` alanı silinen kaydın kimliğidir.

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Öğe sayısı `maxItems` değerini aşıyor | `400`, `detail`: "A batch request can contain at most 50 items."; hiçbir öğe işlenmez |
| Atomik modda bir öğe reddedildi | O öğenin hatası (`400`, `404`, `409`...), `index` ve `items[n].` önekli alan adlarıyla; hiçbir şey kaydedilmez |
| Kısmi modda bir öğe reddedildi | `207`; o öğenin sonucu `problem` taşır, öteki öğeler kaydedilir |

### Seçenekler

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `atomic` | `true` | `true`: ya hepsi ya hiçbiri. `false`: öğe başına sonuç, `207`. |
| `maxItems` | `100` | Bir istekte kabul edilen en çok öğe; 1 ile 10 000 arasında bir sabit. |

## Kopyalama

`Cloneable()` bir kaydın kopyasını oluşturan bir uç ekler. Kopya yeni bir kayıttır: yazılabilir alanlar (yalnızca
oluştururken yazılanlar dahil) ve gömülü satırlar kaynaktan taşınır; geri kalanı yeni bir kayıtta olduğu gibi atanır:
yeni kimlik, başlangıç durumu, kendi kuralıyla yeni numara, zaman damgaları, sahip, sıradaki konum.

<!-- example: clone -->
```csharp
var carts = app.MapCrud<Cart>()
    .Cloneable();

carts.MapDetail<CartItem>();

// Kupon kodu benzersizdir: kopya yeni bir kod ister.
app.MapCrud<Coupon>()
    .NaturalKey(coupon => coupon.Code)
    .CreateOnly(coupon => coupon.Code, coupon => coupon.Percent)
    .Cloneable();

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

public partial class Coupon
{
    [MaxLength(50)]
    public string Code { get; set; } = "";

    [Range(1, 90)]
    public int Percent { get; set; }
}
```

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `POST /carts/{id}/clone` | Kaydı kopyalar; `201 Created`, `Location` başlığı ve kopyanın kendisi döner. |

Gövde isteğe bağlıdır. Gönderilirse `PATCH` biçimindedir (`CloneCartRequest`): yalnızca gönderilen alanlar kopyada
kaynaktakinin yerine geçer. Gövde kaydın kendi alanlarını taşır, yalnızca oluştururken yazılan alanlar da dahil;
gömülü satırlar her zaman kaynaktan kopyalanır.

```http
POST /carts/0193a5c2-9b10-7e21-a3c4-5d6e7f809101/clone
Content-Type: application/json

{ "note": "Weekly (copy)" }
```

```json
{
  "id": "0193a5c3-0c22-7f43-b5d6-7e8f90a1b201",
  "note": "Weekly (copy)",
  "cartItems": [
    { "id": "0193a5c3-0c22-7f43-b5d6-7e8f90a1b202", "productName": "Milk", "quantity": 2 },
    { "id": "0193a5c3-0c22-7f43-b5d6-7e8f90a1b203", "productName": "Eggs", "quantity": 12 }
  ]
}
```

Satırlar kopyalanır, paylaşılmaz: her satırın yeni bir kimliği vardır ve kopyadaki değişiklik kaynağa dokunmaz.

### Kurallar ve hata yanıtları

Kopya, oluşturma ucunun kurallarından geçer:

| Durum | Yanıt |
|---|---|
| Kaynak kayıt yok veya isteği yapana görünmüyor | `404` |
| Gövdedeki bir alan geçersiz | `400` |
| Doğal anahtar (`NaturalKey`) değiştirilmeden kopyalandı | `409`; yeni değeri gövdede verin: `{ "code": "SPRING-2" }` |
| Kopyanın bir referansı (kaynaktan devralınanlar dahil) isteği yapana görünmüyor | `400`, alan düzeyinde |

Referanslar kopyanın değerleri üzerinden yeniden doğrulanır: kaynak kaydı görebilmek, onun referans verdiği kaydı
görebilmek demek değildir. Kopyanın hesaplanan alanları (`IComputed`) yeniden hesaplanır. Sunucunun tuttuğu alanlar
(ör. herkese açık setter'ı olmayan bir kullanım sayacı) kopyada yeni bir kayıttaki gibi başlar.

## Sıralı kayıtlar

`Orderable()` kayıtlara bir sıra verir. `Position` alanı üretilir: 0'dan başlayan, boşluksuz bir tam sayı. Liste,
başka bir sıralama istenmedikçe bu sırayla döner.

Sıra, kaydın kapsamı içindedir: aynı kiracı, aynı sahip, aynı ebeveyn (`Under<T>()`); ağaçta (`Hierarchical()`)
aynı üst düğüm. Her ebeveynin kayıtları kendi aralarında sıralanır.

<!-- example: orderable -->
```csharp
// Kategoriler bir ağaçtır; kardeşler kendi aralarında sıralıdır.
app.MapCrud<Category>()
    .Hierarchical()
    .Orderable();

// Kalemler listenin içinde sıralıdır.
app.MapCrud<ShoppingList>();

app.MapCrud<ShoppingListItem>()
    .Under<ShoppingList>()
    .Orderable();

public partial class Category
{
    public int Id { get; set; }

    [StringLength(100, MinimumLength = 2)]
    public string Name { get; set; } = "";
}

public partial class ShoppingList
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}

public partial class ShoppingListItem
{
    [MaxLength(200)]
    public string Text { get; set; } = "";
}
```

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `POST /categories/{id}/move` | Kaydı verilen konuma taşır ve kapsamdaki kayıtları yeniden numaralar; kaydı döndürür. |
| `POST /shopping-lists/{shoppingListId}/shopping-list-items/{id}/move` | Aynısı, ebeveynin altında. |

`Position` yanıtta görünür ama hiçbir istekte yer almaz; yalnızca Haldus yazar:

- Yeni kayıt kapsamın sonuna eklenir. Geri yüklenen (`restore`) kayıt da sona eklenir.
- Silinen kaydın arkasındakiler bir öne kayar; sırada boşluk kalmaz.
- Ağaçta üst düğümünü değiştiren güncelleme (`parentId`), kaydı yeni dalın sonuna koyar ve eski daldaki boşluğu
  kapatır. Aynı dalda kalan güncelleme sıraya dokunmaz.

```http
POST /categories/42/move
Content-Type: application/json

{ "position": 0 }
```

```json
{ "id": 42, "name": "Laptops", "parentId": 7, "position": 0 }
```

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| `position` gönderilmedi veya eksi | `400` |
| `position` listenin dışında (ör. `99`) | Hata değildir; kayıt sona taşınır. |
| Kayıt yok veya isteği yapana görünmüyor | `404` |
| Kaynak `ConcurrencyChecked` ve `If-Match` yok / eski | `428` / `412` |

Kapsamın üyelerini değiştiren her yazma (oluşturma, silme, geri yükleme, dal değiştirme, taşıma) bir veritabanı
kilidinin altında yapılır. Aynı kapsama aynı anda yazan istekler sıraya girer; kimse aynı konumu almaz ve sıra
boşluksuz kalır. `Position` aranabilir (`Searchable`) ve sıralanabilir (`Sortable`) bir alandır.

## Önizleme

`Previewable()` bir yazmanın sonucunu kaydetmeden gösterir. İstek gerçek uçtaki gibi doğrulanır, kurallardan geçer ve
hesaplanan alanlar (`IComputed`) hesaplanır; sonra hiçbir şey kaydedilmez. Sipariş toplamını, vergiyi veya indirimi
kullanıcı onaylamadan göstermek için kullanılır.

<!-- example: preview -->
```csharp
var orders = app.MapCrud<Order>()
    .ConcurrencyChecked()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)
    .Previewable();

orders.MapDetail<OrderItem>();
orders.MapTransition("place", from: OrderStatus.Draft, to: OrderStatus.Placed);

public enum OrderStatus
{
    Draft,
    Placed,
}

public partial class Order : IComputed
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";

    public decimal Total { get; private set; }

    public int ItemCount { get; private set; }

    public void Compute()
    {
        Total = OrderItems.Sum(item => item.Quantity * item.UnitPrice);
        ItemCount = OrderItems.Count;
    }
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

### Üretilen uçlar

| Yöntem ve adres | Gövde | Ne yapar |
|---|---|---|
| `POST /orders/preview` | Oluşturma gövdesi | Yeni bir kaydın nasıl olacağını döndürür (`200`). |
| `POST /orders/{id}/preview` | Güncelleme (`PUT`) gövdesi | Kaydın güncellemeden sonra nasıl olacağını döndürür (`200`). |

Güncelleme önizlemesi yalnızca kaynağın `Update` işlemi varsa üretilir.

```http
POST /orders/preview
Content-Type: application/json

{
  "customerName": "Ada",
  "orderItems": [
    { "productName": "Keyboard", "quantity": 2, "unitPrice": 100 },
    { "productName": "Mouse", "quantity": 1, "unitPrice": 50 }
  ]
}
```

```json
{
  "id": "00000000-0000-0000-0000-000000000000",
  "customerName": "Ada",
  "total": 250,
  "itemCount": 2,
  "status": "Draft",
  "orderItems": [
    { "id": "0193a5c4-0f10-7d20-8e30-4f5a6b7c8d01", "productName": "Keyboard", "quantity": 2, "unitPrice": 100 },
    { "id": "0193a5c4-0f10-7d20-8e30-4f5a6b7c8d02", "productName": "Mouse", "quantity": 1, "unitPrice": 50 }
  ]
}
```

Sunucunun kaydederken atadığı alanlar önizlemede boştur: kaydın kimliği, numara (`Numbered`), zaman damgaları,
kiracı, sahip, konum (`Orderable`). Gömülü satırlar kimlik alır, ama bu kimlikler hiçbir yerde saklanmaz.

### Kurallar ve hata yanıtları

Önizleme gerçek yazmanın kurallarını izler ve aynı hatayı verir:

| Durum | Yanıt |
|---|---|
| Gövde geçersiz | `400` |
| Kayıt yok (güncelleme önizlemesi) | `404` |
| Kayıt düzenlenemeyen bir durumda (`EditableIn`) veya kapanmış | `409` |

Önizleme bir yazma değildir: `ConcurrencyChecked` kaynakta da `If-Match` istemez, kaydın sürümünü (`ETag`)
değiştirmez ve hiçbir şeyi kalıcı olarak değiştirmez.

## Ölçüte göre silme

`Purgeable()` bir ölçüte uyan kayıtları tek istekte siler (Google API tasarımındaki AIP-165). Ölçüt, liste ucunun
filtresidir (`Filterable<TFilter>()`): aynı sorgu parametreleri aynı anlamla kullanılır. Varsayılan olarak hiçbir
şey silinmez; istek yalnızca kaç kaydın silineceğini ve bir örneğini gösterir.

<!-- example: purge -->
```csharp
app.MapCrud<Contact>()
    .Filterable<ContactFilter>()
    .Batch()
    .Purgeable();

public partial class Contact
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(100)]
    public string? Source { get; set; }
}

public sealed class ContactFilter : IFilter<Contact>
{
    public string? Source { get; set; }

    public string? NameStartsWith { get; set; }

    // Yalnızca kaynağı bilinmeyen kayıtlar.
    public bool Unsourced { get; set; }

    public IQueryable<Contact> Apply(IQueryable<Contact> query) => query
        .WhereIf(Source is not null, contact => contact.Source == Source)
        .WhereIf(NameStartsWith is not null, contact => contact.Name.StartsWith(NameStartsWith!))
        .WhereIf(Unsourced, contact => contact.Source == null);
}
```

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `POST /contacts/purge?{ölçütler}` | Ölçüte uyan kayıtları sayar; `force=true` ile siler. |

| Sorgu parametresi | Anlamı |
|---|---|
| Filtrenin parametreleri (`source`, `nameStartsWith`, `unsourced`) | Ölçüt. En az biri verilmelidir. |
| `force` | `true` ise siler; verilmezse yalnızca sayar. |
| `all` | Ölçüt görünen bütün kayıtlara uyuyorsa silmeye izin verir. |

Önce sayın:

```http
POST /contacts/purge?source=import-2024
```

```json
{
  "matched": 3,
  "purged": 0,
  "sample": [
    "0193a5c4-1a00-7a10-8b20-3c4d5e6f7001",
    "0193a5c4-1a00-7a10-8b20-3c4d5e6f7002",
    "0193a5c4-1a00-7a10-8b20-3c4d5e6f7003"
  ]
}
```

Sonra silin:

```http
POST /contacts/purge?source=import-2024&force=true
```

```json
{ "matched": 3, "purged": 3, "sample": ["0193a5c4-1a00-7a10-8b20-3c4d5e6f7001", "..."] }
```

| Alan | Anlamı |
|---|---|
| `matched` | Ölçüte uyan kayıtların sayısı. |
| `purged` | Bu istekte silinen kayıtların sayısı. |
| `sample` | Ölçüte uyan kayıtlardan anahtara göre ilk yüzünün anahtarları. |

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Hiçbir ölçüt verilmedi; boş veya yalnızca boşluk olan metin de verilmemiş sayılır | `400`, `errors.filter`: "Give at least one criterion; a purge without criteria would delete every record." |
| `force=true` ve ölçüt görünen bütün kayıtlara uyuyor (ör. `unsourced=false`), `all=true` yok | `400`, `errors.filter`: "The criteria match every record. Narrow them, or pass all=true to delete everything." |
| Bir kayıt silinemiyor (ör. başka bir kayıt ona referans veriyor) | O kaydın hatası, `index` ile; hiçbir kayıt silinmez |

- Ölçüt kaynağın kendi filtrelerinden geçer: kiracı, sahiplik ve ebeveyn filtreleri uygulanır. İsteği yapan yalnızca
  görebildiği kayıtları silebilir.
- Her kayıt tekil silmenin kurallarından geçer (`DeletableIn`, referans kısıtları, sıralı kayıtların yeniden
  numaralanması). Silme, toplu silmenin öğe yoludur ve tek bir transaction içindedir: biri bile silinemiyorsa hiçbiri
  silinmez.
- `SoftDeletable` bir kaynakta kayıtlar geri alınabilir biçimde silinir.
- Bir istek en çok 1000 kayıt siler. `matched` değeri `purged` değerinden büyükse aynı isteği yineleyin.

## Değişiklik kümeleri

Verilmiş bir sipariş doğrudan düzenlenemez (`EditableIn` yalnızca taslağa izin verir), ama düzeltilmesi gerekebilir.
`ChangeSets(...)` bunu bir düzenleme oturumu olarak sunar: değişiklikler adım adım biriktirilir, sonucu önizlenir ve
hepsi tek seferde uygulanır ya da vazgeçilir.

Küme, kaydın birikmiş `PATCH` gövdesidir. Uygulama, kaydın kısmi güncellemesinin bütün kurallarından geçer;
yalnızca `EditableIn` kuralının yerini `ChangeSets(...)` ile sayılan durumlar alır.

<!-- example: change-sets -->
```csharp
var orders = app.MapCrud<Order>()
    .ConcurrencyChecked()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)

    // Verilmiş sipariş bir değişiklik kümesiyle düzenlenir.
    .ChangeSets(OrderStatus.Placed);

orders.MapDetail<OrderItem>();
orders.MapTransition("place", from: OrderStatus.Draft, to: OrderStatus.Placed);

// Durum makinesi olmayan kayıt her durumda bir kümeyle düzenlenebilir.
app.MapCrud<Discussion>()
    .ChangeSets();

public enum OrderStatus
{
    Draft,
    Placed,
}

public partial class Order
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";
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

public partial class Discussion
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";
}
```

### Üretilen uçlar

Uçlar kaydın adresi altındadır; kümenin görünürlüğü kaydınkiyle aynıdır.

| Yöntem ve adres | Ne yapar | Başarılı yanıt |
|---|---|---|
| `POST /orders/{id}/edits` | Boş bir küme açar. | `201`, `Location`, küme |
| `GET /orders/{id}/edits` | Kaydın kümeleri, en yenisi önce. | `200`, küme dizisi |
| `GET /orders/{id}/edits/{editId}` | Küme ve biriken değişiklikler. | `200`, küme |
| `PATCH /orders/{id}/edits/{editId}` | Değişiklik ekler; gövde kaydın `PATCH` gövdesidir. | `200`, küme |
| `GET /orders/{id}/edits/{editId}/preview` | Küme şimdi uygulansaydı kaydın nasıl olacağı; kaydetmez. | `200`, kayıt |
| `POST /orders/{id}/edits/{editId}/commit` | Değişiklikleri kayda uygular ve kümeyi kapatır. | `200`, kayıt |
| `DELETE /orders/{id}/edits/{editId}` | Kümeden vazgeçer. | `204` |

### Örnek akış

Kümeyi açın:

```http
POST /orders/0193a5c5-2b00-7b10-9c20-4d5e6f708001/edits
```

```json
{
  "id": "0193a5c5-3c11-7c21-8d31-5e6f70819201",
  "status": 0,
  "changes": {},
  "createdAt": "2026-11-25T09:14:03.512+00:00",
  "createdBy": "user-42",
  "closedAt": null,
  "closedBy": null
}
```

Değişiklikleri adım adım ekleyin. Her adım, kaydın kısmi güncellemesinin kurallarıyla doğrulanır; reddedilen
değişiklik kümeye girmez. Üst düzey alanlar birleşir; aynı alan yeniden gönderilirse sonuncusu geçerlidir. Gömülü
satır listesi ve `metadata` çantası bir bütün olarak değişir.

```http
PATCH /orders/0193a5c5-2b00-7b10-9c20-4d5e6f708001/edits/0193a5c5-3c11-7c21-8d31-5e6f70819201
Content-Type: application/json

{ "orderItems": [ { "productName": "Lamp", "quantity": 3, "unitPrice": 20 } ] }
```

```http
PATCH /orders/0193a5c5-2b00-7b10-9c20-4d5e6f708001/edits/0193a5c5-3c11-7c21-8d31-5e6f70819201
Content-Type: application/json

{ "customerName": "Ada Lovelace" }
```

```json
{
  "id": "0193a5c5-3c11-7c21-8d31-5e6f70819201",
  "status": 0,
  "changes": {
    "orderItems": [ { "productName": "Lamp", "quantity": 3, "unitPrice": 20 } ],
    "customerName": "Ada Lovelace"
  },
  "createdAt": "2026-11-25T09:14:03.512+00:00",
  "createdBy": "user-42",
  "closedAt": null,
  "closedBy": null
}
```

Uygulayın. Uygulama kaydın bir yazmasıdır: `ConcurrencyChecked` kaynakta `If-Match` ister ve yanıt kaydın yeni
`ETag` değerini taşır.

```http
POST /orders/0193a5c5-2b00-7b10-9c20-4d5e6f708001/edits/0193a5c5-3c11-7c21-8d31-5e6f70819201/commit
If-Match: "1834"
```

`status` alanı `ChangeSetStatus` enum'unun değeridir: `0` açık (`Open`), `1` uygulanmış (`Committed`), `2`
vazgeçilmiş (`Discarded`). Uygulamanız JSON'da enum'ları adıyla yazıyorsa (`JsonStringEnumConverter`) adıyla görünür.

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Kayıt `ChangeSets(...)` ile sayılmayan bir durumda (açarken, önizlerken, uygularken) | `409` |
| Değişiklik geçersiz (uzunluk, aralık, satır kuralları) | `400`, alan düzeyinde |
| Gövde bir JSON nesnesi değil veya alanların tipine uymuyor | `400`, `errors.changes` |
| Küme uygulanmış veya vazgeçilmiş; yeniden uygulama, değiştirme, vazgeçme | `409` |
| Küme bu kayda ait değil, kayıt veya küme görünmüyor | `404` |
| `If-Match` yok / eski (`ConcurrencyChecked`) | `428` / `412` |

- Kaynağın durum makinesi varsa `ChangeSets(...)` durumları saymalıdır; saymazsa `HALDUS010` olur. Durum makinesi
  olmayan kaynakta durum sayılırsa da `HALDUS010` olur.
- Küme, açıldığı andaki değil uygulandığı andaki kayda uygulanır. Hazırlanırken kayıt başka yoldan değiştiyse
  değişiklikler onun üzerine yazılır; "kayıt bu sırada değişti" çakışması üretilmez. Bunu istiyorsanız kaynağı
  `ConcurrencyChecked` yapın: uygulama isteği okuduğunuz sürümü `If-Match` ile taşır.
- Aynı kümeyi aynı anda uygulamak isteyen iki istekten yalnızca biri geçer; öteki `409` (`ConcurrencyChecked`
  kaynakta `412`) alır.
- Kümede yazılamayan bir alan (ör. herkese açık setter'ı olmayan bir alan) kayda geçmez; küme yalnızca `PATCH`
  isteğinin taşıyabildiği alanları uygular.
- Uygulama; doğrulama, referanslar, benzersizlik, hesaplanan alanlar, onaylanmış veya kapanmış kayıt korumaları
  dahil, `PATCH` ucunun bütün kurallarından geçer.

## Birlikte kullanım

| Bildirim | Birleştiği archetype'lar | Birleşmediği bildirimler (`HALDUS007`) |
|---|---|---|
| `Batch` | `MapCrud`, `MapReadOnly`, `MapAppendOnly` | `ConcurrencyChecked` (kaynak güncellenebiliyor veya silinebiliyorsa: toplu öğe beklediği sürümü taşıyamaz; `Without(CrudOperation.Update, CrudOperation.Delete)` ile birleşir), `SecretOnce`, `TokenAccess`, `KeyStrategy.Client` |
| `Cloneable` | `MapCrud` (ve ayrı uçlu detay) | `SecretOnce`, `TokenAccess`, `KeyStrategy.Client`, `MapFile`; `Create` işlemi kaldırılmışsa |
| `Orderable` | `MapCrud` (ve ayrı uçlu detay) | Takılabilir kaynak (`AttachedTo`) |
| `Previewable` | `MapCrud`, `MapAppendOnly` | `SecretOnce`, `TokenAccess`; `Create` işlemi kaldırılmışsa |
| `Purgeable` | `MapCrud` (ve ayrı uçlu detay) | `ConcurrencyChecked`; `Filterable` ve `Batch` olmadan; `Delete` işlemi kaldırılmışsa |
| `ChangeSets` | `MapCrud` (ve ayrı uçlu detay) | Hassas veya şifreli alan (kaynakta veya gömülü satırlarında; küme isteği saklayıp geri gösterir); `Patch` işlemi kaldırılmışsa |

`move`, `clone`, `preview` ve `edits` adresleri ayrılmıştır; bir geçiş veya aksiyon bu adı alamaz (`HALDUS007`).

Sürüm kontrollü bir kaynakta toplu uç istiyorsanız güncelleme ve silmeyi kaldırın:

<!-- example: batch-versioned, error: HALDUS007 -->
```csharp
// Hatalı: toplu güncellemenin öğesi beklediği sürümü taşıyamaz.
app.MapCrud<Invoice>()
    .ConcurrencyChecked()
    .Batch();

public partial class Invoice
{
    public decimal Amount { get; set; }
}
```

<!-- example: purge-needs-batch, error: HALDUS007 -->
```csharp
// Hatalı: ölçüte göre silme, her kaydı toplu silmenin yolundan siler.
app.MapCrud<Contact>()
    .Filterable<ContactFilter>()
    .Purgeable();

public partial class Contact
{
    [MaxLength(100)]
    public string? Source { get; set; }
}

public sealed class ContactFilter : IFilter<Contact>
{
    public string? Source { get; set; }

    public IQueryable<Contact> Apply(IQueryable<Contact> query) =>
        query.WhereIf(Source is not null, contact => contact.Source == Source);
}
```

## Sınırlar

- **Toplu işlemler:** toplu yama (`PATCH`) ve geçişlerin toplu biçimi yoktur. Sürüm kontrollü kaynakta toplu
  güncelleme ve silme yoktur. Kısmi modda bir öğe beklenmeyen bir hatayla (`500`) düşerse ondan önceki öğeler
  kaydedilmiş kalır.
- **Sıralı kayıtlar:** araya eklenen sıra anahtarı (rank) yoktur; taşıma kapsamdaki kayıtları yeniden numaralar ve
  büyük kapsamlarda çok satır günceller. Ağacın tamamı tek kilittir; aynı ağaca yapılan yazmalar sıraya girer.
  Komşuları yeniden numaralamak onların `UpdatedAt` alanını ilerletmez ama sürümlerini ilerletir: `ConcurrencyChecked`
  bir kaynakta taşıma, komşuların `ETag` değerini de değiştirir. Süresi dolan (`Expiring`) kayıt gizlenir ama
  konumunu tutar; görünen sırada boşluk kalabilir.
- **Önizleme:** kaydederken atanan alanlar (kimlik, numara, zaman damgaları, kiracı, sahip, konum) önizlemede boştur.
  Kaydetme sırasında çalışan kod (uygulamanın kendi interceptor'ları gibi) önizlemede çalışmaz, çünkü kaydetme yoktur.
- **Ölçüte göre silme:** arka plan işi değildir; eşzamanlıdır ve istek başına 1000 kayıtla sınırlıdır.
- **Değişiklik kümeleri:** kümenin toplu, ayrı uçlu detay ve komutla yazılan satır karşılığı yoktur. Bir kaydın birden
  çok açık kümesi olabilir; kümeler birbirinden habersizdir. Kümeler kendiliğinden silinmez ve kayıt silinince kalır.
  Kümenin içeriği en çok 1 milyon karakterdir. Uygulama isteği `Idempotency-Key` almaz (kümenin durumu ikinci
  uygulamayı zaten durdurur).

## Ayrıca bakın

- [Bildirim dizini](../reference/declarations.md)
- [Listeleme, süzme ve sıralama](querying.md): `Filterable`, `Searchable`, `Sortable`, `NaturalKey`
- [Detaylar ve hesaplanan alanlar](details-and-computed-fields.md)
- [Durum makineleri](state-machines.md)
- [Yaşam döngüsü](lifecycle.md): `ConcurrencyChecked`, `SoftDeletable`, `Idempotent`
- [Kayıtlar arası zincir](record-chains.md)
- [HTTP sözleşmesi](../reference/http.md)
