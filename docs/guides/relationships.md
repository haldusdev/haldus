# İlişkiler

Gerçek bir uygulamada kaynaklar birbirine bağlıdır: ürün bir kategoriye aittir, adres bir müşterinin altındadır,
ürünler etiketlerle çoktan-çoğa bağlanır. Haldus'ta bu bağları elle yazmazsınız; kaynağın zincirinde bildirirsiniz.
Haldus yabancı anahtar alanlarını üretir, her yazmada hedefin var olduğunu ve isteği yapana görünür olduğunu doğrular,
adresleri iç içe kurar ve silme davranışını veritabanı modeline yazar. Bu sayfa bütün ilişki bildirimlerini, ürettikleri
uçları ve kurallarını anlatır.

> [!NOTE]
> Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
> `System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs` içindedir;
> `var builder = WebApplication.CreateBuilder(args);` ve `var app = builder.Build();` satırları yazılmamışsa yazılmış
> sayılır. Hata gövdeleri kısaltılmıştır; tam biçim için [HTTP sözleşmesine](../reference/http.md) bakın.

## İçindekiler

- [Referans](#referans)
- [Ebeveyn altında](#ebeveyn-altında)
  - [Kısa adres](#kısa-adres)
  - [Ebeveyn başına tek kayıt](#ebeveyn-başına-tek-kayıt)
- [Ağaç](#ağaç)
- [Gömülü değer nesnesi](#gömülü-değer-nesnesi)
- [Çoktan-çoğa bağ](#çoktan-çoğa-bağ)
  - [Bağları topluca değiştirmek](#bağları-topluca-değiştirmek)
  - [Yük taşıyan bağ](#yük-taşıyan-bağ)
- [Durumu olan bağ](#durumu-olan-bağ)
- [Kullanıcıya bağlı bağ](#kullanıcıya-bağlı-bağ)
- [Tip ve örnek](#tip-ve-örnek)
- [Rol](#rol)
- [Alt tip](#alt-tip)
- [Silme davranışları](#silme-davranışları)
  - [Zincirleme silme kiracı sınırını aşmaz](#zincirleme-silme-kiracı-sınırını-aşmaz)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Referans

`.References<T>()` kaynağa, başka bir kaynağı gösteren bir yabancı anahtar alanı ekler. Alanın adı hedefin adı ile
hedefin anahtar adının birleşimidir (`CategoryId`, `SupplierId`; `MapLookup` hedefinde `CountryCode`). Alanın tipi
hedefin anahtar tipidir. Alanı entity'ye siz yazmazsınız.

<!-- example: references -->
```csharp
app.MapCrud<Category>();
app.MapCrud<Supplier>();

app.MapCrud<Product>()
    .References<Category>(required: false)
    .References<Supplier>();

public partial class Category
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}

public partial class Supplier
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}
```

Haldus `Product` entity'sine iki alan ekler: isteğe bağlı `CategoryId` (`Guid?`) ve zorunlu `SupplierId` (`Guid`). İkisi
de oluşturma, `PUT` ve `PATCH` isteklerinde ve yanıtta yer alır. Referans yeni bir uç üretmez; kaynağın standart uçları
alanı taşır.

```http
POST /products
Content-Type: application/json

{
  "name": "Mekanik klavye",
  "price": 1450,
  "categoryId": "0199a3c2-1f00-7a11-8a5e-2b7c3d4e5f60",
  "supplierId": "0199a3c2-2a10-7b22-9b6f-3c8d4e5f6071"
}
```

```http
HTTP/1.1 201 Created
Location: /products/0199a3c2-4c30-7d44-8d81-5eaf60718293

{
  "id": "0199a3c2-4c30-7d44-8d81-5eaf60718293",
  "name": "Mekanik klavye",
  "price": 1450,
  "categoryId": "0199a3c2-1f00-7a11-8a5e-2b7c3d4e5f60",
  "supplierId": "0199a3c2-2a10-7b22-9b6f-3c8d4e5f6071"
}
```

**Kurallar ve hata yanıtları**

- Her yazmada (oluşturma, `PUT`, `PATCH`) hedefin var olduğu doğrulanır. Hedef, isteği yapanın göremediği bir kayıtsa
  (başka bir kiracının kaydı, silinmiş bir kayıt) da bulunamamış sayılır. Yanıt alan düzeyinde bir doğrulama hatasıdır:

  ```http
  HTTP/1.1 400 Bad Request
  Content-Type: application/problem+json

  {
    "title": "One or more validation errors occurred.",
    "status": 400,
    "errors": {
      "supplierId": [ "Supplier '0199a3c2-9999-7999-8999-999999999999' was not found." ]
    }
  }
  ```

- `PATCH` isteğinde alan gönderilmezse referans değişmez; isteğe bağlı bir referans `null` gönderilerek boşaltılır.
- Varsayılan silme davranışı `Restrict`'tir: kendisine referans verilen bir kayıt silinmek istendiğinde `409 Conflict`
  döner. Yanıtın başlığı `Resource is in use`, Türkçede `Kayıt kullanımda` olur.
- Üretilen alan aranabilir ve sıralanabilir alan olarak sayılabilir (`.Searchable(product => product.CategoryId)`).
  Liste ucuna kendiliğinden bir `?categoryId=` parametresi eklenmez; liste süzmesi için kendi filtre sınıfınızı yazarsınız
  ([Listeleme](querying.md#süzme)).

**Seçenekler**

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `onDelete` | `OnDelete.Restrict` | Hedef silindiğinde bu kayda ne olacağı: `Restrict`, `Cascade` veya `SetNull`. Bkz. [Silme davranışları](#silme-davranışları). |
| `required` | `true` | Referans zorunlu mu. `false` ise alan boş bırakılabilir (`Guid?`). |

Yabancı anahtar alanını kendiniz yazmak isterseniz (adı farklı olsun diye) ikinci biçimi kullanırsınız. Alanı lambda ile
gösterirsiniz; zorunluluk alanın tipinden okunur: `Guid?` isteğe bağlıdır.

<!-- example: references-own-key -->
```csharp
app.MapCrud<Warehouse>();
app.MapCrud<Shipment>()
    .References<Warehouse>(shipment => shipment.ShipFromWarehouseId, OnDelete.Restrict);

public partial class Warehouse
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}

public partial class Shipment
{
    public Guid? ShipFromWarehouseId { get; set; }

    [MaxLength(50)]
    public string Carrier { get; set; } = "";
}
```

Referans başka bir modüldeki bir entity'ye de verilebilir; iki modül aynı veritabanı bağlamındaysa gerçek bir yabancı
anahtar kısıtı kurulur, değilse yalnızca kimlik tutulur ve varlığı yazarken doğrulanır. Ayrıntısı
[Modüler yapı](modules.md) sayfasındadır.

## Ebeveyn altında

`.Under<T>()` kaynağı ebeveyninin adresi altına yerleştirir: müşterinin adresleri `/customers/{customerId}/addresses`
altında yaşar. Ebeveynin kimliği adresten gelir, istek gövdesinde yer almaz. Haldus yabancı anahtar alanını
(`CustomerId`) üretir ve yanıta ekler.

<!-- example: under -->
```csharp
app.MapCrud<Customer>().MultiTenant();
app.MapCrud<Address>().Under<Customer>();
app.MapCrud<AddressNote>().Under<Address>();

public partial class Customer
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class Address
{
    [MaxLength(300)]
    public string Line { get; set; } = "";

    [MaxLength(100)]
    public string City { get; set; } = "";
}

public partial class AddressNote
{
    [MaxLength(500)]
    public string Text { get; set; } = "";
}
```

**Üretilen uçlar**

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/customers/{customerId}/addresses` | Müşterinin adreslerini listeler. |
| `GET` | `/customers/{customerId}/addresses/{id}` | Bir adresi okur. |
| `POST` | `/customers/{customerId}/addresses` | Müşteriye adres ekler. |
| `PUT` / `PATCH` | `/customers/{customerId}/addresses/{id}` | Adresi günceller. |
| `DELETE` | `/customers/{customerId}/addresses/{id}` | Adresi siler. |

Kaynağın bildirdiği öteki işlemler (toplu uçlar, geçişler, arama) aynı adresin altına gelir. `AddressNote`
`/addresses/{addressId}/address-notes` altındadır.

```http
POST /customers/0199a3c3-0a00-7c10-8a10-0a1b2c3d4e50/addresses
Content-Type: application/json

{ "line": "Kordon Cad. 12", "city": "İzmir" }
```

```http
HTTP/1.1 201 Created
Location: /customers/0199a3c3-0a00-7c10-8a10-0a1b2c3d4e50/addresses/0199a3c3-1b00-7d20-9b20-1b2c3d4e5f60

{
  "id": "0199a3c3-1b00-7d20-9b20-1b2c3d4e5f60",
  "line": "Kordon Cad. 12",
  "city": "İzmir",
  "customerId": "0199a3c3-0a00-7c10-8a10-0a1b2c3d4e50"
}
```

**Kurallar ve hata yanıtları**

- Ebeveyn yoksa veya isteği yapana görünmüyorsa her uç `404 Not Found` döner; oluşturma da.
- Bir kayda yalnızca kendi ebeveyninin adresinden ulaşılır. Aynı kiracıdaki başka bir müşterinin adresinden
  `/customers/{başkası}/addresses/{id}` okumak, güncellemek veya silmek `404` döner.
- Kaynağın görünürlüğü ebeveyninden gelir. Yukarıdaki `Address` kendisi `MultiTenant` değildir, ama müşteri öyle olduğu
  için başka bir kiracı adresleri ne listeleyebilir ne okuyabilir ne de onlara adres ekleyebilir. Bu koruma veritabanı
  modelindeki bir sorgu filtresidir; adrese referans veren veya onun altına yerleşen her kaynak (`AddressNote` gibi)
  aynı korumadan geçer.
- Ebeveyn geri alınabilir biçimde silinirse (`SoftDeletable`) altındaki kayıtlar da görünmez olur. Ebeveyn kalıcı
  silinince altındakiler de silinir.
- Varsayılan silme davranışı `Cascade`'dir: ebeveyn silinince altındaki kayıtlar da silinir. Kiracıya ait kayıtlar genel
  bir ebeveynin altındaysa varsayılan `Restrict` olur; bkz.
  [Zincirleme silme kiracı sınırını aşmaz](#zincirleme-silme-kiracı-sınırını-aşmaz).

**Seçenekler**

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `onDelete` | `OnDelete.Cascade` | Ebeveyn silindiğinde bu kayda ne olacağı. |
| `shallow` | `false` | `true` ise kaynak kendi adresinde kalır; bkz. [Kısa adres](#kısa-adres). |

Bir kaynağın yalnızca bir ebeveyni olur; `Under` iki kez yazılırsa `HALDUS007` verilir. `Under` yalnızca `MapCrud`,
`MapReadOnly`, `MapAppendOnly` ve `MapSingleton` ile birleşir.

### Kısa adres

`.Under<T>(shallow: true)` kaynağı ebeveynine bağlar ama kendi adresinde bırakır (`/supplier-contacts/{id}`). İç içe
adresler derinleştiğinde, istemci bir kaydı ebeveynini bilmeden adreslemek istediğinde işe yarar. Ebeveynin anlamı
aynen sürer: zorunludur, görünürlüğü belirler ve silinince altındakiler de silinir.

<!-- example: shallow -->
```csharp
app.MapCrud<Supplier>();
app.MapCrud<SupplierContact>().Under<Supplier>(shallow: true);

public partial class Supplier
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class SupplierContact
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [EmailAddress]
    [MaxLength(200)]
    public string? Email { get; set; }
}
```

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/supplier-contacts?supplierId={supplierId}` | Kişileri listeler; `supplierId` verilirse o tedarikçinin kişilerine süzer. |
| `GET` | `/supplier-contacts/{id}` | Bir kişiyi okur. |
| `POST` | `/supplier-contacts` | Kişi oluşturur; ebeveyn gövdede gelir. |
| `PUT` / `PATCH` | `/supplier-contacts/{id}` | Kişiyi günceller; ebeveyn değişmez. |
| `DELETE` | `/supplier-contacts/{id}` | Kişiyi siler. |

```http
POST /supplier-contacts
Content-Type: application/json

{ "supplierId": "0199a3c4-0a00-7c10-8a10-0a1b2c3d4e50", "name": "Ayşe Demir", "email": "ayse@tedarik.example" }
```

```http
HTTP/1.1 201 Created
Location: /supplier-contacts/0199a3c4-2c00-7e30-8c30-2c3d4e5f6071

{
  "id": "0199a3c4-2c00-7e30-8c30-2c3d4e5f6071",
  "name": "Ayşe Demir",
  "email": "ayse@tedarik.example",
  "supplierId": "0199a3c4-0a00-7c10-8a10-0a1b2c3d4e50"
}
```

- `supplierId` yalnızca oluşturma isteğinde yer alır. Ebeveyn yoksa veya görünmüyorsa `400` döner ve hata
  `supplierId` alanına yazılır.
- `PUT` ve `PATCH` isteklerinde ebeveyn alanı yoktur; gönderilse de yok sayılır. Kaydın ebeveyni sonradan değişmez.
- Liste ebeveynler arasında döner. `Orderable` ile birlikte kullanılırsa sıra ebeveyn içindedir.
- Rails'teki gibi ayrıca bir iç içe koleksiyon adresi (`/suppliers/{id}/supplier-contacts`) üretilmez.
- `ChangeFeed` ile birleşmez (`HALDUS007`): akış, silinen bir kaydın hangi ebeveyne ait olduğunu adresten bilemezdi.

### Ebeveyn başına tek kayıt

`MapSingleton<T>().Under<TParent>()` her ebeveyn için tek bir kayıt tutar: tedarikçinin ödeme koşulları, müşterinin
tercihleri. Kaydın anahtarı ebeveynin anahtarıdır; ayrı bir kimliği yoktur.

<!-- example: singleton-under -->
```csharp
app.MapCrud<Supplier>();
app.MapSingleton<SupplierTerms>().Under<Supplier>();

public partial class Supplier
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class SupplierTerms
{
    [Range(0, 180)]
    public int PaymentDays { get; set; } = 30;

    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "TRY";
}
```

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/suppliers/{supplierId}/supplier-terms` | Kaydı okur; hiç yazılmadıysa entity'nin varsayılan değerlerini döner. |
| `PUT` | `/suppliers/{supplierId}/supplier-terms` | Kaydı yazar (yoksa oluşturur). |
| `PATCH` | `/suppliers/{supplierId}/supplier-terms` | Gönderilen alanları değiştirir. |

```http
GET /suppliers/0199a3c4-0a00-7c10-8a10-0a1b2c3d4e50/supplier-terms
```

```http
HTTP/1.1 200 OK

{ "paymentDays": 30, "currency": "TRY", "supplierId": "0199a3c4-0a00-7c10-8a10-0a1b2c3d4e50" }
```

- Ebeveyn yoksa okuma da yazma da `404` döner.
- Kayıt ebeveyniyle birlikte silinir.
- Kayıt ebeveyninin görünürlüğünü izler; bu yüzden `MultiTenant` ile birlikte yazılamaz (`HALDUS007`): ebeveyn kiracıya
  aitse kayıt da öyledir. `shallow: true` da anlamsızdır ve `HALDUS007` verir.

## Ağaç

`.Hierarchical()` kayıtları aynı kaynağın içinde bir ağaca dizer: kategoriler ve alt kategoriler, yorumlar ve
yanıtları. Haldus isteğe bağlı bir `ParentId` alanı üretir; tipi kaynağın anahtar tipidir.

<!-- example: hierarchical -->
```csharp
app.MapCrud<Category>().Hierarchical();

public partial class Category
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}
```

`ParentId` oluşturma, `PUT` ve `PATCH` isteklerinde yazılır. Kök düğümün `parentId` alanı `null`'dır. Liste ucu iki
sorgu parametresi kazanır:

| İstek | Ne döner |
|---|---|
| `GET /categories?parentId={id}` | Yalnızca o düğümün doğrudan çocukları. |
| `GET /categories?roots=true` | Yalnızca kök düğümler. |

```http
POST /categories
Content-Type: application/json

{ "name": "Klavyeler", "parentId": "0199a3c5-0a00-7c10-8a10-0a1b2c3d4e50" }
```

```http
HTTP/1.1 201 Created
Location: /categories/0199a3c5-1b00-7d20-9b20-1b2c3d4e5f60

{
  "id": "0199a3c5-1b00-7d20-9b20-1b2c3d4e5f60",
  "name": "Klavyeler",
  "parentId": "0199a3c5-0a00-7c10-8a10-0a1b2c3d4e50"
}
```

**Kurallar ve hata yanıtları**

- Üst düğüm var olmalı ve görünür olmalıdır; değilse `400` döner ve hata `parentId` alanına yazılır.
- Bir düğüm kendi altına veya kendi altındaki bir düğümün altına taşınamaz:

  ```http
  HTTP/1.1 400 Bad Request

  {
    "title": "One or more validation errors occurred.",
    "status": 400,
    "errors": {
      "parentId": [ "A record cannot be placed under itself or under one of its own descendants." ]
    }
  }
  ```

- Çocuğu olan düğüm silinemez: `409 Conflict`. Önce çocukları silinir veya taşınır. Bu davranış sabittir; `Hierarchical`
  bir `onDelete` seçeneği almaz.
- Kaynak bir ebeveynin altındaysa (`.Under<Product>().Hierarchical()`), üst düğüm aynı ebeveynin altında olmalıdır: bir
  ürünün yorumu başka bir ürünün yorumuna yanıt olamaz.
- `Orderable` ile birlikte kullanılırsa kardeşler üst düğüm içinde sıralanır; bkz.
  [Kayıt işlemleri](record-operations.md#sıralı-kayıtlar).

`Hierarchical` yalnızca `MapCrud`, `MapReadOnly` ve `MapAppendOnly` ile birleşir.

## Gömülü değer nesnesi

`.Embeds<T>()` o tipteki alanları gömülü değer nesnesi yapar: para, ölçü, adres gibi kendi kimliği ve tablosu olmayan
değerler. Değer DTO'larda iç içe bir nesne olarak görünür, veritabanında sahibinin kolonlarında saklanır (EF Core
complex property).

<!-- example: embeds -->
```csharp
app.MapCrud<Plan>().Embeds<Money>();

public sealed record Money(
    [Range(0.0, 1_000_000_000.0)] decimal Amount,
    [StringLength(3, MinimumLength = 3)] string Currency);

public partial class Plan
{
    [MaxLength(100)]
    public string Name { get; set; } = "";

    public Money Price { get; set; } = new(0m, "TRY");
}
```

Alanı (`Price`) entity'ye siz yazarsınız; `Embeds<Money>` yalnızca o tipteki alanların nasıl taşınacağını söyler.
Bildirim olmadan değer nesnesi tipindeki alanlar DTO'lara girmez.

```http
POST /plans
Content-Type: application/json

{ "name": "Pro", "price": { "amount": 99.5, "currency": "EUR" } }
```

```http
HTTP/1.1 201 Created

{
  "id": "0199a3c6-0a00-7c10-8a10-0a1b2c3d4e50",
  "name": "Pro",
  "price": { "amount": 99.5, "currency": "EUR" }
}
```

**Kurallar ve hata yanıtları**

- Değer nesnesinin kendi doğrulama kuralları her yazmada çalışır. Hata anahtarları alanın yolunu taşır:

  ```http
  HTTP/1.1 400 Bad Request

  {
    "title": "One or more validation errors occurred.",
    "status": 400,
    "errors": {
      "price.amount": [ "The Amount field must be between 0 and 1000000000." ],
      "price.currency": [ "The Currency field must be between 3 and 3 characters long." ]
    }
  }
  ```

- Referans tipli bir değer (`record`, `class`) oluşturma ve `PUT` isteğinde zorunludur; gönderilmezse `400` döner.
  `struct` bir değerin varsayılanı olduğu için zorunlu değildir.
- `PATCH` değeri bütün olarak değiştirir: `{ "price": { "amount": 15, "currency": "USD" } }`. Değerin tek bir alanı
  yamalanmaz. `PATCH` içinde `"price": null` göndermek `400` döner. Değer gönderilmezse dokunulmaz.
- Değer nesnesi tipindeki alan nullable olamaz (`HALDUS007`).
- Bildirilen tipte hiç alan yoksa bildirim etkisizdir ve `HALDUS007` verir.
- Gömülü bir değer aranamaz (`.Searchable(plan => plan.Price)` → `HALDUS005`).

## Çoktan-çoğa bağ

`MapAssociation<TLeft, TRight>()` iki kaynağı çoktan-çoğa bağlar: ürünler ve etiketler. Haldus bağ entity'sini
(`ProductTag`) üretir; anahtarı iki ucun yabancı anahtarlarıdır (`ProductId`, `TagId`). Bağ entity'si sol ucun
namespace'ine üretilir.

<!-- example: association -->
```csharp
app.MapCrud<Product>();
app.MapCrud<Tag>();
app.MapAssociation<Product, Tag>();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}

public partial class Tag
{
    [MaxLength(50)]
    public string Name { get; set; } = "";
}
```

**Üretilen uçlar**

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/products/{productId}/tags` | Ürüne bağlı etiketleri listeler (sayfalı). |
| `GET` | `/tags/{tagId}/products` | Etikete bağlı ürünleri listeler (sayfalı). |
| `GET` | `/products/{productId}/tags/{tagId}` | Bağ varsa `204`, yoksa `404`. |
| `PUT` | `/products/{productId}/tags/{tagId}` | Bağı kurar. Gövdesizdir; `204` döner. |
| `DELETE` | `/products/{productId}/tags/{tagId}` | Bağı kaldırır; bağ yoksa `404`. |
| `PUT` | `/products/{productId}/tags` | Ürünün etiketlerini verilen listeyle aynı yapar; bkz. [Bağları topluca değiştirmek](#bağları-topluca-değiştirmek). |
| `PUT` | `/tags/{tagId}/products` | Aynısı, öteki yönden. |

Listeler bağ kayıtlarını değil, bağlı kayıtların kendisini kendi yanıt biçimlerinde döndürür:

```http
GET /products/0199a3c7-0a00-7c10-8a10-0a1b2c3d4e50/tags
```

```http
HTTP/1.1 200 OK

{
  "items": [ { "id": "0199a3c7-1b00-7d20-9b20-1b2c3d4e5f60", "name": "kablosuz" } ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

**Kurallar ve hata yanıtları**

- `PUT` yinelenebilir: bağ zaten varsa hiçbir şey değişmez ve yine `204` döner. Aynı anda gelen istekler de tek bir bağ
  bırakır.
- İki uç da isteği yapana görünür olmalıdır; biri yoksa veya görünmüyorsa `404` döner. Görünmeyen bir kayda bağ
  kurulamaz.
- Bağ, iki ucu da görünürse görünür. Müşteri gibi kiracıya ait bir uca kurulan bağı başka bir kiracı ne görebilir ne
  kaldırabilir; listelerde de çıkmaz. Ucu geri alınabilir biçimde silinmiş bir bağ, uç geri yüklenene kadar görünmez.
- Uçlardan biri silinince bağları da silinir. İstisnası, genel bir uç ile kiracıya ait bir uç arasındaki bağdır: genel
  ucun silinmesi `409` döner; bkz. [Zincirleme silme kiracı sınırını aşmaz](#zincirleme-silme-kiracı-sınırını-aşmaz).
- İki uç da bir `Map*` çağrısıyla eşlenmiş olmalıdır (`HALDUS009`). Bir kaynak kendisiyle bağlanamaz, `MapSingleton`
  bir bağın ucu olamaz (`HALDUS007`). İki uç aynı modülde eşlenmiş olmalıdır.

Etiketleme için aynı bağ, kaynağın zincirinde `.Taggable<Tag>()` olarak da yazılabilir; bkz.
[Dosyalar](files.md#etiketler).

### Bağları topluca değiştirmek

`PUT /products/{productId}/tags` gövdesinde bir kimlik listesi alır ve ürünün bağlarını o listeyle aynı yapar: listede
olmayan bağ kaldırılır, eksik olan kurulur, ortak olan kalır. Aynı uç öteki yönden de çalışır
(`PUT /tags/{tagId}/products`).

```http
PUT /products/0199a3c7-0a00-7c10-8a10-0a1b2c3d4e50/tags
Content-Type: application/json

{ "ids": [ "0199a3c7-1b00-7d20-9b20-1b2c3d4e5f60", "0199a3c7-2c00-7e30-8c30-2c3d4e5f6071" ] }
```

```http
HTTP/1.1 204 No Content
```

- Boş liste (`{ "ids": [] }`) bütün bağları kaldırır. Yinelenen kimlikler bir kez sayılır.
- Listedeki her kayıt isteği yapana görünür olmalıdır. Biri bile bulunamazsa `400` döner, hata `ids` alanına yazılır ve
  hiçbir bağ değişmez.
- Ürünün kendisi yoksa `404`, gövdede `ids` yoksa `400` döner.
- İsteği yapana görünmeyen bağlara dokunulmaz: bir kiracı listeyi değiştirirken başka bir kiracının bağlarını silemez.
- Liste en çok 1000 kimlik taşır; fazlası `400` döner.
- Uç yalnızca yükü ve durumu olmayan bağlarda üretilir: listedeki kimlikler bir yük taşıyamaz.

### Yük taşıyan bağ

Bağın kendi alanları olabilir: tedarikçinin bir ürünü hangi fiyata ve kaç günde verdiği. Bunun için bağ sınıfını
(`{Sol}{Sağ}`, burada `SupplierProduct`) sol ucun namespace'inde `partial` olarak kendiniz yazarsınız. Sınıfın skaler
alanları bağın yüküdür; anahtarları Haldus ekler.

<!-- example: payload-link -->
```csharp
app.MapCrud<Supplier>();
app.MapCrud<Product>();
app.MapAssociation<Supplier, Product>();

public partial class Supplier
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class SupplierProduct
{
    [Range(0.0, 1_000_000.0)]
    public decimal Cost { get; set; }

    [Range(0, 365)]
    public int LeadTimeDays { get; set; }
}
```

Yük taşıyan bağın uçları değişir:

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `PUT` | `/suppliers/{supplierId}/products/{productId}` | Bağı yüküyle kurar veya yükünü değiştirir; bağı döndürür (`200`). |
| `GET` | `/suppliers/{supplierId}/products/{productId}` | Bağı yüküyle okur; yoksa `404`. |
| `DELETE` | `/suppliers/{supplierId}/products/{productId}` | Bağı kaldırır. |
| `GET` | `/suppliers/{supplierId}/products` | Tedarikçinin ürünlerini listeler. |
| `GET` | `/products/{productId}/suppliers` | Ürünün tedarikçilerini listeler. |
| `GET` | `/suppliers/{supplierId}/products/links` | Bağları yükleriyle listeler. |
| `GET` | `/products/{productId}/suppliers/links` | Aynısı, öteki yönden. |

```http
PUT /suppliers/0199a3c8-0a00-7c10-8a10-0a1b2c3d4e50/products/0199a3c8-1b00-7d20-9b20-1b2c3d4e5f60
Content-Type: application/json

{ "cost": 12.5, "leadTimeDays": 7 }
```

```http
HTTP/1.1 200 OK

{
  "supplierId": "0199a3c8-0a00-7c10-8a10-0a1b2c3d4e50",
  "productId": "0199a3c8-1b00-7d20-9b20-1b2c3d4e5f60",
  "cost": 12.5,
  "leadTimeDays": 7
}
```

- Bağ tektir: aynı çifte yeniden `PUT` göndermek yeni bir bağ kurmaz, yükü değiştirir.
- Yükün doğrulama kuralları istekte çalışır; kurala uymayan istek `400` döner.
- Yükü olan bağ topluca değiştirilemez; `PUT /suppliers/{supplierId}/products` uçu üretilmez.

## Durumu olan bağ

Bağın bir durumu olabilir: önerilen bir tedarik onaylanır veya sonlandırılır, davet edilen bir üye etkinleşir. Durum
bağın zincirinde `.Workflow<TStatus>()` ile bildirilir; geçişler bağın builder'ında gövdesiz `MapTransition` ile
yazılır. Bağ sınıfını yazmak zorunlu değildir; yazarsanız alanları yine yüktür.

<!-- example: link-state -->
```csharp
app.MapCrud<Organization>();
app.MapCrud<User>();

var memberships = app.MapAssociation<Organization, User>()
    .Workflow<MembershipStatus>(initial: MembershipStatus.Invited);

memberships.MapTransition("activate", from: MembershipStatus.Invited, to: MembershipStatus.Active);
memberships.MapTransition("suspend", from: [MembershipStatus.Invited, MembershipStatus.Active], to: MembershipStatus.Suspended);

public enum MembershipStatus
{
    Invited,
    Active,
    Suspended,
}

public partial class Organization
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class User
{
    [MaxLength(200)]
    public string Email { get; set; } = "";
}

public partial class OrganizationUser
{
    [MaxLength(50)]
    public string Role { get; set; } = "member";
}
```

Haldus bağa `Status` alanını ekler ve her geçiş için bir uç üretir:

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/organizations/{organizationId}/users/{userId}/activate` | `Invited` durumundaki bağı `Active` yapar; bağı döndürür. |
| `POST` | `/organizations/{organizationId}/users/{userId}/suspend` | `Invited` veya `Active` bağı `Suspended` yapar. |
| `GET` | `/organizations/{organizationId}/users/links?status=Active` | Bağları duruma göre süzer (öteki yönden de). |

```http
POST /organizations/0199a3c9-0a00-7c10-8a10-0a1b2c3d4e50/users/0199a3c9-1b00-7d20-9b20-1b2c3d4e5f60/activate
```

```http
HTTP/1.1 200 OK

{
  "organizationId": "0199a3c9-0a00-7c10-8a10-0a1b2c3d4e50",
  "userId": "0199a3c9-1b00-7d20-9b20-1b2c3d4e5f60",
  "role": "admin",
  "status": "Active"
}
```

**Kurallar ve hata yanıtları**

- Yeni bağ başlangıç durumunda doğar. `Status` istekle yazılamaz; `PUT` gövdesinde gönderilse de yok sayılır. Yükü
  yeniden yazmak durumu başa döndürmez.
- Bağ sayılan durumlardan birinde değilse geçiş `409 Conflict` döner. Olmayan bir bağın geçişi `404` döner.
- Durum veritabanında ve yanıtta adıyla durur (`"status": "Active"`).
- Aynı bağın durumunu aynı anda değiştiren iki istekten ikincisi `409` alır ("The state of the record changed while
  this request was being processed."); istemci bağı yeniden okuyup dener.
- Durumu olan ama yükü olmayan bir bağ da gövdesiz `PUT` ile kurulur, ama `204` yerine bağı döndürür (`200`).
- Durumu olan bağ topluca değiştirilemez.
- `Workflow` doğrudan `MapAssociation` çağrısına zincirlenmelidir; değişken üzerinden çağrılırsa `HALDUS004` verilir.
  `MapTransition` durum makinesi olmayan bir bağda `HALDUS007`, durum tipinde olmayan bir durumla `HALDUS010` verir.

Bağın geçişi iş mantığı taşımaz (istek tipi, lambda, handler yoktur) ve bağın `EditableIn` / `DeletableIn` kuralı
yoktur: yük her durumda yazılır, bağ her durumda kaldırılır. Kaynakların durum makineleri için
[Durum makineleri](state-machines.md) sayfasına bakın.

## Kullanıcıya bağlı bağ

`MapAssociation<TLink, T>().ForCurrentUser()` geçerli kullanıcıyı bir kayda bağlar: favori, yıldız, izleme listesi.
İlk tip argümanı bir uç değil, üretilecek bağ entity'sinin adıdır (`Favorite`). Bu sınıfı yazmazsınız; Haldus onu
çağrının bulunduğu namespace'e üretir.

<!-- example: favorites -->
```csharp
app.MapCrud<Product>();
app.MapAssociation<Favorite, Product>().ForCurrentUser();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}
```

**Üretilen uçlar**

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/me/favorites` | Kullanıcının favori ürünlerini listeler (sayfalı). |
| `GET` | `/me/favorites/{productId}` | Ürün favorideyse `204`, değilse `404`. |
| `PUT` | `/me/favorites/{productId}` | Ürünü favorilere ekler; yinelenebilir, `204` döner. |
| `DELETE` | `/me/favorites/{productId}` | Ürünü favorilerden çıkarır; yoksa `404`. |

```http
GET /me/favorites
Authorization: Bearer …
```

```http
HTTP/1.1 200 OK

{
  "items": [ { "id": "0199a3ca-0a00-7c10-8a10-0a1b2c3d4e50", "name": "Mekanik klavye", "price": 1450 } ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

**Kurallar ve hata yanıtları**

- Liste bağları değil, bağlı kayıtları döndürür.
- Adreste kullanıcı yer almaz; her kullanıcı yalnızca kendi bağlarını görür ve kaldırır. Başkasının favorisi bulunamaz
  sayılır.
- Kullanıcısı çözülemeyen istek `401 Unauthorized` alır. Ürün yoksa veya görünmüyorsa `PUT` `404` döner.
- Ürün silinince ona kurulmuş bütün favoriler de silinir: bağ yalnızca bir işarettir ve kimsenin işareti silmeyi
  engellememelidir.
- Kullanıcının kim olduğu token'daki claim'den okunur; bkz. [Kiracılar ve sahiplik](tenancy-and-ownership.md#kullanıcıya-ait-kaynaklar).

Kullanıcıya bağlı bağ yük ve durum taşımaz; `.Workflow()` ile birlikte yazılamaz (`HALDUS007`). Değer taşıyan bir oy
bu yüzden bu bildirimle yazılamaz.

## Tip ve örnek

`.TypedBy<TType>()` kaydı bir tip kaydına bağlar: cihaz ve cihaz modeli, ürün ve ürün tipi. Tip varsayılanları taşır,
kayıt yalnızca farklı olanı yazar. Haldus tipe zorunlu bir referans (`DeviceModelId`) üretir.

<!-- example: typed-by -->
```csharp
app.MapCrud<DeviceModel>();
app.MapCrud<Device>().TypedBy<DeviceModel>();

public partial class DeviceModel
{
    [MaxLength(100)]
    public string Name { get; set; } = "";

    [Range(0, 120)]
    public int WarrantyMonths { get; set; }

    [MaxLength(200)]
    public string? Manual { get; set; }
}

public partial class Device
{
    [MaxLength(50)]
    public string SerialNumber { get; set; } = "";

    [Range(0, 120)]
    public int? WarrantyMonths { get; set; }

    [MaxLength(200)]
    public string? Manual { get; set; }
}
```

Kayıtta boş bırakılabilen ve tipte aynı adla, aynı değer tipiyle bulunan alanlar (`WarrantyMonths`, `Manual`)
boş bırakıldıklarında tipten doldurulur:

```http
POST /devices
Content-Type: application/json

{ "serialNumber": "SN-1001", "deviceModelId": "0199a3cb-0a00-7c10-8a10-0a1b2c3d4e50" }
```

```http
HTTP/1.1 201 Created

{
  "id": "0199a3cb-1b00-7d20-9b20-1b2c3d4e5f60",
  "serialNumber": "SN-1001",
  "warrantyMonths": 24,
  "manual": "x200.pdf",
  "deviceModelId": "0199a3cb-0a00-7c10-8a10-0a1b2c3d4e50"
}
```

**Kurallar ve hata yanıtları**

- Değer **yazılırken** devralınır ve kayıtta saklanır. Tipin varsayılanı sonradan değişirse var olan kayıtlar kendi
  değerini korur; yeni kayıtlar yeni değeri alır.
- Devralma her yazmada (oluşturma, `PUT`, `PATCH`, toplu uçlar, kopya, önizleme, geçiş ve aksiyon) ve hesaplanan
  alanlardan (`Compute()`) önce yapılır. Bir alanı `null` yaparak boşaltmak onu yeniden tipe bağlar.
- Tip zorunludur ve var olmalıdır; yoksa `400` döner, hata `deviceModelId` alanına yazılır.
- Tip yalnızca oluştururken yazılır. `PUT` ve `PATCH` isteklerinde yer almaz; gönderilse de yok sayılır.
- Kayıtları olan tip silinemez: `409 Conflict`.
- Tipin hassas alanları (`Sensitive`, `Encrypted`) iki yönde de devralınmaz; gizli bir değer kaydın yanıtına taşınmasın
  diye.
- Devralma uçlara bağlıdır: uygulamanın kendi kodu kaydı doğrudan bağlamla yazarsa alanlar doldurulmaz.

Bir kaydın yalnızca bir tipi olur. `TypedBy` `MapCrud`, `MapReadOnly` ve `MapAppendOnly` ile birleşir; tip aynı projede
bir `Map*` çağrısıyla eşlenmiş olmalıdır (`HALDUS007`).

## Rol

`.RoleOf<TPlayer>()` kaydı bir oyuncunun rolü yapar: bir kişinin çalışan, müşteri veya tedarikçi rolü. Haldus oyuncuya
zorunlu, değişmeyen bir referans (`PersonId`) üretir ve onu kaydın doğal anahtarı yapar: bir kişinin bu rolden en çok
bir kaydı olur.

<!-- example: role-of -->
```csharp
app.MapCrud<Person>();
app.MapCrud<StaffMember>().RoleOf<Person>();

public partial class Person
{
    [MaxLength(200)]
    public string FullName { get; set; } = "";
}

public partial class StaffMember
{
    [MaxLength(100)]
    public string? Department { get; set; }
}
```

Standart uçlara bir okuma ucu eklenir:

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/staff-members/by-person-id/{personId}` | Kişinin çalışan kaydını okur; yoksa `404`. |

```http
POST /staff-members
Content-Type: application/json

{ "personId": "0199a3cc-0a00-7c10-8a10-0a1b2c3d4e50", "department": "Depo" }
```

```http
HTTP/1.1 201 Created

{
  "id": "0199a3cc-1b00-7d20-9b20-1b2c3d4e5f60",
  "department": "Depo",
  "personId": "0199a3cc-0a00-7c10-8a10-0a1b2c3d4e50"
}
```

**Kurallar ve hata yanıtları**

- Aynı kişi için ikinci bir çalışan kaydı `409 Conflict` döner (`Already exists`). Kaynak `MultiTenant` ise kural kiracı
  başınadır: aynı kişi her kiracıda ayrı bir rol kaydı taşıyabilir.
- Oyuncu var olmalıdır; yoksa `400` döner, hata `personId` alanına yazılır.
- Oyuncu yalnızca oluştururken yazılır; `PUT` ve `PATCH` onu değiştirmez.
- Oyuncu silinince rolü de silinir (`Cascade`). Kiracıya ait bir rol genel bir oyuncuya bağlıysa varsayılan
  `Restrict` olur; bkz. [Zincirleme silme kiracı sınırını aşmaz](#zincirleme-silme-kiracı-sınırını-aşmaz).

**Seçenekler**

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `onDelete` | `OnDelete.Cascade` | Oyuncu silindiğinde rol kaydına ne olacağı. |

Oyuncu rolün doğal anahtarı olduğu için kaynak ayrıca `NaturalKey` bildiremez ve yalnızca bir `RoleOf` taşıyabilir
(`HALDUS007`). Oyuncu aynı projede eşlenmiş olmalıdır.

## Alt tip

`.SubtypeOf<TBase>()` bir kaynağı başka bir kaynağın alt tipi yapar: otomobil ve kamyon birer araçtır. Alt tipin entity
sınıfı üst tipten gerçekten türer. Bütün kayıtlar üst tipin tablosunda, onun anahtarı ve trait'leriyle durur (tek tablo
kalıtımı). Haldus kaydın türünü söyleyen `Kind` alanını ekler; değeri kaynağın kebab-case adıdır (`car`, `truck`).

<!-- example: subtype -->
```csharp
app.MapCrud<Vehicle>().Auditable().SoftDeletable();
app.MapCrud<Car>().SubtypeOf<Vehicle>();
app.MapCrud<Truck>().SubtypeOf<Vehicle>().Sortable(truck => truck.PayloadTons);

public partial class Vehicle
{
    [MaxLength(20)]
    public string Plate { get; set; } = "";
}

public partial class Car : Vehicle
{
    [Range(1, 9)]
    public int Seats { get; set; }
}

public partial class Truck : Vehicle
{
    [Range(0.0, 100.0)]
    public decimal PayloadTons { get; set; }
}
```

**Üretilen uçlar**

| Adres | Ne sunar |
|---|---|
| `/vehicles` | Bütün türleri ortak alanlarıyla. Liste `?kind=truck` ile türe göre süzülür. |
| `/cars` | Yalnızca otomobilleri, üst tipin alanları ve kendi alanlarıyla. |
| `/trucks` | Yalnızca kamyonları. |

Her adres kaynağın standart uçlarını ve üst tipin trait'lerinin uçlarını taşır (burada `restore` ve `purge`).

```http
POST /cars
Content-Type: application/json

{ "plate": "35 ABC 101", "seats": 5 }
```

```http
HTTP/1.1 201 Created

{
  "id": "0199a3cd-0a00-7c10-8a10-0a1b2c3d4e50",
  "plate": "35 ABC 101",
  "seats": 5,
  "createdAt": "2026-11-25T09:30:00.123456+00:00",
  "updatedAt": null,
  "deletedAt": null,
  "kind": "car"
}
```

**Kurallar ve hata yanıtları**

- Bir otomobil `/vehicles/{id}` adresinden de okunur (aynı kimlik, ortak alanlar). Alt tipin adresi yalnızca kendi
  türünü görür: bir kamyonu `/cars/{id}` adresinden okumak veya güncellemek `404` döner.
- Ortak alan iki adresten de yazılır; alt tipin kendi alanı yalnızca kendi adresinden.
- `kind` istekle yazılamaz ve değişmez.
- Silme ve öteki trait'ler üst tipin kuralını izler; yukarıdaki otomobil geri alınabilir biçimde silinir ve
  `/cars/{id}/restore` ile geri gelir.
- Üst tipin adresinden oluşturulan kayıt üst tipin kendisidir (`"kind": "vehicle"`).

**Üst tip ve alt tipin zincirleri**

- Üst tip `MapCrud` ile eşlenmeli ve sunucunun ürettiği `Guid` anahtarı taşımalıdır. Zincirinde yalnızca bütün alt
  tipler için aynı anlama gelen bildirimler yazılır: `MultiTenant`, `Auditable`, `SoftDeletable`, `Archivable`,
  `ConcurrencyChecked`, `Without`, `Only`, `Sortable`, `Searchable`, `Filterable`. Başka bir bildirim (`Workflow`,
  `Revisioned`...) `HALDUS007` verir.
- Alt tip de `MapCrud` ile eşlenir; zincirinde yalnızca `SubtypeOf`, `Without`, `Only`, `Sortable`, `Searchable` ve
  `Filterable` yazılır. Kendi referansı, durumu veya trait'i olamaz.
- Sınıf üst tipten türemelidir (`public partial class Car : Vehicle`); türemiyorsa `HALDUS007` verilir.
- Hiyerarşi tek düzeydir: bir alt tipin alt tipi olamaz. Hiyerarşideki bir kaynak gömülü detay taşıyamaz.

## Silme davranışları

Bir kayıt silindiğinde ona bağlı kayıtlara ne olacağı ilişkinin türüne göre değişir. `OnDelete` seçeneği alan
bildirimlerde varsayılanı değiştirebilirsiniz:

| Değer | Anlamı |
|---|---|
| `OnDelete.Restrict` | Bağlı kayıt varken silme reddedilir: `409 Conflict`. |
| `OnDelete.Cascade` | Bağlı kayıtlar da silinir. |
| `OnDelete.SetNull` | Bağlı kayıtlardaki referans boşaltılır. Yalnızca isteğe bağlı referansta kullanılabilir. |

| Bildirim | Varsayılan | Değiştirilebilir mi |
|---|---|---|
| `References<T>()` | `Restrict` | Evet (`onDelete:`). `SetNull` için `required: false` gerekir; yoksa `HALDUS007`. |
| `Under<T>()` | `Cascade` | Evet. |
| `Hierarchical()` | `Restrict` (çocuğu olan düğüm silinemez) | Hayır. |
| `MapAssociation<TLeft, TRight>()` | Bağlar uçla birlikte silinir | Hayır. |
| `ForCurrentUser()` | Bağlar kayıtla birlikte silinir | Hayır. |
| `TypedBy<T>()` | `Restrict` (kayıtları olan tip silinemez) | Hayır. |
| `RoleOf<T>()` | `Cascade` | Evet (`onDelete:`). |
| `MapDetail<T>()` | Satırlar sahibiyle silinir | Hayır. |
| `CreatedFrom<T>()` | `Restrict` | Evet; bkz. [Kayıtlar arası zincir](record-chains.md#başka-bir-kayıttan-üretmek). |

<!-- example: ondelete -->
```csharp
app.MapCrud<Category>();
app.MapCrud<Product>()
    .References<Category>(onDelete: OnDelete.SetNull, required: false);

public partial class Category
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

Yukarıda kategori silinince ürünler kalır, `categoryId` alanları boşalır.

Veritabanı bir silmeyi reddettiğinde (bağlı kayıt varken `Restrict`) yanıt her zaman `409` olur:

```http
HTTP/1.1 409 Conflict

{
  "title": "Resource is in use",
  "status": 409,
  "detail": "Category '0199a3ce-0a00-7c10-8a10-0a1b2c3d4e50' cannot be deleted because other records refer to it."
}
```

Kaynak `SoftDeletable` ise silme kaydı fiziksel olarak kaldırmadığı için çakışma oluşmaz; çakışma ancak kalıcı silmede
(`purge`) ortaya çıkar. Bir kayda başka bir modülden veya elle yazılmış bir ilişkiden referans verilebileceği için,
bağlı kaydı Haldus'un bilmediği durumlarda da veritabanının reddi `409` olarak bildirilir.

### Zincirleme silme kiracı sınırını aşmaz

Genel bir kayıt (kiracıya ait olmayan) silindiğinde, kiracılara ait kayıtlar kendiliğinden silinmez. Aksi halde genel
kaydı silen kişi başka kiracıların verisini yok etmiş olurdu. Bu yüzden varsayılanı `Cascade` olan ilişkilerde, bağlı
kayıt kiracıya aitse ve hedef genelse varsayılan `Restrict` olur:

- `Under<T>()`: kiracıya ait kayıtlar genel bir ebeveynin altındaysa ebeveynin silinmesi `409` döner.
- `RoleOf<T>()`: kiracıya ait bir rol genel bir oyuncuya bağlıysa oyuncunun silinmesi `409` döner.
- `MapAssociation`: genel bir uç ile kiracıya ait bir uç arasındaki bağda genel ucun silinmesi `409` döner. Kiracı kendi
  kaydını kalıcı silince bağ da gider ve genel uç yeniden silinebilir.

Bir kaynak kendisi `MultiTenant` ise veya `MultiTenant` bir ebeveynin altındaysa kiracıya ait sayılır.

<!-- example: tenant-boundary -->
```csharp
app.MapCrud<Marketplace>();

// Varsayılan: pazaryerinin, kiracılara ait ilanları varken silinmesi 409 döner.
app.MapCrud<Listing>().MultiTenant().Under<Marketplace>();

// Açık seçim: pazaryeri silinince bütün kiracıların teklifleri de silinir.
app.MapCrud<Offer>().MultiTenant().Under<Marketplace>(OnDelete.Cascade);

public partial class Marketplace
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}

public partial class Listing
{
    [MaxLength(200)]
    public string Title { get; set; } = "";
}

public partial class Offer
{
    public decimal Price { get; set; }
}
```

Zincirleme silmeyi kiracı sınırının ötesine taşımak istiyorsanız `OnDelete.Cascade` değerini açıkça yazarsınız; o
zaman karar sizindir. Kural yalnızca kiracılar içindir: [kullanıcıya ait](tenancy-and-ownership.md#kullanıcıya-ait-kaynaklar)
kayıtlar ortak bir ebeveynin altındaysa ebeveynle birlikte silinirler.

## Birlikte kullanım

| Bildirim | Birleşir | Birleşmez (`HALDUS007`) |
|---|---|---|
| `References<T>()` | `MapSingleton` dışındaki archetype'lar; `Searchable`, `Sortable`, `Cloneable`, `Batch`. | `MapSingleton` (kendisi referans veremez); `MapSingleton` ve kiracıya ait `MapStore` hedef olamaz. Aynı hedefe hem `References` hem `CreatedFrom`. |
| `Under<T>()` | `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapSingleton`; `Hierarchical`, `Orderable`, `MultiTenant`. | `MapStore`, `MapLookup`; ikinci bir `Under`; aynı hedeften `CreatedFrom`. Kısa adres `ChangeFeed` ile; tek kayıt `MultiTenant` ve `shallow: true` ile. |
| `Hierarchical()` | `MapCrud`, `MapReadOnly`, `MapAppendOnly`; `Under`, `Orderable`, `SoftDeletable`. | `MapLookup`, `MapStore`, `MapSingleton`. |
| `Embeds<T>()` | Birden çok değer tipi (`.Embeds<Money>().Embeds<Dimensions>()`). | Nullable değer alanı; eşleşen alanı olmayan tip; `Searchable` içinde gömülü alan (`HALDUS005`). |
| `MapAssociation` | Kiracıya ait, geri alınabilir biçimde silinen uçlar. | Kendisiyle bağ; `MapSingleton` veya kiracıya ait `MapStore` uç; `/me` altındaki bir kaynak uç. |
| `ForCurrentUser()` | — | `Workflow`; yük. |
| `TypedBy<T>()` | `MapCrud`, `MapReadOnly`, `MapAppendOnly`; `Compute()`, `Batch`, `Cloneable`. | İkinci bir `TypedBy`; eşlenmemiş tip. |
| `RoleOf<T>()` | `MapCrud`, `MapReadOnly`, `MapAppendOnly`; `MultiTenant`, `Numbered`. | `NaturalKey`; ikinci bir `RoleOf`; eşlenmemiş oyuncu. |
| `SubtypeOf<T>()` | Yukarıda sayılan okuma seçenekleri. | `MapCrud` dışındaki archetype'lar; alt tipte trait; gömülü detay; iki düzey. |

Eşlenmemiş bir hedefe verilen referans, ebeveyn veya bağ ucu `HALDUS009` verir. İlişki bildirimleri doğrudan `Map*`
çağrısına zincirlenmelidir; değişken üzerinden yazılırsa `HALDUS004` verilir. Tanıların açıklaması
[Tanılar](../reference/diagnostics.md) sayfasındadır.

## Sınırlar

- `References` liste ucuna yabancı anahtarla süzen bir parametre eklemez; süzme için `Filterable` veya `Searchable`
  kullanılır.
- Bağ listelerinin sıralama ve süzme seçeneği yoktur (durumu olan bağda `?status=` dışında); bağlı kayıtlar
  anahtarlarına göre sıralı döner.
- Bağların topluca değiştirilmesi kilitsizdir: aynı ucu aynı anda değiştiren iki isteğin sonucu ikisinin birleşimi
  olabilir.
- Bağın durumunda `EditableIn` / `DeletableIn` benzeri kurallar ve geçiş mantığı yoktur; bağın uçları idempotency ve
  token ile erişim almaz.
- Sağ ucun anahtarı metinse (`MapLookup`), `links` kodlu bir kayıt `…/links` adresiyle çakışır.
- Kullanıcıya bağlı bağ yük ve durum taşımaz.
- `Under` başka bir modülün kaynağını ebeveyn alabilir, ama ikisi aynı veritabanı bağlamında olmalıdır; değilse uygulama
  model kurulurken durur. `MapAssociation`'ın iki ucu aynı modülde eşlenmelidir.
- Tip-örnekte "tip örneği kısıtlar" yalnızca şu kadardır: tip zorunludur, değişmez ve kayıtları varken silinemez.
  Tipin varsayılanı değişince var olan kayıtlar güncellenmez.
- Alt tip hiyerarşisi tek düzeydir; alt tipin alanları tabloda boş bırakılabilir kolonlardır. Başka bir kaynağın
  `Counts<Üst>()` toplamı alt tiplerin kayıtlarını saymaz.
- Sahiplik silme davranışını değiştirmez: ortak bir ebeveyn silinince altındaki, başka kullanıcılara ait kayıtlar da
  silinir.

## Ayrıca bakın

- [Detaylar ve hesaplanan alanlar](details-and-computed-fields.md): sahibine ait satırlar (`MapDetail`).
- [Toplanan alanlar](rollups.md): bağlı kayıtların sayısı ve toplamı.
- [Kayıtlar arası zincir](record-chains.md): başka bir kayıttan üretilen kayıtlar.
- [Kiracılar ve sahiplik](tenancy-and-ownership.md)
- [Dosyalar](files.md#takılabilir-kaynaklar): birden çok hedefe takılabilen kaynaklar (`AttachedTo`).
- [Modüler yapı](modules.md): modüller arası referanslar.
- [Bildirim dizini](../reference/declarations.md)
