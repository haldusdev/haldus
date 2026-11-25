# Kaydın yaşam döngüsü

Bir kayıt oluşturulur, değişir ve bir gün ortadan kalkar. Bu sayfadaki bildirimler o yolculuğun kurallarını koyar:
silmenin geri alınabilmesi (`SoftDeletable`), kaydın silinmeden kenara kaldırılması (`Archivable`), iki kişinin aynı
kaydı aynı anda değiştirmesine karşı koruma (`ConcurrencyChecked`), süresi dolan kayıtlar (`Expiring`), belli bir yaştan
sonra silinen kayıtlar (`Retention`), yinelenen isteklere karşı koruma (`Idempotent`), belirli bir dönemde geçerli olan
kayıtlar (`EffectiveDated`) ve yinelenen kayıtlar (`Recurring`).

Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. `builder` ve `app` satırları yoksa `Program.cs`'in geri
kalanı yazılmış sayılır.

## İçindekiler

- [Geri alınabilir silme](#geri-alınabilir-silme)
- [Arşivleme](#arşivleme)
- [Eşzamanlılık denetimi](#eşzamanlılık-denetimi)
- [Süresi dolan kayıtlar](#süresi-dolan-kayıtlar)
- [Saklama süresi](#saklama-süresi)
- [Tekrara karşı koruma](#tekrara-karşı-koruma)
- [Geçerlilik dönemleri](#geçerlilik-dönemleri)
- [Tekrar serisi](#tekrar-serisi)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Geri alınabilir silme

`.SoftDeletable()` ile `DELETE` kaydı veritabanından kaldırmaz; silinme zamanını (`deletedAt`) yazar ve kaydı gizler.
Silinen kayıt geri yüklenebilir veya kalıcı olarak silinebilir.

<!-- example: soft-delete -->
```csharp
app.MapCrud<Customer>()
    .SoftDeletable();

public partial class Customer
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `DELETE` | `/customers/{id}` | Kaydı siler (gizler); 204. |
| `GET` | `/customers?includeDeleted=true` | Listeye silinmiş kayıtları da katar. |
| `POST` | `/customers/{id}/restore` | Silinmiş kaydı geri yükler; kaydı döndürür (200). |
| `POST` | `/customers/{id}/purge` | Silinmiş kaydı kalıcı olarak siler; 204. Geri alınamaz. |

```http
GET /customers?includeDeleted=true
```

```json
{
  "items": [
    {
      "id": "019a8fa0-6e7f-7a8b-9c9d-0e1f2a3b4c5d",
      "name": "Ada Lovelace",
      "deletedAt": "2026-11-25T11:20:05.481022+00:00"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

### Kurallar ve hata yanıtları

- Silinmiş kayıt her yerde gizlidir: tekil okuma 404, `PUT`, `PATCH` ve `DELETE` 404 alır; liste ve arama onu yalnızca
  `includeDeleted` ile gösterir (aramada gövdede: `"includeDeleted": true`). Gizleme bir EF Core sorgu filtresidir; bu
  yüzden başka kayıtlardan yapılan referans doğrulamaları ve uygulamanızın kendi sorguları da onu görmez. Silinmiş bir
  kaydın altındaki kayıtlar (`Under<T>()`) da, ebeveynleri görünmediği için görünmez.
- `restore` ve `purge` yalnızca silinmiş kayıtta çalışır; silinmemiş kayıtta ikisi de 404 döner.
- Başka kayıtların referans verdiği bir kaydın kalıcı silinmesi veritabanınca reddedilir ve 409 döner.
- Silme bir güncellemedir: `Auditable` kaynakta `updatedAt` alanını ilerletir. Kaydı hangi kod silerse silsin (üretilen
  uç veya `context.Remove(...)`) silme bir güncellemeye çevrilir.
- Silinmiş kayıt, doğal anahtarının değerini tutmaya devam eder ([doğal anahtar](querying.md#doğal-anahtar)).
- Uygulama kodunda silinmiş kayıtları da okumak için filtreyi adıyla kaldırın:
  `IgnoreQueryFilters([HaldusFilters.SoftDelete])`. Parametresiz `IgnoreQueryFilters()` kiracı ve sahiplik filtrelerini
  de kaldırır.

`SoftDeletable`; `MapAppendOnly`, `MapStore`, `MapLookup`, `MapSingleton`, `MapJob` ve `MapFile` ile birleşmez
(`HALDUS007`).
Ölçüte uyan kayıtları topluca silmek için bkz. [ölçüte göre silme](record-operations.md#ölçüte-göre-silme).

## Arşivleme

`.Archivable()` kaydı silmeden kenara kaldırmanın yoludur: artık kullanılmayan bir marka, ayrılmış bir müşteri.
Arşivlenen kayıt listede görünmez ama kimliğiyle okunabilir.

<!-- example: archive -->
```csharp
app.MapCrud<Brand>()
    .Archivable()
    .Without(CrudOperation.Delete);

public partial class Brand
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";
}
```

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/brands/{id}/archive` | Kaydı arşivler; kaydı döndürür (200). |
| `POST` | `/brands/{id}/unarchive` | Arşivden çıkarır; kaydı döndürür (200). |
| `GET` | `/brands?includeArchived=true` | Listeye arşivlenmiş kayıtları da katar. |

```http
POST /brands/019a8fb1-7f8a-7b9c-8d0e-1f2a3b4c5d6e/archive
```

```json
{
  "id": "019a8fb1-7f8a-7b9c-8d0e-1f2a3b4c5d6e",
  "name": "Eski Marka",
  "archivedAt": "2026-11-25T11:42:18.093411+00:00"
}
```

### Kurallar

- Arşivlenmiş kayıt yalnızca listeden ve aramadan gizlenir (aramada gövdede `"includeArchived": true`). Tekil okuma,
  güncelleme ve geçişler çalışmaya devam eder: arşivlemek kaydı kilitlemez.
- Arşivlenmiş bir kaydı yeniden arşivlemek hata değildir; ilk arşivlenme zamanı korunur. `unarchive` alanı boşaltır.
- Arşivleme bir güncellemedir: `Auditable` kaynakta `updatedAt` alanını ilerletir, `ConcurrencyChecked` kaynakta
  `If-Match` ister.

`Archivable`; `MapSingleton`, `MapJob` ve `MapBalance` ile birleşmez (`HALDUS007`).

## Eşzamanlılık denetimi

İki kişi aynı kaydı okuyup ikisi de değiştirirse, ikincinin yazması birincininkini fark etmeden ezer. `.ConcurrencyChecked()`
bunu önler: her yanıt kaydın sürümünü `ETag` başlığında taşır, değiştiren her istek okuduğu sürümü `If-Match`
başlığında geri gönderir. Kayıt bu arada değiştiyse istek reddedilir.

<!-- example: concurrency -->
```csharp
app.MapCrud<Customer>()
    .ConcurrencyChecked();

public partial class Customer
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

Kaydı okuyun; sürüm başlıktadır, gövdede değildir:

```http
GET /customers/019a8fa0-6e7f-7a8b-9c9d-0e1f2a3b4c5d

HTTP/1.1 200 OK
ETag: "744"
```

Değiştirirken o sürümü gönderin:

```http
PUT /customers/019a8fa0-6e7f-7a8b-9c9d-0e1f2a3b4c5d
If-Match: "744"
Content-Type: application/json

{ "name": "Ada King" }
```

İstek geçer ve yanıt yeni sürümü taşır (`ETag: "747"`).

| Durum | Yanıt |
|---|---|
| `If-Match` gönderilmedi | 428 |
| `If-Match` eski bir sürüm veya okunamayan bir değer | 412; kayıt değişmez. |
| `If-Match: *` | Sürüme bakılmadan geçer (başlık yine zorunludur). |

```json
{
  "title": "Version required",
  "status": 428,
  "detail": "Send the version you last read in the If-Match header."
}
```

```json
{
  "title": "Resource has changed",
  "status": 412,
  "detail": "The resource was modified after you last read it. Reload it and try again."
}
```

### Kurallar

- `ETag` tekil okuma, oluşturma ve kaydı değiştiren her yanıtta (silme dahil) döner. Liste yanıtının öğeleri sürüm
  taşımaz; sürümü kaydı tek tek okuyarak alırsınız.
- `If-Match` kaydın durumunu değiştiren her uçta zorunludur: `PUT`, `PATCH`, `DELETE`, `restore`, `purge`, `archive`,
  `unarchive`, `expire`, geçişler, entity aksiyonları, satır komutları, çeviriler ve tekrar serisinin oluş uçları.
- Sürüm, satırın PostgreSQL'deki sürümüdür (`xmin`); bir kolon eklenmez. Aynı sürümle gelen iki yazmadan yalnızca biri
  geçer, öteki 412 alır.
- Silme yanıtı da yeni sürümü taşır: geri alınabilir silmede kaydı hemen geri yüklemek için o sürümü kullanın.
- `ConcurrencyChecked` olmayan kaynakta başlıklar yok sayılır ve son yazan kazanır.

`ConcurrencyChecked`; `MapReadOnly`, `MapAppendOnly`, `MapStore`, `MapLookup`, `MapSingleton` ve `MapJob` ile
birleşmez. Toplu güncelleme ve silme öğe başına sürüm taşıyamadığı için, güncellemesi veya silmesi olan bir kaynakta
`Batch` ile de birleşmez (`HALDUS007`).

## Süresi dolan kayıtlar

`.Expiring(...)` kayda bir son kullanma zamanı (`expiresAt`) ekler. Süresi dolan kayıt silinmez ama erişimi kesilir:
okunamaz ve yazılamaz. Üç biçimi vardır:

| Bildirim | `expiresAt` | Örnek |
|---|---|---|
| `.Expiring()` | İstemci yazar; boşsa kayıt süresizdir. | Son kullanma tarihi olan kupon |
| `.Expiring(TimeSpan.FromMinutes(15))` | Sunucu oluştururken hesaplar; istekle yazılamaz. | Stok rezervasyonu |
| `.Expiring(TimeSpan.FromDays(7), sliding: true)` | Sunucu hesaplar ve her yazmada süreyi yeniden başlatır. | Kullanıldıkça yaşayan sepet |

<!-- example: expiring -->
```csharp
app.MapCrud<Coupon>()
    .Expiring();

app.MapCrud<Reservation>()
    .Expiring(TimeSpan.FromMinutes(15));

app.MapCrud<Cart>()
    .Expiring(TimeSpan.FromDays(7), sliding: true);

public partial class Coupon
{
    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = "";
}

public partial class Reservation
{
    [MaxLength(200)]
    public string ProductName { get; set; } = "";

    [Range(1, 1000)]
    public int Quantity { get; set; }
}

public partial class Cart
{
    [MaxLength(500)]
    public string? Note { get; set; }
}
```

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/reservations/{id}/expire` | Kaydı hemen sonlandırır (`expiresAt` şimdiki zaman olur); 204. |

```http
POST /coupons
Content-Type: application/json

{ "code": "BLACK-FRIDAY", "expiresAt": "2026-11-30T23:59:59+03:00" }
```

```http
POST /reservations
Content-Type: application/json

{ "productName": "Klavye", "quantity": 1 }
```

```json
{
  "id": "019a8fc2-8a9b-7c0d-9e1f-2a3b4c5d6e7f",
  "productName": "Klavye",
  "quantity": 1,
  "expiresAt": "2026-11-25T12:15:00.204817+00:00"
}
```

### Kurallar

- Zamanı geçmiş kayıt her yerde gizlidir: tekil okuma, `PUT`, `PATCH`, `DELETE` ve geçişler 404 alır, liste ve arama onu
  göstermez, başka kayıtların referans doğrulaması onu bulamaz. Gizleme okuma anında, sorgu filtresiyle yapılır; kayıt
  veritabanında kalır.
- `expire` süresi dolmuş bir kayıtta 404 döner.
- `.Expiring()` biçiminde `expiresAt` sıradan bir alandır: oluştururken ve güncellerken yazılır, `PATCH` ile uzatılabilir
  veya geçmişe çekilerek kayıt hemen sonlandırılabilir.
- Ömürlü biçimde istekteki `expiresAt` yok sayılır; alan yalnızca yanıtta görünür.
- `sliding: true` ile süre her `PUT`, `PATCH` ve geçişte yeniden başlar. Okumak süreyi uzatmaz.
- Ömürlü bir kaynağın kopyası (`Cloneable`) yeni bir ömürle doğar.
- Süresi dolmuş kayıt, doğal anahtarının değerini tutmaya devam eder: süresi dolmuş bir kuponun kodu yeniden
  dağıtılamaz.
- Ömür, derleme zamanında okunabilen bir ifade olmalıdır (`TimeSpan.FromMinutes(15)` gibi sabit argümanlı); bir değişken
  veya yapılandırmadan okunan değer `HALDUS010` olur.

### Süresi dolmuş kayıtların silinmesi

Varsayılan olarak süresi dolan kayıtlar hiç silinmez: kullanıcı verisi kendiliğinden yok edilmez. Silinmelerini
istiyorsanız `HaldusOptions.PurgeExpiredAfter` verin; bakım servisi, süresi bu kadar önce dolmuş kayıtları fiziksel
olarak siler:

<!-- example: purge-expired -->
```csharp
builder.Services.AddHaldus<AppDbContext>(options =>
{
    options.PurgeExpiredAfter = TimeSpan.FromDays(30);
    options.MaintenanceInterval = TimeSpan.FromHours(1);
});

var app = builder.Build();

app.MapCrud<Reservation>()
    .Expiring(TimeSpan.FromMinutes(15));

public partial class Reservation
{
    [MaxLength(200)]
    public string ProductName { get; set; } = "";
}
```

| Seçenek | Varsayılan | Anlamı |
|---|---|---|
| `PurgeExpiredAfter` | `null` (hiç silinmez) | Süresi dolduktan ne kadar sonra silineceği. `TimeSpan.Zero` süresi dolar dolmaz siler. |
| `MaintenanceInterval` | 1 saat | Bakımın ne sıklıkla çalıştığı. |

Silme geri alınamaz ve veritabanında zincirleme silinen alt kayıtları da götürür. Bakım ayrıca saklama süresi geçmiş
idempotency yanıtlarını ve [saklama süresi](#saklama-süresi) dolan kayıtları da siler.

`Expiring` yalnızca `MapCrud` ve `MapAppendOnly` ile birleşir.

## Saklama süresi

Bazı kayıtlar birikir ve bir süre sonra değerini yitirir: bildirimler, olay günlükleri. `.Retention(...)` kayıtları
oluşturulduktan verilen süre sonra siler.

<!-- example: retention -->
```csharp
app.MapAppendOnly<ActivityLog>()
    .Retention(TimeSpan.FromDays(90));

public partial class ActivityLog
{
    [Required]
    [MaxLength(500)]
    public string Message { get; set; } = "";
}
```

Kaydın yaşı `createdAt` alanından ölçülür; `Retention` bu yüzden `Auditable` alanlarını kendiliğinden ekler. Bakım
servisi her turda (`HaldusOptions.MaintenanceInterval`, varsayılan 1 saat) oluşturulalı 90 günden uzun olmuş kayıtları
fiziksel olarak siler.

### Kurallar

- Silme geri alınamaz ve bir istek değildir; uç üretilmez.
- Kiracı, sahip, silinmiş ve süresi dolmuş kayıt ayrımı yapılmaz: yaşını dolduran her kayıt silinir.
- Başka bir kayıt hâlâ referans veriyorsa veritabanı silmeyi reddeder; uyarı günlüğe yazılır ve bir sonraki turda
  yeniden denenir.
- Silme değişiklik izleyicisini atlayan toplu bir sorgudur: olay (`PublishesEvents`), sürüm (`Revisioned`), değişiklik
  akışı (`ChangeFeed`) ve toplanan alanlar (`Counts` / `Sums`) bu silmeyi görmez.
- Süre, `Expiring`'in aksine ayrıca bir seçenekle açılmaz: bildirimi geliştirici açıkça yazmıştır.
- Süre derleme zamanında okunabilen bir değer olmalıdır (`TimeSpan.FromDays(90)`); değilse `HALDUS010`.

`Retention`; `MapCrud`, `MapReadOnly` ve `MapAppendOnly` ile birleşir. İçeriği depoda sahipsiz kalacağı için dosyalarla
(`MapFile`), yayındaki kopyası olduğu için `Publishable` ile birleşmez (`HALDUS007`).

## Tekrara karşı koruma

Ağ koptuğunda istemci isteğinin işlenip işlenmediğini bilemez ve yeniden gönderir. Bir ödemenin, iadenin veya siparişin
iki kez oluşması böyle olur. `.Idempotent()` ile istemci isteğe bir `Idempotency-Key` başlığı ekler; aynı anahtarla
yeniden gelen istek işlemi tekrarlamaz, ilk isteğin yanıtını döndürür.

<!-- example: idempotent -->
```csharp
app.MapAppendOnly<Refund>()
    .Idempotent(required: true);

public partial class Refund
{
    [Range(0.01, 1_000_000.0)]
    public decimal Amount { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }
}
```

```http
POST /refunds
Idempotency-Key: 6f1c0d2e-8a4b-4c7d-9e3f-5a2b1c0d9e8f
Content-Type: application/json

{ "amount": 25.5, "reason": "Hasarlı ürün" }
```

İlk istek kaydı oluşturur ve `201 Created` döner. Aynı anahtar ve aynı gövdeyle ikinci istek yeni bir kayıt oluşturmaz;
saklanan yanıtı aynen (durum kodu, gövde, `Location` ve `ETag` başlıklarıyla) döndürür ve bir başlık ekler:

```http
HTTP/1.1 201 Created
Location: /refunds/019a8fd3-9b0c-7d1e-8f2a-3b4c5d6e7f80
Idempotent-Replayed: true
```

### Hangi uçlar korunur

Kaydı oluşturan uçlar ve geçişler: tekil oluşturma, `batch-create`, kopya (`clone`), kayıtlar arası zincir işlemleri
(`reverse`, `supersede`, `amend`, `from-...`), geçişler, entity aksiyonları ve işin yeniden çalıştırılması (`rerun`).
`MapBalance` kaynağında düzeltme, mutlak yazma, taşıma ve tutma uçları. `PUT`, `PATCH` ve `DELETE` korunmaz; onlar zaten
tekrara dayanıklıdır veya `If-Match` ile korunur. Entity'ye bağlı olmayan `MapAction` uçları kapsam dışıdır.

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Başlık yok, `required: false` (varsayılan) | Koruma uygulanmaz; istek her zamanki gibi işlenir. |
| Başlık yok, `required: true` | 400 |
| Anahtar 255 karakterden uzun | 400 |
| Aynı anahtar, aynı gövde | İlk yanıt döner; `Idempotent-Replayed: true`. |
| Aynı anahtar, farklı gövde | 422; işlem yapılmaz. |
| Aynı anahtarla eşzamanlı iki istek | İkincisi birincinin bitmesini bekler, sonra onun yanıtını alır. Tek kayıt oluşur. |

```json
{
  "title": "Idempotency key already used",
  "status": 422,
  "detail": "This Idempotency-Key was already used with a different request. Use a new key for a new request."
}
```

- Anahtar, işlemin kendisiyle aynı transaction'da saklanır: işlem ya yanıtıyla birlikte kalıcı olur ya da hiç olmamış
  sayılır.
- Yalnızca başarılı (2xx) yanıtlar saklanır. Başarısız istek anahtarla birlikte geri alınır; istemci aynı anahtarla
  isteği düzeltip yeniden gönderebilir.
- Anahtarın kapsamı kiracı, kullanıcı, HTTP yöntemi ve adrestir. Başka bir kiracının aynı anahtarı yeni bir istektir.
- Gövdeler JSON'a çevrilmiş halleriyle karşılaştırılır; boşluk ve alan sırası farkı gövdeyi değiştirmez.
- Yanıtlar `HaldusOptions.IdempotencyRetention` kadar (varsayılan 24 saat) saklanır; sonra bakım onları siler ve aynı
  anahtar yeni bir istek sayılır.
- Anahtarlar `HaldusIdempotencyRecords` tablosunda durur; tablo yalnızca `Idempotent` bir kaynak varsa modele eklenir.

| Seçenek | Varsayılan | Anlamı |
|---|---|---|
| `required` | `false` | Başlık zorunlu mu; zorunluysa eksik istek 400 alır. |

`Idempotent`; `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapJob` ve `MapBalance` ile birleşir. Kaynağın ne oluşturma
işlemi ne de geçişi varsa etkisizdir ve proje derlenmez (`HALDUS007`).

## Geçerlilik dönemleri

Bazı kayıtlar belirli bir dönemde geçerlidir: bir tedarikçinin fiyatı, bir çalışanın maaşı, bir vergi oranı. Yeni fiyat
eskisinin yerine geçer, ama eskisi geçmiş için doğru kalmalıdır. `.EffectiveDated(...)` kayda bir dönem ekler ve aynı
kapsamda dönemlerin çakışmasını engeller.

<!-- example: effective-dated -->
```csharp
app.MapCrud<Supplier>();

app.MapCrud<SupplierRate>()
    .Under<Supplier>()
    .EffectiveDated(rate => rate.Currency);

public partial class Supplier
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class SupplierRate
{
    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "TRY";

    [Range(0.0, 1_000_000.0)]
    public decimal UnitPrice { get; set; }
}
```

| Alan | Tipi | Anlamı |
|---|---|---|
| `effectiveFrom` | `DateOnly` | Dönemin ilk günü (dahil). Zorunludur. |
| `effectiveTo` | `DateOnly?` | Dönemin bittiği gün (hariç). Boşsa dönem açık uçludur. |

İki alan da istemcinin yazdığı sıradan alanlardır. Dönem yarı açıktır: `effectiveTo` olan gün artık dönemin içinde
değildir, böylece bir dönemin bittiği gün sonrakinin başladığı gün olabilir.

`EffectiveDated(...)` içinde sayılan alanlar dönemlerin ayrı tutulduğu kapsamı genişletir: yukarıda her tedarikçinin her
para birimi için ayrı bir fiyat dizisi vardır. Kapsamın geri kalanı kaydın görünürlüğüdür: kiracı, sahip ve ebeveyn.

```http
POST /suppliers/019a8fe4-0c1d-7e2f-9a3b-4c5d6e7f8091/supplier-rates
Content-Type: application/json

{ "currency": "TRY", "unitPrice": 10, "effectiveFrom": "2026-01-01" }
```

```json
{
  "id": "019a8fe4-1d2e-7f3a-8b4c-5d6e7f809102",
  "currency": "TRY",
  "unitPrice": 10,
  "effectiveFrom": "2026-01-01",
  "effectiveTo": null,
  "supplierId": "019a8fe4-0c1d-7e2f-9a3b-4c5d6e7f8091"
}
```

Haziranda fiyat değişir. Yeni kayıt açık uçlu dönemin içinde başladığı için Haldus öncekini, yenisinin başladığı gün
kendiliğinden kapatır:

```http
POST /suppliers/019a8fe4-0c1d-7e2f-9a3b-4c5d6e7f8091/supplier-rates
Content-Type: application/json

{ "currency": "TRY", "unitPrice": 12, "effectiveFrom": "2026-06-01" }
```

İlk kaydın `effectiveTo` alanı artık `"2026-06-01"`'dir.

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/suppliers/{supplierId}/supplier-rates?asOf=2026-03-01` | O gün geçerli olan kayıtları listeler. |

`?asOf=2026-03-01` yukarıda 10'luk fiyatı, `?asOf=2026-06-01` 12'liği döndürür; `?asOf=2025-12-31` boştur.

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| `effectiveFrom` gönderilmedi (oluşturmada, `PUT`'ta) ya da `PATCH`'te `null` gönderildi | 400, hata `effectiveFrom` alanında. |
| `effectiveTo`, `effectiveFrom`'dan sonra değil | 400, hata `effectiveTo` alanında. |
| Dönem, kapsamdaki başka bir kaydın dönemiyle çakışıyor (oluşturmada veya güncellemede) | 409 |
| Yeni dönem, kapsamdaki tek açık uçlu dönemin içinde başlıyor | Kabul edilir; önceki dönem yeninin başladığı gün kapanır. |

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Periods overlap",
  "status": 409,
  "detail": "Another SupplierRate is already effective during this period."
}
```

- Çakışma denetimi kapsamın kilidi altında yapılır: aynı kapsama aynı anda yazan istekler sırayla işlenir ve dönemler
  hiçbir zaman çakışmaz. Veritabanında ayrıca bir aralık kısıtı yoktur.
- Kural kaydetmenin kendisine bağlıdır: oluşturma, `PUT`, `PATCH` ve toplu oluşturma aynı denetimden geçer.
- Silme serbesttir ve araya boşluk bırakabilir; bir dönemi kısaltmak da öyle.

`EffectiveDated` yalnızca `MapCrud` ile birleşir; `Publishable` ile birleşmez (`HALDUS007`).

## Tekrar serisi

`.Recurring()` kaydı yinelenen bir seri yapar: haftalık toplantı, aylık bakım. Kayıt bir kez saklanır; tek tek oluşları
saklanmaz, her okumada kuraldan hesaplanır. Tek bir oluş ertelenebilir veya iptal edilebilir.

<!-- example: recurring -->
```csharp
app.MapCrud<Meeting>()
    .Recurring();

public partial class Meeting
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [Range(5, 1440)]
    public int DurationMinutes { get; set; } = 30;
}
```

### Kuralın alanları

`Recurring` kayda şu yazılabilir alanları ekler:

| Alan | Tipi | Anlamı |
|---|---|---|
| `startsAt` | `DateTimeOffset` | İlk oluşun zamanı; sonrakiler buna göre hesaplanır. Zorunludur. |
| `recurrenceFrequency` | `None`, `Daily`, `Weekly`, `Monthly`, `Yearly` | Yinelenme sıklığı. `None` tek bir oluştur. Adıyla yazılır ve okunur. |
| `recurrenceInterval` | `int?` (1–1000) | Kaç aralıkta bir; iki haftada bir için `2`. Boşsa 1. |
| `recurrenceUntil` | `DateTimeOffset?` | Bu zamandan sonra oluş yoktur. |
| `recurrenceCount` | `int?` (1–1000) | Serinin en çok kaç oluşu olduğu. Dönemleri değil oluşları sayar. |
| `recurrenceByDay` | `string?` | Oluşların düştüğü günler, iCalendar'ın `BYDAY` yazımıyla: `MO,WE,FR`. Aylık ve yıllık seride gün bir sıra alabilir: `2TU` ayın ikinci salısı, `-1FR` son cuması. Boşsa oluşlar ilk oluşun gününe düşer. |
| `recurrenceTimeZone` | `string?` | IANA saat dilimi (`Europe/Istanbul`). Verilirse oluşlar o dilimin duvar saatini korur: yaz saatine geçilince toplantı yine 09.00'dadır. Boşsa oluşlar ilk oluşun UTC farkıyla hesaplanır. |

`recurrenceUntil` ve `recurrenceCount` ikisi de boşsa seri açık uçludur.

```http
POST /meetings
Content-Type: application/json

{
  "title": "Haftalık toplantı",
  "durationMinutes": 45,
  "startsAt": "2027-01-04T09:00:00+00:00",
  "recurrenceFrequency": "Weekly",
  "recurrenceCount": 5
}
```

### Oluş uçları

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/meetings/{id}/occurrences?from=&to=` | Aralıktaki oluşları listeler. |
| `PUT` | `/meetings/{id}/occurrences/{start}` | Tek bir oluşu erteler veya iptal eder; oluşu döndürür. |
| `DELETE` | `/meetings/{id}/occurrences/{start}` | Oluşu kurala geri döndürür; oluşu döndürür. |

```http
GET /meetings/019a8ff5-2e3f-7a4b-9c5d-6e7f8091a2b3/occurrences
```

```json
[
  { "originalStart": "2027-01-04T09:00:00+00:00", "start": "2027-01-04T09:00:00+00:00", "cancelled": false },
  { "originalStart": "2027-01-11T09:00:00+00:00", "start": "2027-01-11T09:00:00+00:00", "cancelled": false },
  { "originalStart": "2027-01-18T09:00:00+00:00", "start": "2027-01-18T09:00:00+00:00", "cancelled": false },
  { "originalStart": "2027-01-25T09:00:00+00:00", "start": "2027-01-25T09:00:00+00:00", "cancelled": false },
  { "originalStart": "2027-02-01T09:00:00+00:00", "start": "2027-02-01T09:00:00+00:00", "cancelled": false }
]
```

| Alan | Anlamı |
|---|---|
| `originalStart` | Kuralın verdiği zaman. Oluşun kimliği budur. |
| `start` | Oluşun gerçek zamanı; ertelendiyse yeni zaman. |
| `cancelled` | Oluş iptal edildi mi. İptal edilen oluş listede kalır. |

`from` ve `to` verilmezse aralık ilk oluştan başlayıp bir yıl sürer; yalnızca `from` verilirse `from`'dan bir yıl.
İki sınır da dahildir. Yanıt sayfalı değildir ve en çok 500 oluş içerir.

İkinci oluşu bir gün ertelemek:

```http
PUT /meetings/019a8ff5-2e3f-7a4b-9c5d-6e7f8091a2b3/occurrences/2027-01-11T09:00:00Z
Content-Type: application/json

{ "start": "2027-01-12T11:00:00+00:00" }
```

```json
{ "originalStart": "2027-01-11T09:00:00+00:00", "start": "2027-01-12T11:00:00+00:00", "cancelled": false }
```

İptal etmek için gövde `{ "cancelled": true }`'dur. `DELETE` aynı adrese gönderilirse oluş kurala geri döner. Adresteki
`{start}` bir zaman olduğu için `+` işareti `%2B` olarak yazılmalıdır; UTC zamanlar için `Z` sonekini kullanabilirsiniz.

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Kayıt yok veya görünmüyor | 404 |
| `{start}` kuralın verdiği bir oluş değil (yanlış gün veya serinin sonundan sonra) | 404 |
| `recurrenceInterval` veya `recurrenceCount` 1–1000 dışında | 400 |
| `recurrenceByDay` geçersiz; aylık veya yıllık olmayan seride sıralı gün (`2TU`) | 400 |
| `recurrenceTimeZone` bilinen bir IANA adı değil | 400 |

- Oluşlar dönem dönem, ilk oluştan sayılarak hesaplanır: ayın 31'inde başlayan aylık seri şubatta ayın son gününe düşer,
  martta yine 31'ine döner. Ayda o sıra yoksa (beşinci cuma) o ay oluş yoktur.
- Seri ilk oluştan önce başlamaz: çarşamba başlayan `MO,WE` serisinde o haftanın pazartesisi yoktur.
- Kural değişince oluşlar da değişir. Eski kurala ait istisnalar artık hiçbir oluşa uymaz ve yok sayılır (silinmez).
- Tek bir oluşu değiştirmek serinin bir yazmasıdır: `If-Match` (`ConcurrencyChecked`), düzenlenebilir durum
  (`EditableIn`) ve onaylanmış kaydın kilidi uygulanır; serinin sürümü ilerler ve toplanan onaylar düşer.

`Recurring` yalnızca `MapCrud` ile birleşir.

## Birlikte kullanım

| Bildirim | Birleştiği archetype'lar | Not |
|---|---|---|
| `SoftDeletable` | `MapCrud`, `MapReadOnly` | Dosyalarla (`MapFile`) ve `KeyStrategy.Client` ile birleşmez. |
| `Archivable` | `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapStore`, `MapLookup` | |
| `ConcurrencyChecked` | `MapCrud` | Güncellemesi veya silmesi olan kaynakta `Batch` ile birleşmez. |
| `Expiring` | `MapCrud`, `MapAppendOnly` | `Publishable` ve `KeyStrategy.Client` ile birleşmez. |
| `Retention` | `MapCrud`, `MapReadOnly`, `MapAppendOnly` | `MapFile` ve `Publishable` ile birleşmez. |
| `Idempotent` | `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapJob`, `MapBalance` | Oluşturma veya geçiş gerekir. |
| `EffectiveDated` | `MapCrud` | `Publishable` ile birleşmez. |
| `Recurring` | `MapCrud` | |

- `SoftDeletable` ve `Expiring` kayıtları gizler; bu yüzden `KeyStrategy.Client` ile birleşmezler
  ([anahtar biçimi](fields.md#anahtar-biçimi)).
- `ConcurrencyChecked` ve `Idempotent` birlikte kullanılabilir: aynı anahtarla yinelenen bir geçiş, eski sürümü gönderse
  de ilk yanıtı alır.

Bir archetype'la veya başka bir bildirimle çelişen yazım sessizce yok sayılmaz; proje `HALDUS007` ile derlenmez.

## Sınırlar

- Silinmiş kayıtlar kendiliğinden kalıcı olarak silinmez; `purge` veya [ölçüte göre silme](record-operations.md#ölçüte-göre-silme)
  gerekir.
- Arşivleme kaydı kilitlemez; arşivlenmiş kaydın yazılmasını engellemek gerekiyorsa bunu bir durum makinesiyle yapın.
- Sürüm yalnızca `ETag` başlığındadır, gövdede yoktur; liste öğeleri sürüm taşımaz.
- Süresi dolmuş kayıtlar `PurgeExpiredAfter` verilmedikçe veritabanında kalır. `Retention` ve süresi dolmuş kayıtların
  silinmesi olay, sürüm ve değişiklik akışı üretmez.
- `Idempotent`, entity'ye bağlı olmayan `MapAction` uçlarını korumaz.
- Geçerlilik dönemleri gün çözünürlüğündedir; saat düzeyinde dönem yoktur.
- Tekrar serisi iCalendar `RRULE`'unun tamamı değildir: `BYMONTHDAY`, `BYSETPOS`, `BYMONTH`, `WKST` (hafta pazartesi
  başlar), `EXDATE` / `RDATE` yoktur. Kayıt silinince istisnaları kalır; oluş başına ayrı alan (farklı başlık)
  taşınmaz; oluşlara göre süzme, "bu aralıkta olanlar" sorgusu ve çakışma denetimi yoktur.

## Ayrıca bakın

- [Ortak kurallar](../concepts/conventions.md): `PUT` ile `PATCH`, hata biçimi.
- [Listeleme ve arama](querying.md): `includeDeleted`, `includeArchived`, `asOf` ve kendiliğinden gelen öteki süzgeçler.
- [Durum makineleri](state-machines.md): geçişler ve `EditableIn`.
- [Kayıt işlemleri](record-operations.md): toplu işlemler, kopya, ölçüte göre silme.
- [Üretim](production.md): bakım servisi ve veri saklama.
- [Yapılandırma](../reference/configuration.md): `HaldusOptions`.
