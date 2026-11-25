# Alanlar ve anahtarlar

Bir entity'nin alanlarını siz yazarsınız; Haldus onlardan istek ve yanıt tiplerini üretir, doğrulama kurallarını taşır
ve bazı alanları kendisi ekleyip doldurur. Bu sayfa alanlarla ilgili her şeyi anlatır: doğrulama, yalnızca yanıtta
görünen alanlar, oluşturma ve değiştirme bilgisi (`Auditable`), yalnızca oluştururken yazılan alanlar (`CreateOnly`),
anahtarın biçimi (`KeyStrategy`), okunabilir numaralar (`Numbered`), serbest anahtar-değer çantası (`Metadata`),
kiracının tanımladığı alanlar (`CustomFields`) ve başka dillere çeviriler (`Translatable`).

Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. `builder` ve `app` satırları yoksa `Program.cs`'in geri
kalanı yazılmış sayılır.

## İçindekiler

- [Doğrulama](#doğrulama)
- [Yalnızca yanıtta görünen alanlar](#yalnızca-yanıtta-görünen-alanlar)
- [Denetim alanları](#denetim-alanları)
- [Yalnızca oluştururken yazılan alanlar](#yalnızca-oluştururken-yazılan-alanlar)
- [Anahtar biçimi](#anahtar-biçimi)
- [Numaralar](#numaralar)
- [Metadata](#metadata)
- [Özel alanlar](#özel-alanlar)
- [Çeviriler](#çeviriler)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Doğrulama

Entity'nin alanlarına yazdığınız DataAnnotations kuralları üretilen istek tiplerine (`CreateCustomerRequest`,
`UpdateCustomerRequest`, `PatchCustomerRequest`) kopyalanır ve her yazmada, kayda dokunulmadan önce denetlenir.

<!-- example: validation -->
```csharp
app.MapCrud<Customer>();

public partial class Customer
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [EmailAddress]
    [MaxLength(320)]
    public string? Email { get; set; }

    [Range(0, 1_000_000)]
    public int LoyaltyPoints { get; set; }
}
```

Kurala uymayan istek 400 alır. Hatalar alan düzeyindedir: `errors` nesnesinin anahtarları alanların camelCase adıdır.

```http
POST /customers
Content-Type: application/json

{ "name": "", "email": "ada-at-example", "loyaltyPoints": 5 }
```

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "name": ["The Name field is required."],
    "email": ["The Email field is not in a valid format."]
  }
}
```

Mesajlar `Accept-Language` başlığının dilindedir (`en` varsayılan, `tr`). Türkçe yanıt: `"Name alanı zorunludur."`.

### Kurallar

- Haldus'un kendi mesajlarıyla çevrilen kurallar: `[Required]`, `[MaxLength]`, `[MinLength]`, `[StringLength]`,
  `[Range]` ve biçim denetleyen `[EmailAddress]`, `[Url]`, `[Phone]`, `[RegularExpression]`. Başka bir attribute kendi
  mesajını verir. Attribute'a `ErrorMessage` yazdıysanız o mesaja dokunulmaz.
- Boş olamayan bir referans alanı (`string Name`, `?` yok) kendiliğinden zorunlu sayılır: istekte hiç gönderilmezse 400
  alır. Boş metne (`""`) ise izin verilir. Boş metni de reddetmek için `[Required]` yazın; o zaman yalnızca boşluktan
  oluşan metin de reddedilir.
- `PATCH` yalnızca gönderilen alanları doğrular. Boş olamayan bir alana açıkça `null` gönderilirse `"The Name field is
  required."` hatası döner; nullable bir alana `null` göndermek onu boşaltır.
- Gömülü değer nesnelerinin ve satırların hataları yollarıyla adlandırılır: `address.city`, `orderItems[1].quantity`.
  Özel alanların hataları `customFields.{anahtar}`, toplu uçlardaki öğelerinki `items[2].name` biçimindedir.
- İstek gövdesinde tipin tanımadığı alanlar yok sayılır. Bir alanın JSON değeri tipine uymuyorsa (`"price": "abc"`) istek
  yine 400 alır.
- Kurallar Haldus'un kendi uç filtresinde çalışır; .NET 10'un `AddValidation` generator'ı başka bir generator'ın
  ürettiği tipleri göremez. Siz bir şey kaydetmezsiniz.

Referans alanlarının (`categoryId`) var olmayan bir kaydı göstermesi de alan düzeyinde 400'dür; bkz.
[referans](relationships.md#referans).

## Yalnızca yanıtta görünen alanlar

Herkese açık `set` erişimcisi olmayan bir alan isteklerde yer almaz, yalnızca yanıtta görünür. Değerini sizin kodunuz
(bir aksiyon, bir geçiş, `Compute()`) yazar; alan veritabanında saklanır.

<!-- example: response-only -->
```csharp
var coupons = app.MapCrud<Coupon>();

coupons.MapAction("redeem", coupon => coupon.Redeem());

public partial class Coupon
{
    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = "";

    [Range(1, 90)]
    public int Percent { get; set; }

    public int TimesRedeemed { get; private set; }

    public void Redeem() => TimesRedeemed++;
}
```

`POST /coupons` ve `PUT /coupons/{id}` yalnızca `code` ve `percent` alır; `timesRedeemed` yanıtta görünür ve yalnızca
`POST /coupons/{id}/redeem` ile artar. Gövdede gönderilse de yok sayılır.

Haldus'un ürettiği alanlar (`id`, `createdAt`, `status`, `number`...) da aynı kuraldadır: yanıtta görünürler, istekle
yazılamazlar. Kiracı (`TenantId`) ve sahip (`OwnerId`) alanları ne istekte ne yanıtta yer alır. `Sensitive` ve
`Encrypted` alanlar ise tersidir: istekte yazılır, hiçbir yanıtta görünmez ([hassas veriler](sensitive-data.md)).

## Denetim alanları

`.Auditable()` kayda oluşturulma ve son değiştirilme zamanını ekler. `trackUser: true` ile oluşturan ve son değiştiren
kullanıcı da tutulur.

<!-- example: auditable -->
```csharp
app.MapCrud<Customer>()
    .Auditable(trackUser: true);

public partial class Customer
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

| Alan | Tipi | Ne zaman yazılır |
|---|---|---|
| `createdAt` | `DateTimeOffset` | Kayıt eklenirken. |
| `updatedAt` | `DateTimeOffset?` | Kayıt her değiştiğinde. Hiç değişmediyse `null`. |
| `createdBy` | `string?` | `trackUser: true` ile; kayıt eklenirken, geçerli kullanıcının kimliği. |
| `updatedBy` | `string?` | `trackUser: true` ile; kayıt her değiştiğinde. |

```json
{
  "id": "019a8f60-2a3b-7c4d-9e5f-6a7b8c9d0e1f",
  "name": "Ada Lovelace",
  "createdAt": "2026-11-25T09:30:12.345678+00:00",
  "updatedAt": "2026-11-25T10:02:41.120934+00:00",
  "createdBy": "alice",
  "updatedBy": "bob"
}
```

### Kurallar

- Alanları kaydetme sırasında Haldus yazar (bir EF Core interceptor'ı). Kaydı hangi kod yazarsa yazsın (üretilen uç, bir
  aksiyon, bir arka plan işi, uygulamanın kendi kodu) doldurulurlar. İstekle yazılamazlar.
- Zaman `TimeProvider`'dan, UTC olarak alınır ve mikrosaniyeye kırpılır (PostgreSQL daha hassasını saklamaz).
- Kullanıcı, kullanıcı claim'inden okunur (`HaldusOptions.UserClaimType`, varsayılan `NameIdentifier`). Kullanıcısı
  çözülemeyen bir yazmada `createdBy` / `updatedBy` `null` olur.
- Geri alınabilir silme, arşivleme ve `ReadTracking` ile okundu işaretlemek de birer değişikliktir ve `updatedAt`
  alanını ilerletir. Hiçbir alanı değiştirmeyen bir yazma değişiklik sayılmaz.
- Alanlar aranabilir ve sıralanabilir: `.Sortable(c => c.CreatedAt)`, `.Searchable(c => c.UpdatedBy)`.

`Retention(...)` ve `MapFile` kendiliğinden `Auditable` ekler.

| Seçenek | Varsayılan | Anlamı |
|---|---|---|
| `trackUser` | `false` | Oluşturan ve son değiştiren kullanıcı da tutulsun mu. |

## Yalnızca oluştururken yazılan alanlar

Bazı alanlar kayıt oluşturulduktan sonra anlamını değiştirmemelidir: dağıtılmış bir kuponun kodu ve indirim oranı, bir
sözleşmenin tarafı. `.CreateOnly(...)` sayılan alanları yalnızca `POST` isteğine koyar.

<!-- example: create-only -->
```csharp
app.MapCrud<Coupon>()
    .CreateOnly(coupon => coupon.Code, coupon => coupon.Percent);

public partial class Coupon
{
    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = "";

    [Range(1, 90)]
    public int Percent { get; set; }

    [Range(1, 1_000_000)]
    public int? MaxRedemptions { get; set; }
}
```

| İstek | Yazılabilen alanlar |
|---|---|
| `POST /coupons` | `code`, `percent`, `maxRedemptions` |
| `PUT /coupons/{id}` | `maxRedemptions` |
| `PATCH /coupons/{id}` | `maxRedemptions` |

```http
PUT /coupons/019a8f52-1d4e-7a63-8c5f-4b8e3d0f2a93
Content-Type: application/json

{ "code": "OTHER", "percent": 90, "maxRedemptions": 5 }
```

```json
{
  "id": "019a8f52-1d4e-7a63-8c5f-4b8e3d0f2a93",
  "code": "SPRING-25",
  "percent": 25,
  "maxRedemptions": 5
}
```

`code` ve `percent` güncelleme isteğinin tipinde yoktur; gönderilseler de yok sayılırlar ve kayıt değişmez.

### Kurallar

- Sayılan alan entity'nin yazılabilir bir alanı olmalıdır. Üretilen bir yabancı anahtar da sayılabilir
  (`.CreateOnly(c => c.CategoryId)`); o zaman referans oluşturulduktan sonra değişmez ve güncellemede yeniden
  doğrulanmaz. Yazılabilir olmayan veya alanın kendisi olmayan bir ifade `HALDUS010` olur.
- Yalnızca `MapCrud` ile (ve ayrı uçlu detayla) birleşir. Kaynakta oluşturma ya da `PUT` / `PATCH` işlemlerinden hiçbiri
  yoksa bildirimin etkisi olmaz ve proje derlenmez (`HALDUS007`).
- Kopya (`Cloneable`) isteği bu alanları taşır: kopya yeni bir kayıttır, kodu değiştirilerek kopyalanabilir.
- Doğal anahtarla birlikte sık kullanılır: `.NaturalKey(c => c.Code).CreateOnly(c => c.Code)`
  ([doğal anahtar](querying.md#doğal-anahtar)).

## Anahtar biçimi

`MapCrud`, `MapReadOnly` ve `MapAppendOnly` kayıtlarının anahtarını (`Id`) varsayılan olarak sunucu, zamana göre sıralı
bir GUID (v7) olarak üretir. `.KeyStrategy(...)` başka bir biçim seçer.

| Biçim | Örnek | Tipi | Kim üretir |
|---|---|---|---|
| `KeyStrategy.Sequential` (varsayılan) | `019a8f60-2a3b-7c4d-9e5f-6a7b8c9d0e1f` | `Guid` (v7) | Sunucu |
| `KeyStrategy.Random` | `3f2b8c1e-9a4d-4e6f-8b2a-7c5d1e9f0a3b` | `Guid` (v4) | Sunucu |
| `KeyStrategy.Prefixed` | `ord_01kaxr3m7c9p2t5v8w0yzq4hn6` | `string` | Sunucu |
| `KeyStrategy.Ulid` | `01KAXR3M7C9P2T5V8W0YZQ4HN6` | `string` | Sunucu |
| `KeyStrategy.Client` | `POST /enrollments?id=...` | `Guid` | İstemci |

<!-- example: keys -->
```csharp
app.MapCrud<Order>().KeyStrategy(KeyStrategy.Prefixed, "ord");
app.MapCrud<Parcel>().KeyStrategy(KeyStrategy.Ulid);
app.MapCrud<Note>().KeyStrategy(KeyStrategy.Random);
app.MapCrud<Enrollment>().KeyStrategy(KeyStrategy.Client);

public partial class Order
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";
}

public partial class Parcel
{
    [Required]
    [MaxLength(200)]
    public string Recipient { get; set; } = "";
}

public partial class Note
{
    [MaxLength(2000)]
    public string Text { get; set; } = "";
}

public partial class Enrollment
{
    [Required]
    [MaxLength(200)]
    public string Course { get; set; } = "";
}
```

### Sıralı ve rastgele GUID

Varsayılan v7 GUID, indeksleri sırayla doldurur ve kabaca oluşturulma sırasıyla sıralanır; buna karşılık kaydın ne
zaman oluşturulduğunu ele verir. `Random` (v4) bunu sızdırmaz. İkisinin de adresi aynıdır: `/notes/{id}`.

### Önekli kimlik

`Prefixed`, önek + alt çizgi + 26 karakterlik bir gövdedir: `ord_01kaxr3m7c9p2t5v8w0yzq4hn6`. Gövde, v7 GUID'in küçük
harfli Crockford base32 yazımıdır; metin sırası oluşturulma sırasıdır. Kaydın türü kimliğinden anlaşılır.

- Önek bir metin sabiti olmalıdır: küçük harfle başlar, küçük harf ve rakamlardan oluşur, en çok 12 karakterdir; değilse
  `HALDUS010`.
- Anahtar `string` olur ve adreste, altındaki kaynakların adresinde ve yabancı anahtarlarında aynen taşınır:
  `/orders/ord_01kaxr3m7c9p2t5v8w0yzq4hn6`.
- Uygulamanın kendi kodunun eklediği, anahtarı boş kayıtlar da aynı biçimi alır.

### ULID

`Ulid`, 26 karakterlik, büyük harfli Crockford base32 bir metindir (48 bit zaman, 80 bit rastgele): `01KAXR3M7C9P2T5V8W0YZQ4HN6`.
Metin sırası oluşturulma sırasıdır; aynı milisaniyede üretilen iki kimliğin sırası rastgeledir. Uygulamanın kendi
eklediği kayıtlar da ULID alır.

### İstemcinin verdiği kimlik

`Client` ile kimliği istemci, oluşturma isteğinin sorgusunda verir (gövdede değil). İstemci kaydın kimliğini
göndermeden önce bilir; yinelenen istek ikinci bir kayıt yaratmaz.

```http
POST /enrollments?id=7d1c2b9e-4f3a-4c8d-9e2b-1a6f5c3d8e07
Content-Type: application/json

{ "course": "Cebir" }
```

```http
HTTP/1.1 201 Created
Location: /enrollments/7d1c2b9e-4f3a-4c8d-9e2b-1a6f5c3d8e07
```

| Durum | Yanıt |
|---|---|
| `id` yok veya boş GUID | 400 |
| Aynı kimlikle ikinci oluşturma | 409; var olan kayda dokunulmaz. |

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Already exists",
  "status": 409,
  "detail": "Another Enrollment already has the id '7d1c2b9e-4f3a-4c8d-9e2b-1a6f5c3d8e07'."
}
```

Kimlik tablo genelinde benzersizdir. "Bu kimlik alınmış" yanıtı, isteği yapanın göremediği bir kaydın var olduğunu
söylerdi; bu yüzden `Client`, kayıtları herkese görünmeyen kaynaklarla birleşmez: `MultiTenant`, `OwnedByCurrentUser`,
`Under<T>()`, `AttachedTo<T>()`, `TokenOnly`, `SoftDeletable`, `Expiring`, `SubtypeOf<T>()` (`HALDUS007`). Kimliği
istemcinin vermediği kayıt oluşturan yollarla da birleşmez: `Batch`, `Cloneable`, `CreatedFrom`, `Reversible`,
`Supersedes`, `Document`, `Publishable`. Öyle kaynaklarda yinelenen isteğe karşı
[`Idempotent()`](lifecycle.md#tekrara-karşı-koruma) kullanın.

### Kurallar

- `KeyStrategy` yalnızca `MapCrud`, `MapReadOnly` ve `MapAppendOnly` ile yazılır (`HALDUS007`). `MapStore`,
  `MapLookup` ve `MapBalance` anahtarını zaten istemci verir; işin (`MapJob`) kimliği her zaman GUID'dir.
- `Id` alanını kendiniz yazdıysanız tipi biçime uymalıdır: `Prefixed` ve `Ulid` için `string`, öteki biçimler için
  `Guid`; değilse `HALDUS007`. `Prefixed` dışındaki bir biçime önek vermek de `HALDUS007` olur.
- Gömülü detay satırlarının kimliği her zaman v7 GUID'dir.

## Numaralar

`.Numbered(...)` kayda okunabilir, benzersiz bir numara verir: `INV-2026-000042`. Numara `number` alanında durur,
yalnızca yanıtta görünür ve bir kez verildikten sonra değişmez.

<!-- example: numbered -->
```csharp
app.MapCrud<Invoice>()
    .MultiTenant()
    .Numbered("INV-{yyyy}-{seq:6}");

public partial class Invoice
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";

    public decimal Amount { get; set; }
}
```

```json
{
  "id": "019a8f71-3b4c-7d5e-8f6a-7b8c9d0e1f2a",
  "customerName": "Ada Lovelace",
  "amount": 1250,
  "number": "INV-2026-000042"
}
```

### Desen

Desen sabit metin ve şu parçalardan oluşur:

| Parça | Anlamı |
|---|---|
| `{seq}` | Sayaç, dolgusuz: `42`. |
| `{seq:N}` | Sayaç, N haneye sıfırla doldurulmuş (N 1 ile 18 arası): `{seq:6}` → `000042`. |
| `{yyyy}`, `{yy}` | Yıl: `2026`, `26`. |
| `{MM}` | Ay: `03`. |
| `{dd}` | Gün: `07`. |

`{seq}` tam olarak bir kez geçmelidir. Bilinmeyen bir parça, kapanmamış bir süslü parantez veya geçersiz bir dolgu proje
derlenirken `HALDUS010` olur. Varsayılan desen `{seq:6}`'dır. Tarih parçaları numaranın verildiği anın UTC tarihidir.

### Sayacın kapsamı

Varsayılan sayaç boşluksuzdur (`gapless: true`): numara, kayıtla aynı transaction'da, kapsamına ait bir sayaç satırından
alınır. Kayıt kaydedilemezse veya uç hata ile dönerse numara geri verilir; numaralarda boşluk oluşmaz.

Sayaç şu kapsam başına ayrıdır:

- kaynak,
- kiracı (kaynak `MultiTenant` ise),
- desendeki tarih parçaları: `{yyyy}` varsa sayaç her yıl, `{MM}` de varsa her ay yeniden 1'den başlar.

Numara benzersiz bir indeksle korunur; çok kiracılı kaynakta benzersizlik kiracı içindedir. Aynı kapsama aynı anda
yazan istekler sayacı sırayla alır, yani birbirini bekler.

`gapless: false` ile numara bir veritabanı sequence'inden gelir (`{Entity}NumberSequence`). Yazmalar birbirini beklemez,
ama geri alınan işlemler numara yakar (boşluk oluşur) ve sayaç kiracı ya da dönem başına sıfırlanmaz; tarih parçaları
yine numaraya yazılır.

<!-- example: numbered-sequence -->
```csharp
app.MapAppendOnly<Refund>()
    .Numbered("RF-{seq:8}", gapless: false);

public partial class Refund
{
    [Range(0.01, 1_000_000.0)]
    public decimal Amount { get; set; }
}
```

### Numarayı bir durumda vermek

Durum makinesi olan kaynakta numara kayıt oluşturulurken değil, belirli bir duruma geçerken verilebilir: taslak
siparişin numarası yoktur, verildiğinde alır.

<!-- example: numbered-assign-on -->
```csharp
var orders = app.MapCrud<Order>()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)
    .Numbered("ORD-{yyyy}-{seq:6}", assignOn: OrderStatus.Placed);

orders.MapTransition("place", from: OrderStatus.Draft, to: OrderStatus.Placed);

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
```

Taslağın `number` alanı `null`'dır. `POST /orders/{id}/place` numarayı geçişle aynı transaction'da verir. Numarası olan
kayıt o duruma yeniden girerse yeni numara almaz.

`assignOn` bir durum makinesi ister ve o duruma giden bir geçiş olmalıdır; değilse `HALDUS007`.

| Seçenek | Varsayılan | Anlamı |
|---|---|---|
| `pattern` | `"{seq:6}"` | Numara deseni. Derleme zamanında okunan bir metin sabiti olmalıdır. |
| `assignOn` | `null` | Numaranın verildiği durum; verilmezse kayıt oluşturulurken. |
| `gapless` | `true` | `true`: kapsam başına kilitli sayaç, boşluksuz. `false`: veritabanı sequence'i. |

`Numbered` yalnızca `MapCrud` ve `MapAppendOnly` ile birleşir. Sayaç tablosu (`HaldusCounters`) veya sequence EF Core
modelinize eklenir ve migration'larınıza girer; bkz. [veritabanı](database.md).

## Metadata

`.Metadata()` kayda serbest bir anahtar-değer çantası ekler: dış sistemdeki kimlik, içe aktarma partisi, entegrasyon
notu. Değerler metindir ve Haldus anlamlarına bakmaz.

<!-- example: metadata -->
```csharp
app.MapCrud<Product>()
    .Metadata();

public partial class Product
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}
```

```http
POST /products
Content-Type: application/json

{ "name": "Klavye", "price": 100, "metadata": { "source": "import", "batch": "42" } }
```

```json
{
  "id": "019a8f80-4c5d-7e6f-9a7b-8c9d0e1f2a3b",
  "name": "Klavye",
  "price": 100,
  "metadata": { "source": "import", "batch": "42" }
}
```

| İstek | Çantaya etkisi |
|---|---|
| `POST` | Gönderilen çanta yazılır; gönderilmezse boş başlar. Yanıtta çanta her zaman vardır (`{}`). |
| `PUT` | Çantanın tamamını gönderilenle değiştirir. `metadata` gönderilmezse çanta boşaltılır. |
| `PATCH`, `metadata` içinde anahtarlar | Birleştirir: gönderilen anahtar eklenir veya değişir, `null` gönderilen silinir, gönderilmeyen kalır. |
| `PATCH`, `"metadata": null` | Çantayı boşaltır. |
| `PATCH`, `metadata` yok | Çantaya dokunmaz. |

```http
PATCH /products/019a8f80-4c5d-7e6f-9a7b-8c9d0e1f2a3b
Content-Type: application/json

{ "metadata": { "batch": "43", "source": null, "owner": "ops" } }
```

Yanıttaki çanta: `{ "batch": "43", "owner": "ops" }`.

### Sınırlar ve hata yanıtları

En çok 50 anahtar; anahtar 1 ile 40, değer en çok 500 karakter. `PATCH` birleştirdiği için sınır birleşmiş çantaya da
uygulanır: tek tek geçerli iki istek birleşince sınırı aşarsa ikincisi reddedilir ve hiçbir şey kaydedilmez.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "metadata": ["The field Metadata may hold at most 50 entries; keys must be 1 to 40 characters and values at most 500 characters."]
  }
}
```

Çanta veritabanında tek bir jsonb kolonunda durur. `MapCrud`, `MapReadOnly` ve `MapAppendOnly` ile birleşir. Değerlerine
göre aranamaz ve sıralanamaz.

## Özel alanlar

Bazı kaynaklarda alanları uygulamayı yazan değil kullanan belirler: her kiracı makinelerine kendi alanlarını (renk,
ağırlık, kurulum tarihi) eklemek ister. `.CustomFields()` kayda bir `customFields` çantası ekler; çantanın alanlarını
kiracı, kaynağın adresindeki uçlarla tanımlar ve her yazmada değerler bu tanımlara göre doğrulanır.

<!-- example: custom-fields -->
```csharp
app.MapCrud<Machine>()
    .MultiTenant()
    .CustomFields();

public partial class Machine
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

### Tanım uçları

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/machines/custom-fields` | Tanımları listeler (sayfasız). |
| `PUT` | `/machines/custom-fields/{key}` | Bir alanı tanımlar veya tanımını değiştirir; tanımı döndürür (200). |
| `DELETE` | `/machines/custom-fields/{key}` | Tanımı kaldırır (204); yoksa 404. |

```http
PUT /machines/custom-fields/color
Content-Type: application/json

{ "label": "Renk", "type": "Choice", "required": true, "options": ["red", "blue"] }
```

```json
{
  "key": "color",
  "label": "Renk",
  "type": "Choice",
  "required": true,
  "options": ["red", "blue"]
}
```

| Gövde alanı | Kural |
|---|---|
| `label` | Zorunlu, en çok 200 karakter. Kullanıcıya gösterilen ad. |
| `type` | `Text`, `Number`, `Boolean`, `Date` veya `Choice`. |
| `required` | Kayıt bu alan olmadan yazılamaz mı; varsayılan `false`. |
| `options` | Yalnızca `Choice` için ve zorunlu: 1 ile 100 arası seçenek, her biri 1 ile 200 karakter. Başka türde verilirse 400. |

Anahtar (`{key}`) harfle başlar, yalnızca harf, rakam ve alt çizgi içerir, en çok 40 karakterdir; değilse 400 (`key`
alanında). Bir kaynağın en çok 100 tanımı olur.

Tanımlar kaynak tipi ve kiracı başınadır: bir kiracının tanımladığı alan ötekinde yoktur. Kiracıya bağlı olmayan bir
kaynakta tanımlar herkes için ortaktır. Tanım uçlarını kimin çağırabileceği uygulamanızın yetki kurallarına bağlıdır.

### Değerler

Değerler kaydın `customFields` nesnesinde, tanımın anahtarıyla yazılır:

```http
POST /machines
Content-Type: application/json

{
  "name": "Matkap",
  "customFields": { "color": "red", "weight": 2.5, "installedOn": "2026-06-01", "fragile": false }
}
```

| Tür | Kabul edilen JSON değeri |
|---|---|
| `Text` | Metin, en çok 2000 karakter. |
| `Number` | Sayı (`2.5`). Tırnaklı sayı (`"2.5"`) reddedilir. |
| `Boolean` | `true` veya `false`. |
| `Date` | `yyyy-MM-dd` biçiminde metin: `"2026-06-01"`. |
| `Choice` | Tanımdaki seçeneklerden biri, birebir aynı yazımla. |

Tanımı olmayan anahtar, türüne uymayan değer ve eksik zorunlu alan alan düzeyinde 400 olur; her hata
`customFields.{anahtar}` adını taşır:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "customFields.color": ["The value is not a valid choice."],
    "customFields.serial": ["No custom field with this key is defined."]
  }
}
```

- `PATCH` çantayı birleştirir: gönderilen anahtar değişir, `null` gönderilen silinir, gönderilmeyen kalır. Zorunlu bir
  alan `null` ile silinemez. Çantaya dokunmayan bir `PATCH` onu doğrulamaz.
- `PUT` çantanın tamamını değiştirir; zorunlu alanlar her `PUT`'ta gönderilmelidir.
- Tanımı kaldırılan alanın değeri kaydın bir sonraki yazmasında düşer ve o anahtar artık yazılamaz.
- Tanım değişince var olan kayıtlar yeniden doğrulanmaz; yeni kural kaydın bir sonraki yazmasında uygulanır.
- Kopya, yayın ve geri yükleme gibi aynı kaynağın başka bir kaydından doğan kayıtlar değerleri doğrulamadan taşır.
- Özel alanlar kaydın içeriğidir: onaya bağlı bir kaynakta değişmeleri toplanan onayları düşürür.

`CustomFields` yalnızca `MapCrud` ile birleşir. Tanımlar tek bir adreste yönetildiği için `Under<T>()` ve
`AttachedTo<T>()` ile birleşmez (`HALDUS007`).

## Çeviriler

`.Translatable(...)` sayılan metin alanlarını başka dillere çevrilebilir yapar. Alanın kendisi kaynak dildeki metni
taşımaya devam eder; çeviriler kayıtla birlikte saklanır.

<!-- example: translatable -->
```csharp
app.MapCrud<Article>()
    .Translatable(article => article.Title, article => article.Body);

public partial class Article
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [MaxLength(10_000)]
    public string? Body { get; set; }
}
```

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `PUT` | `/articles/{id}/translations/{locale}` | O dilin çevirilerini gövdedekilerle değiştirir; kaydı döndürür. |
| `DELETE` | `/articles/{id}/translations/{locale}` | O dilin çevirilerini kaldırır; kaydı döndürür. Çevirisi olmayan dil hata değildir. |
| `GET` | `/articles/{id}?locale=tr`, `/articles?locale=tr` | Çevrilmiş alanları o dildeki metinle döndürür. |

```http
PUT /articles/019a8f90-5d6e-7f7a-8b8c-9d0e1f2a3b4c/translations/tr
Content-Type: application/json

{ "title": "Lansman", "body": "Merhaba" }
```

```json
{
  "id": "019a8f90-5d6e-7f7a-8b8c-9d0e1f2a3b4c",
  "title": "Launch",
  "body": "Hello",
  "translations": {
    "tr": { "title": "Lansman", "body": "Merhaba" }
  }
}
```

Yanıt, çevirilerin tamamını her zaman `translations` altında taşır; anahtarlar dil etiketi ve alanın camelCase adıdır.

```http
GET /articles/019a8f90-5d6e-7f7a-8b8c-9d0e1f2a3b4c?locale=tr
```

Yanıtta `"title": "Lansman"` ve `"body": "Merhaba"` olur; `translations` yine yanındadır.

### Okuma kuralları

- Okuma yalnızca `?locale=` verilirse çevrilir. `Accept-Language` başlığı veriyi değiştirmez (yalnızca hata
  mesajlarının dilini seçer): okuduğunu geri yazan bir istemci, örneğin Türkçe tarayıcıda açılmış bir yönetim paneli,
  kaynak metnin üzerine çeviri yazmasın diye.
- Tam eşleşme yoksa daha genel dile düşülür: `?locale=tr-TR` → `tr`, `pt-BR` → `pt`. Çevirisi olmayan alan kaynak
  dildeki metinle döner. Bilinmeyen veya geçersiz bir dil hata değildir; kaynak dildeki kayıt döner.
- `?locale=` liste, tekil okuma ve yayındaki halin okumalarında (`/published`) uygulanır. Yazma yanıtları, arama
  (`/search`), doğal anahtarla okuma ve sürümler kaynak dildedir.

### Yazma kuralları

- Dil etiketi `tr`, `pt-BR`, `zh-Hant-TW` biçimindedir ve küçük harfe çevrilerek saklanır (`pt-br`). Etiket biçimine
  uymayan bir değer (`turkish!`) 400 alır, hata `locale` alanındadır.
- `PUT` o dilin çevirilerini tamamen değiştirir. Gövdedeki alanların hiçbiri zorunlu değildir; boş bırakılan veya boş
  metin gönderilen alanın çevirisi kalkar, hiçbir alanı kalmayan dil çantadan çıkar.
- Çeviri, çevirdiği alanın uzunluk kurallarını taşır (`title` için en çok 200 karakter).
- Bir kayıt en çok 50 dile çevrilebilir; 51. dil 400 alır. Var olan bir dilin yeniden yazılması sınıra takılmaz.
- Çeviri yazmak kaydın bir güncellemesidir: `If-Match` (`ConcurrencyChecked`), düzenlenebilir durum (`EditableIn`) ve
  onaylanmış kaydın kilidi uygulanır. Çeviri sürümlerde saklanır, geri yüklemede geri gelir ve onayı düşürür.
- Çeviriler oluşturma ve güncelleme isteklerinde taşınmaz; kaynak metni değiştirmek çevirileri değiştirmez.
- Kopya ve yayındaki hal çevirileri taşır.

Sayılan alanlar entity'nin yazılabilir `string` alanları olmalıdır. `Translatable` yalnızca `MapCrud` ile birleşir ve
`PUT` ya da `PATCH` işlemlerinden en az birini ister (`HALDUS007`).

## Birlikte kullanım

| Bildirim | Birleştiği archetype'lar | Birleşmediği bildirimler |
|---|---|---|
| `Auditable` | `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapJob` (bakiyede ve dosyada kendiliğinden vardır) | |
| `CreateOnly` | `MapCrud`, ayrı uçlu detay | Oluşturması veya güncellemesi kaldırılmış kaynak |
| `KeyStrategy` | `MapCrud`, `MapReadOnly`, `MapAppendOnly` | `Client` için yukarıdaki listeler |
| `Numbered` | `MapCrud`, `MapAppendOnly` | `Publishable` |
| `Metadata` | `MapCrud`, `MapReadOnly`, `MapAppendOnly` | |
| `CustomFields` | `MapCrud` | `Under<T>()`, `AttachedTo<T>()` |
| `Translatable` | `MapCrud` | `PUT` ve `PATCH`'i kaldırılmış kaynak |

Bir archetype'la çelişen bildirim sessizce yok sayılmaz; proje `HALDUS007` ile derlenmez ve mesaj iki bildirimi ve
nedeni söyler.

## Sınırlar

- Çevirilere ve özel alanlara göre süzme, arama ve sıralama yoktur; hepsi kaynak dildeki metin ve kaydın kendi alanları
  üzerindendir.
- Kaynak metin değişince çeviriler eskimiş sayılmaz. Gömülü satırların ve gömülü değer nesnelerinin alanları
  çevrilemez. Toplu oluşturulan ve içe aktarılan kayıtlar çevirisiz doğar.
- Özel alan tanımı değişince var olan kayıtlar yeniden doğrulanmaz.
- Metadata değerleri yalnızca metindir; iç içe nesne veya sayı tutmaz.
- Numaralar bir kez verilir ve değiştirilemez; numarayı sonradan yeniden düzenleyen bir uç yoktur.

## Ayrıca bakın

- [Ortak kurallar](../concepts/conventions.md): `PUT` ile `PATCH`, alan adları, hata biçimi.
- [Listeleme ve arama](querying.md): doğal anahtar, alanlara göre sıralama ve arama.
- [Durum makineleri](state-machines.md): `Workflow` ve numaranın bir durumda verilmesi.
- [Hassas veriler](sensitive-data.md): yalnızca yazılan ve şifreli alanlar.
- [Kiracılar ve kullanıcılar](tenancy-and-ownership.md): kiracının ve kullanıcının nereden okunduğu.
- [Bildirim dizini](../reference/declarations.md).
