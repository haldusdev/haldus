# Detaylar ve hesaplanan alanlar

Bazı kayıtlar satırlarıyla birlikte bir bütündür: sipariş ve sipariş satırları, fatura ve kalemleri, satın alma talebi
ve kalemleri. Satır kendi başına bir kaynak değildir; sahibine aittir, sahibiyle silinir ve sahibinin görünürlüğünü
devralır. Haldus'ta böyle satırlar `MapDetail` ile bildirilir. Satırlardan hesaplanan değerler (toplam tutar, kalem
sayısı) ise entity'nin `IComputed` sözleşmesiyle yazılır. Bu sayfa satırların üç yazılış biçimini ve hesaplanan
alanları anlatır.

> [!NOTE]
> Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
> `System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs` içindedir;
> `var builder = WebApplication.CreateBuilder(args);` ve `var app = builder.Build();` satırları yazılmamışsa yazılmış
> sayılır. Hata gövdeleri kısaltılmıştır.

## İçindekiler

- [Satırları bildirmek](#satırları-bildirmek)
- [Gömülü satırlar](#gömülü-satırlar)
  - [Eşitleme](#eşitleme)
- [Ayrı uçlu satırlar](#ayrı-uçlu-satırlar)
- [Komutla yazılan satırlar](#komutla-yazılan-satırlar)
- [Hesaplanan alanlar](#hesaplanan-alanlar)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Satırları bildirmek

`MapDetail<T>()` sahibin builder'ından çağrılır. Sahibi, çağrıldığı builder'ın tip argümanından okunur; bu yüzden
builder bir değişkende tutulabilir:

<!-- fragment -->
```csharp
var orders = app.MapCrud<Order>();

orders.MapDetail<OrderItem>();                  // gömülü: siparişin isteğinde ve yanıtında
orders.MapDetail<OrderNote>().AsSubresource();  // ayrı uçlar: /orders/{orderId}/order-notes
orders.MapDetail<OrderLine>().AsCommands();     // yanıtta gömülü, tek tek komutlarla yazılır
```

| Biçim | Satırlar nerede yazılır | Satırlar nerede okunur | Ne zaman |
|---|---|---|---|
| Gömülü (varsayılan) | Sahibin oluşturma, `PUT` ve `PATCH` isteklerinde, topluca. | Sahibin yanıtında. | Satırlar az ve sahiple birlikte düzenlenir (sipariş satırları). |
| `.AsSubresource()` | Kendi uçlarında, tek tek. | Kendi uçlarında. | Satırlar çok veya sahipten bağımsız değişir (notlar). |
| `.AsCommands()` | Sahibin adresindeki komutlarla, tek tek. | Sahibin yanıtında. | Satırlar sahibin parçasıdır ama topluca değil tek tek düzenlenir (sertifikalar). |

Satır entity'sini `partial class` olarak siz yazarsınız; anahtarını (`Id`) ve sahibine giden yabancı anahtarı
(`OrderId`) Haldus ekler. Satır kendi `Map*` çağrısıyla ayrıca eşlenemez (`HALDUS002`). `AsSubresource` ve `AsCommands`
doğrudan `MapDetail` çağrısına zincirlenmelidir; değişken üzerinden çağrılırlarsa `HALDUS004` verilir. İkisi birlikte
yazılamaz (`HALDUS007`).

## Gömülü satırlar

Varsayılan biçimde satırlar sahibin istek ve yanıtlarında bir liste olarak taşınır ve sahiple aynı kaydetmede yazılır.
Haldus sahibin entity'sine satır koleksiyonunu ekler; adı satır tipinin çoğuludur (`OrderItems`). Bu koleksiyonu
`Compute()` içinde ve iş mantığınızda kullanırsınız.

<!-- example: embedded -->
```csharp
var orders = app.MapCrud<Order>();
orders.MapDetail<OrderItem>();

public partial class Order : IComputed
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";

    // Herkese açık setter'ı olmayan alanlar yalnızca yanıtta görünür.
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

Satırın kendi uçları yoktur; siparişin uçları satırları taşır:

```http
POST /orders
Content-Type: application/json

{
  "customerName": "Ada Yılmaz",
  "orderItems": [
    { "productName": "Klavye", "quantity": 2, "unitPrice": 100 },
    { "productName": "Mouse", "quantity": 1, "unitPrice": 40.5 }
  ]
}
```

```http
HTTP/1.1 201 Created
Location: /orders/0199a3d0-0a00-7c10-8a10-0a1b2c3d4e50

{
  "id": "0199a3d0-0a00-7c10-8a10-0a1b2c3d4e50",
  "customerName": "Ada Yılmaz",
  "total": 240.5,
  "itemCount": 2,
  "orderItems": [
    {
      "id": "0199a3d0-0a01-7c11-8a11-0a1b2c3d4e51",
      "productName": "Klavye",
      "quantity": 2,
      "unitPrice": 100,
      "orderId": "0199a3d0-0a00-7c10-8a10-0a1b2c3d4e50"
    },
    {
      "id": "0199a3d0-0a02-7c12-8a12-0a1b2c3d4e52",
      "productName": "Mouse",
      "quantity": 1,
      "unitPrice": 40.5,
      "orderId": "0199a3d0-0a00-7c10-8a10-0a1b2c3d4e50"
    }
  ]
}
```

Satırlar sahibin liste ve tekil okuma yanıtlarında da yer alır.

**Kurallar ve hata yanıtları**

- Oluşturmada satırlar isteğe bağlıdır; gönderilmezse kayıt satırsız oluşur.
- Satırın doğrulama kuralları her yazmada çalışır. Hata anahtarı satırın sırasını taşır:

  ```http
  HTTP/1.1 400 Bad Request

  {
    "title": "One or more validation errors occurred.",
    "status": 400,
    "errors": {
      "orderItems[1].quantity": [ "The Quantity field must be between 1 and 10000." ]
    }
  }
  ```

- Satırlar istekte gönderildikleri sırayla saklanır ve aynı sırayla döner. Sırayı satırın `Position` alanı tutar;
  Haldus bu alanı ekler ve DTO'lara koymaz. Alanı kendiniz yazdıysanız Haldus onu kullanır.
- Satırın sahibi (`orderId`) yanıtta görünür ama satırın isteğinde yer almaz; sahip adresten ve kayıttan bilinir.
- Satırlar sahibinin görünürlüğünü devralır: sipariş kiracıya aitse satırları da yalnızca o kiracı görür. Sahip
  silinince satırları da silinir.
- Yalnızca satırlar değişse de sahip değişmiş sayılır: sürümü (`ETag`) ve değiştirilme zamanı (`updatedAt`) ilerler.
  Satırları uçlar dışında, bir aksiyonun veya geçişin mantığı ya da uygulamanın kendi kodu değiştirdiğinde de öyledir.

### Eşitleme

`PUT` ve `PATCH` satırları gönderilen listeyle eşitler:

| Satır | Ne olur |
|---|---|
| `id` verildi | Var olan satır güncellenir. |
| `id` verilmedi | Yeni satır eklenir; kimliğini Haldus verir. |
| İstekte yok | Satır silinir. |

```http
PUT /orders/0199a3d0-0a00-7c10-8a10-0a1b2c3d4e50
Content-Type: application/json

{
  "customerName": "Ada Yılmaz",
  "orderItems": [
    { "id": "0199a3d0-0a01-7c11-8a11-0a1b2c3d4e51", "productName": "Klavye", "quantity": 5, "unitPrice": 100 },
    { "productName": "Kulaklık", "quantity": 1, "unitPrice": 750 }
  ]
}
```

Yukarıdaki istek ilk satırı günceller, kulaklığı ekler ve mouse satırını siler.

- `PUT` isteğinde satır listesi zorunludur. Gönderilmeyen liste "hepsini sil" anlamına gelmez; istek `400` ile
  reddedilir ve hata `orderItems` alanına yazılır. Bütün satırları silmek için boş liste (`[]`) gönderilir.
- `PATCH` isteğinde liste gönderilmezse satırlara dokunulmaz. Gönderilirse `PUT`'taki gibi eşitlenir; `null` gönderilen
  liste `400` döner.
- Satırın kimliği o kayda ait değilse veya istekte iki kez geçiyorsa `400` döner, hata `orderItems` alanına yazılır ve
  hiçbir şey kaydedilmez:

  ```http
  HTTP/1.1 400 Bad Request

  {
    "title": "One or more validation errors occurred.",
    "status": 400,
    "errors": {
      "orderItems": [ "OrderItem '0199a3d1-0b00-7c10-8a10-0a1b2c3d4e50' does not belong to this record, or it appears more than once in the request." ]
    }
  }
  ```

- Satırlar tek tek yamalanmaz: bir satırın tek bir alanını değiştirmek için satırı bütün alanlarıyla ve kimliğiyle
  gönderirsiniz.

## Ayrı uçlu satırlar

`.AsSubresource()` satırları sahibin istek ve yanıtlarından çıkarır ve onlara kendi uçlarını verir. Uçlar sahibin
adresinin altındadır.

<!-- example: subresource -->
```csharp
var orders = app.MapCrud<Order>().MultiTenant();
orders.MapDetail<OrderNote>().AsSubresource();

public partial class Order
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";
}

public partial class OrderNote
{
    [MaxLength(500)]
    public string Text { get; set; } = "";
}
```

**Üretilen uçlar**

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/orders/{orderId}/order-notes` | Siparişin notlarını listeler. |
| `GET` | `/orders/{orderId}/order-notes/{id}` | Bir notu okur. |
| `POST` | `/orders/{orderId}/order-notes` | Not ekler (`201`). |
| `PUT` / `PATCH` | `/orders/{orderId}/order-notes/{id}` | Notu günceller. |
| `DELETE` | `/orders/{orderId}/order-notes/{id}` | Notu siler. |

```http
POST /orders/0199a3d0-0a00-7c10-8a10-0a1b2c3d4e50/order-notes
Content-Type: application/json

{ "text": "Teslimattan önce arayın." }
```

```http
HTTP/1.1 201 Created
Location: /orders/0199a3d0-0a00-7c10-8a10-0a1b2c3d4e50/order-notes/0199a3d2-0c00-7c10-8a10-0a1b2c3d4e50

{
  "id": "0199a3d2-0c00-7c10-8a10-0a1b2c3d4e50",
  "text": "Teslimattan önce arayın.",
  "orderId": "0199a3d0-0a00-7c10-8a10-0a1b2c3d4e50"
}
```

**Kurallar ve hata yanıtları**

- Sahip yoksa veya isteği yapana görünmüyorsa uçlar `404` döner. Satırlar sahibin görünürlüğünü devralır: başka bir
  kiracı yukarıdaki notları ne listeleyebilir ne okuyabilir ne de not ekleyebilir.
- Uçlar sahibin grubuna eşlendiği için sahibin istek kurallarını da devralır; yukarıda kiracısı çözülemeyen istek `403`
  alır.
- Satır sahibin parçasıdır: sahibin durum makinesinde `EditableIn` varsa, satırın yazma uçları da sahibin durumuna
  bakar. Sipariş düzenlenebilir bir durumda değilse not eklemek, güncellemek ve silmek `409 Conflict` döner.
  Sahipten bağımsız yazılması gereken alt kayıtlar için `MapDetail` yerine
  [`.Under<T>()`](relationships.md#ebeveyn-altında) kullanılır.
- Sahip silinince satırları da silinir.
- Ayrı uçlu satırdaki bir değişiklik sahibin `Compute()` metodunu çalıştırmaz ve sahibin sürümünü ilerletmez.

`AsSubresource` `MapCrud`, `MapAppendOnly` ve `MapReadOnly` sahiplerinde kullanılabilir. Salt-okunur bir siparişin
notları böylece istemciden yazılabilir.

## Komutla yazılan satırlar

`.AsCommands()` iki biçimin ortasıdır: satırlar sahibin yanıtında ve oluşturma isteğinde gömülü kalır, ama `PUT` ve
`PATCH` isteklerinde taşınmaz. Oluşturulduktan sonra her satır tek bir komutla eklenir, değiştirilir veya kaldırılır.
Her komut sahibin bir yazmasıdır.

<!-- example: commands -->
```csharp
var suppliers = app.MapCrud<Supplier>().ConcurrencyChecked();
suppliers.MapDetail<SupplierCertificate>().AsCommands();

public partial class Supplier : IComputed
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public int CertificateCount { get; private set; }

    public void Compute() => CertificateCount = SupplierCertificates.Count;
}

public partial class SupplierCertificate
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    public DateTimeOffset? ValidUntil { get; set; }
}
```

**Üretilen uçlar**

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/suppliers/{id}/supplier-certificates` | Satır ekler (sona). |
| `PUT` | `/suppliers/{id}/supplier-certificates/{rowId}` | Satırı değiştirir. |
| `DELETE` | `/suppliers/{id}/supplier-certificates/{rowId}` | Satırı kaldırır. |

Üçü de sahibi, satırları ve hesaplanan alanlarıyla birlikte döndürür (`200`).

```http
POST /suppliers/0199a3d3-0d00-7c10-8a10-0a1b2c3d4e50/supplier-certificates
If-Match: "1043"
Content-Type: application/json

{ "name": "ISO 14001" }
```

```http
HTTP/1.1 200 OK
ETag: "1051"

{
  "id": "0199a3d3-0d00-7c10-8a10-0a1b2c3d4e50",
  "name": "Acme Tedarik",
  "certificateCount": 2,
  "supplierCertificates": [
    { "id": "0199a3d3-0d01-7c11-8a11-0a1b2c3d4e51", "name": "ISO 9001", "validUntil": null, "supplierId": "0199a3d3-0d00-7c10-8a10-0a1b2c3d4e50" },
    { "id": "0199a3d3-0d02-7c12-8a12-0a1b2c3d4e52", "name": "ISO 14001", "validUntil": null, "supplierId": "0199a3d3-0d00-7c10-8a10-0a1b2c3d4e50" }
  ]
}
```

**Kurallar ve hata yanıtları**

- Komut, sahibin yazma kurallarından geçer: sahip `ConcurrencyChecked` ise `If-Match` ister (eksikse `428`, eski
  sürümle `412`), sahibin `EditableIn` kuralına uyar (`409`), `Compute()` çalışır ve sahibin sürümü ilerler.
- Satırın doğrulama kuralları istekte çalışır (`400`). Satır o kayda ait değilse `404` döner.
- Sahibin `PUT` ve `PATCH` istekleri satırlara dokunmaz; gönderilen bir satır listesi yok sayılır.
- Yeni satır sona eklenir; araya ekleme yoktur.
- Komutun adresi (`supplier-certificates`) sahibin üzerinde ayrılmıştır; aynı adla bir geçiş veya aksiyon `HALDUS007`
  verir.

## Hesaplanan alanlar

Entity `IComputed` uygularsa Haldus her yazmada, isteği uyguladıktan ve gömülü satırları yükledikten sonra, kaydetmeden
hemen önce `Compute()` metodunu çağırır.

<!-- fragment -->
```csharp
public interface IComputed
{
    void Compute();
}
```

Hesaplanan alan, herkese açık setter'ı olmayan bir alandır (`{ get; private set; }`). Böyle bir alan:

- istek DTO'larına girmez; istemci onu yazamaz, gönderse de yok sayılır,
- yanıtta görünür,
- veritabanında saklanır; bu yüzden `Sortable`, `Searchable` ve kendi filtre sınıfınızla süzülüp sıralanabilir.

<!-- example: computed -->
```csharp
app.MapCrud<Invoice>().Sortable(invoice => invoice.Total);

public partial class Invoice : IComputed
{
    [Range(0.0, 10_000_000.0)]
    public decimal Subtotal { get; set; }

    [Range(0.0, 1.0)]
    public decimal TaxRate { get; set; }

    public decimal Total { get; private set; }

    public void Compute() => Total = decimal.Round(Subtotal * (1 + TaxRate), 2);
}
```

```http
POST /invoices
Content-Type: application/json

{ "subtotal": 1000, "taxRate": 0.2, "total": 1 }
```

```http
HTTP/1.1 201 Created

{ "id": "0199a3d4-0e00-7c10-8a10-0a1b2c3d4e50", "subtotal": 1000, "taxRate": 0.2, "total": 1200 }
```

**`Compute()` ne zaman çağrılır**

Kaydı yazan bütün uçlarda: oluşturma, `PUT`, `PATCH`, toplu uçlar, geçişler ve entity aksiyonları, komutla yazılan
satırlar, kopya, önizleme, değişiklik kümeleri, başka bir kayıttan üretme, ters kayıt ve yerine geçen kayıt. Toplanan
alanlar değiştiğinde de yeniden çağrılır ([Toplanan alanlar](rollups.md)). Kayıt bir tipten varsayılan devralıyorsa
([`TypedBy`](relationships.md#tip-ve-örnek)) devralma `Compute()`'tan önce yapılır.

**Kurallar**

- `Compute()` kaydı kaydetmemelidir; `SaveChangesAsync` çağırmayın. Haldus mantıktan sonra kaydeder.
- `Compute()` senkron ve yan etkisizdir: yalnızca kaydın kendi alanlarından ve yüklenmiş satırlarından hesaplar.
  Başka kayıtlardan türeyen değerler için [toplanan alanları](rollups.md) kullanın.
- Ayrı uçlu (`AsSubresource`) satırların değişikliği sahibin `Compute()` metodunu çalıştırmaz.
- `Compute()` Haldus'un uçlarına bağlıdır. Uygulamanızın kendi kodu kaydı doğrudan bağlamla yazıyorsa, gerekiyorsa
  `Compute()` metodunu kendiniz çağırırsınız.

## Birlikte kullanım

| Biçim | Sahip olabilir | Birleşmez |
|---|---|---|
| Gömülü | `MapCrud`, `MapAppendOnly` | `MapReadOnly`, `MapSingleton` ve öteki archetype'lar (`HALDUS007`); alt tip hiyerarşisindeki kaynaklar. |
| `AsSubresource()` | `MapCrud`, `MapAppendOnly`, `MapReadOnly` | `MapStore` ve öteki archetype'lar (`HALDUS007`); `Approvable` sahibi; `AsCommands`. |
| `AsCommands()` | Gömülü satırla aynı sahipler | `AsSubresource`; sahipte aynı adlı geçiş veya aksiyon. |

Birden çok hedefe takılabilen bir kaynağın (`AttachedTo`) tek bir adresi olmadığı için hiçbir biçimde satırı olamaz
(`HALDUS007`).

Satırın alanları da hassas olabilir: `orders.MapDetail<OrderItem>().Sensitive(item => item.Note)` alanı yalnızca
yazılan bir alan yapar; bkz. [Hassas alanlar](sensitive-data.md). Satır ekleyip çıkaran iş mantığı (geçiş, aksiyon,
`ICreatedFrom<T>`) yazabilirsiniz; yeni satırlara kimliği ve sırayı Haldus verir. Bkz.
[İş mantığı](business-logic.md#entity-aksiyonları).

## Sınırlar

- Gömülü satırın `Id` alanı `Guid` olmalıdır; kendiniz başka bir tiple yazarsanız `HALDUS007` verilir.
- Satırın satırı yoktur: bir detayın kendi detayı olamaz.
- Gömülü satırlar tek tek yamalanmaz; liste her zaman bütün olarak eşitlenir.
- Komutla yazılan satırların toplu biçimi ve önizlemesi yoktur; yeni satır her zaman sona eklenir.
- Ayrı uçlu satırın sahip durumu denetimi sahibin satırını kilitlemez: sahip tam o anda durum değiştirirse satır
  yazması yine de geçebilir.
- Ayrı uçlu satırlar sahibin sürümüne, `Compute()` metoduna ve onayına bağlı değildir.

## Ayrıca bakın

- [Toplanan alanlar](rollups.md): bağlı kayıtların sayısı ve toplamı.
- [İlişkiler](relationships.md): bağımsız alt kayıtlar için `Under<T>()`.
- [Durum makineleri](state-machines.md): `EditableIn` ve geçişler.
- [Kayıt işlemleri](record-operations.md#kopyalama): satırlarıyla birlikte kopyalama.
- [Bildirim dizini](../reference/declarations.md)
