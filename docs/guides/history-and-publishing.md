# Sürümler, yayın ve okundu bilgisi

Bu sayfa bir kaydın zaman içindeki halleriyle ilgili bildirimleri anlatır: her yazmadan sonraki halin saklanması ve
geri yüklenmesi (`Revisioned`), taslak düzenlenirken yayındaki halin korunması (`Publishable`) ve ileri tarihli yayın
(`ScheduledPublishing`), "şundan beri ne değişti" sorusunu yanıtlayan değişiklik akışı (`ChangeFeed`) ve kayıtların
okunup okunmadığı (`ReadTracking`, `ReadReceipts`).

Örneklerde web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs` içindedir; `builder` ve
`app` değişkenleri [kurulum](../getting-started/installation.md) sayfasındaki gibi tanımlıdır.

## İçindekiler

- [Sürümler](#sürümler)
- [Taslak ve yayın](#taslak-ve-yayın)
- [Zamanlanmış yayın](#zamanlanmış-yayın)
- [Değişiklik akışı](#değişiklik-akışı)
- [Okundu takibi](#okundu-takibi)
- [Okundu işaretleri](#okundu-işaretleri)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Sürümler

`Revisioned()` kaydın her yazmadan sonraki halini saklar. Saklanan, bir fark günlüğü değil kaydın anlık
görüntüsüdür: alanları ve gömülü satırlarıyla birlikte. Bir sürüm kaydın kendi yanıt biçiminde okunur ve kayıt o
sürüme geri yüklenebilir.

Görüntüyü, kaydı hangi kod yazarsa yazsın, Haldus kaydederken alır: üretilen uçlar, toplu uçlar, geçişler,
aksiyonlar, arka plan işleri ve uygulamanın kendi kodu. Yalnızca satırları değişen kayıt da (ör. bir aksiyon
bölümleri sildiyse) yeni bir sürüm alır.

<!-- example: revisions -->
```csharp
var articles = app.MapCrud<Article>()
    .Revisioned();

articles.MapDetail<ArticleSection>();

public partial class Article : IComputed
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [MaxLength(10_000)]
    public string? Body { get; set; }

    public int SectionCount { get; private set; }

    public void Compute() => SectionCount = ArticleSections.Count;
}

public partial class ArticleSection
{
    [Required]
    [MaxLength(200)]
    public string Heading { get; set; } = "";

    [MaxLength(10_000)]
    public string? Text { get; set; }
}
```

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `GET /articles/{id}/revisions` | Kaydın sürümleri, en yenisi önce; sayfalıdır (`?page=&pageSize=`). |
| `GET /articles/{id}/revisions/{revisionId}` | Kaydın o sürümdeki hali, kaydın kendi yanıt biçiminde. |
| `POST /articles/{id}/revisions/{revisionId}/rollback` | Kaydı o sürüme geri yükler; kaydın yeni halini döndürür. |

```http
GET /articles/0193a5c6-1000-7a00-8b00-1c2d3e4f5001/revisions
```

```json
{
  "items": [
    { "id": 1207, "createdAt": "2026-11-25T10:02:41.118+00:00", "createdBy": "editor-7" },
    { "id": 1185, "createdAt": "2026-11-25T09:47:05.903+00:00", "createdBy": "editor-7" }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 2
}
```

Sürümün kimliği (`id`) bir sayıdır ve kaydın sürümleri arasında artan sıradadır. `createdBy`, o yazmayı yapan
kullanıcıdır.

```http
GET /articles/0193a5c6-1000-7a00-8b00-1c2d3e4f5001/revisions/1185
```

```json
{
  "id": "0193a5c6-1000-7a00-8b00-1c2d3e4f5001",
  "title": "First title",
  "body": "Draft",
  "sectionCount": 2,
  "articleSections": [
    { "id": "0193a5c6-1000-7a00-8b00-1c2d3e4f5002", "heading": "Intro", "text": null },
    { "id": "0193a5c6-1000-7a00-8b00-1c2d3e4f5003", "heading": "Outro", "text": null }
  ]
}
```

### Geri yükleme

`POST /{id}/revisions/{revisionId}/rollback` bir güncellemedir:

- Güncellenebilir alanlar ve gömülü satırlar o sürümdeki değerlerine döner. Satırlar kimlikleriyle geri gelir: hâlâ
  duran satır güncellenir, silinmiş olan aynı kimlikle yeniden eklenir, sürümde olmayan satır silinir. Satırların
  sırası da geri gelir.
- Hesaplanan alanlar (`IComputed`) yeniden hesaplanır.
- Geri gelen referanslar yeniden doğrulanır; o arada silinmiş veya görünmez olmuş bir kayda geri dönülemez (`400`).
- Güncelleme korumaları geçerlidir: kayıt `EditableIn` ile izin verilmeyen bir durumdaysa, onaylanmışsa veya bir kayıt
  zincirinde kapanmışsa `409`; kaynak `ConcurrencyChecked` ise `If-Match` istenir (`428` / `412`).
- Geri yükleme de bir yazmadır: yeni bir sürüm oluşturur; eski sürümler durur.

Geri yüklenmeyenler: yalnızca oluştururken yazılan alanlar (`CreateOnly`), ağaçtaki yer (`Hierarchical` ile gelen
`ParentId`), Haldus'un yönettiği alanlar (durum, numara, zaman damgaları) ve hassas alanlar (bkz.
[hassas ve şifreli alanlar](sensitive-data.md)).

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Kayıt yok veya isteği yapana görünmüyor | `404` |
| Sürüm yok veya başka bir kayda ait | `404` |
| Geri yükleme, kaydın düzenlenemediği bir durumda | `409` |

Hiçbir şeyi değiştirmeyen bir yazma (ör. aynı başlığı yeniden gönderen bir `PATCH`) yeni bir sürüm oluşturmaz.

## Taslak ve yayın

`Publishable()` kaydı taslak ve yayın olarak ikiye ayırır. Kaynağın sıradan uçları her zaman taslağı okur ve yazar.
Yayın, taslağın o andaki halinin (alanları, gömülü satırları ve hesaplanan alanlarıyla) bir kopyasıdır; taslak sonra
değişse de yayındaki hal bir sonraki yayına kadar aynı kalır. Bir makaleyi okurlar eski haliyle okurken yeniden
yazmak için kullanılır.

<!-- example: publishing -->
```csharp
var articles = app.MapCrud<Article>()
    .Publishable()
    .NaturalKey(article => article.Slug);

articles.MapDetail<ArticleSection>();

public partial class Article
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [Required]
    [MaxLength(200)]
    public string Slug { get; set; } = "";

    [MaxLength(10_000)]
    public string? Body { get; set; }
}

public partial class ArticleSection
{
    [Required]
    [MaxLength(200)]
    public string Heading { get; set; } = "";
}
```

`PublishedAt` alanı üretilir: taslağın son yayınlandığı an; yayında değilse boştur. Yalnızca yayın uçları yazar.

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `POST /articles/{id}/publish` | Taslağın o andaki halini yayına alır (önceki yayını değiştirir); taslağı döndürür. |
| `POST /articles/{id}/unpublish` | Yayından kaldırır; taslağı döndürür. Yayında olmayan kayıtta bir şey değiştirmez. |
| `GET /articles/published` | Yayındaki kayıtlar, en son yayınlanan önce; sayfalıdır. |
| `GET /articles/published/{id}` | Bir kaydın yayındaki hali. |

Yayındaki hal taslağın kimliğiyle okunur: `GET /articles/published/{id}` adresindeki `id`, taslağın kimliğidir.

```http
POST /articles/0193a5c7-2000-7b00-8c00-2d3e4f506001/publish
```

```json
{
  "id": "0193a5c7-2000-7b00-8c00-2d3e4f506001",
  "title": "Launch",
  "slug": "launch",
  "body": "v1",
  "publishedAt": "2026-11-25T11:30:12.406+00:00",
  "articleSections": [
    { "id": "0193a5c7-2000-7b00-8c00-2d3e4f506002", "heading": "Why" },
    { "id": "0193a5c7-2000-7b00-8c00-2d3e4f506003", "heading": "How" }
  ]
}
```

Taslak sonra değişir (`PUT /articles/{id}` ile başlığı `"Launch (rewritten)"` olur); yayındaki hal değişmez:

```http
GET /articles/published/0193a5c7-2000-7b00-8c00-2d3e4f506001
```

```json
{
  "id": "0193a5c7-2000-7b00-8c00-2d3e4f506001",
  "title": "Launch",
  "slug": "launch",
  "body": "v1",
  "publishedAt": "2026-11-25T11:30:12.406+00:00",
  "articleSections": [
    { "id": "0193a5c7-2000-7b00-8c00-2d3e4f506002", "heading": "Why" },
    { "id": "0193a5c7-2000-7b00-8c00-2d3e4f506003", "heading": "How" }
  ]
}
```

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Kayıt yok (`publish`, `unpublish`) | `404` |
| Kayıt yayınlanmamış (`GET /published/{id}`) | `404` |
| Yayındaki kayıt siliniyor | `409`; önce `unpublish` |
| Aynı taslağı aynı anda yayınlayan iki istek | Biri geçer, öteki `409` alır; taslağın tek bir yayın kopyası olur |
| Kaynak `ConcurrencyChecked` ve `If-Match` yok / eski (`publish`, `unpublish`) | `428` / `412` |

- Yayın kopyası yalnızca yayın uçlarında görünür. Liste, tekil okuma, doğal anahtarla okuma, arama, değişiklik akışı
  ve sürümler yalnızca taslakları görür.
- Doğal anahtar (`NaturalKey`) taslaklar arasında benzersizdir; kopyanın aynı değeri taşıması yayını engellemez.
- Yayındaki kayıtlar kaynağın filtresiyle (`Filterable`) süzülür ve `?locale=` ile çevrilir (`Translatable`);
  aranamaz (`/search`) ve sıralanamaz.
- Taslağın sürümleri (`Revisioned`) yayını da bir yazma olarak sayar: yayın, taslağın `PublishedAt` alanını yazar.

## Zamanlanmış yayın

`ScheduledPublishing()` taslağın yayınını ileri bir zamana kurar. `Publishable()` ile birlikte yazılır; tek başına
`HALDUS007`'dir.

<!-- example: scheduled -->
```csharp
// Zamanı gelen taslakları bakım servisi yayınlar; aralığı buradan ayarlanır (varsayılan bir saat).
builder.Services.AddHaldus<AppDbContext>(options => options.MaintenanceInterval = TimeSpan.FromMinutes(5));

var app = builder.Build();

app.MapCrud<Article>()
    .Publishable()
    .ScheduledPublishing();

public partial class Article
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [MaxLength(10_000)]
    public string? Body { get; set; }
}
```

`ScheduledAt` alanı üretilir: taslağın yayına alınacağı an. Yalnızca zamanlama uçları ve yayının kendisi yazar.

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `POST /articles/{id}/schedule` | Gövdedeki `publishAt` anını taslağa yazar; taslağı döndürür. Yayınlamaz. |
| `DELETE /articles/{id}/schedule` | Zamanlamayı kaldırır; taslağı döndürür. |

```http
POST /articles/0193a5c7-2000-7b00-8c00-2d3e4f506001/schedule
Content-Type: application/json

{ "publishAt": "2026-12-01T08:00:00Z" }
```

```json
{
  "id": "0193a5c7-2000-7b00-8c00-2d3e4f506001",
  "title": "Launch",
  "body": "v1",
  "publishedAt": null,
  "scheduledAt": "2026-12-01T08:00:00+00:00"
}
```

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| `publishAt` gönderilmedi | `400` |
| Kayıt yok | `404` |
| Kaynak `ConcurrencyChecked` ve `If-Match` yok / eski | `428` / `412` |

- Zamanı gelen taslağı arka plandaki bakım servisi (`HaldusMaintenance`) o andaki haliyle yayına alır. Zamanlandıktan
  sonra yapılan düzenlemeler de yayına girer. Yayınlanan taslağın zamanlaması düşer.
- Yayın, zamanından en çok bir bakım aralığı (`HaldusOptions.MaintenanceInterval`, varsayılan bir saat) sonra
  gerçekleşir. Geçmiş bir an da verilebilir: taslak bir sonraki bakım turunda yayınlanır.
- Elle yayın (`POST /{id}/publish`) bekleyen zamanlamayı düşürür.
- Bakım her taslağı kendi kiracısı ve sahibi adına, kaynağın kendi filtrelerinden geçirerek yayınlar. Kendi
  filtrelerinden geçemeyen taslak (silinmiş sayılan, üst kaydı görünmeyen) yayınlanmaz ve zamanlaması düşürülür.
  Yayınlanamayan bir taslak atlanır; sıradaki taslakları bekletmez.
- Testlerde bakımı beklemeden çalıştırabilirsiniz: `await HaldusMaintenance.RunAsync(services, cancellationToken)`.

## Değişiklik akışı

`ChangeFeed()` "şu imleçten beri ne değişti" sorusunu yanıtlar. Bir istemci (mobil uygulamanın yerel önbelleği, bir
arama dizini, başka bir sistem) kaynağı baştan okumadan güncel tutar. Akış, imleçten sonra değişen kayıtların şimdiki
halini ve artık bulunamayan kayıtların kimliklerini verir.

Değişikliği, kaydı hangi kod yazarsa yazsın, Haldus kaydederken günlüğe yazar; üretilen uçlarda akışla ilgili kod
yoktur.

<!-- example: change-feed -->
```csharp
app.MapCrud<Article>()
    .Auditable()
    .ChangeFeed();

public partial class Article
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";
}
```

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `GET /articles/changes?since={imleç}&pageSize={n}` | İmleçten sonraki değişiklikler. |

| Sorgu parametresi | Varsayılan | Anlamı |
|---|---|---|
| `since` | `0` (baştan) | Bir önceki yanıtın `next` değeri. |
| `pageSize` | `100` | Okunacak günlük satırı; en çok `500`. |

```http
GET /articles/changes?since=4810
```

```json
{
  "items": [
    {
      "id": "0193a5c8-3000-7c00-8d00-3e4f50617001",
      "deleted": false,
      "record": {
        "id": "0193a5c8-3000-7c00-8d00-3e4f50617001",
        "title": "Kept and edited",
        "createdAt": "2026-11-25T12:00:01.204+00:00",
        "updatedAt": "2026-11-25T12:00:03.877+00:00"
      },
      "changedAt": "2026-11-25T12:00:03.877+00:00"
    },
    {
      "id": "0193a5c8-3000-7c00-8d00-3e4f50617002",
      "deleted": true,
      "record": null,
      "changedAt": "2026-11-25T12:00:04.512+00:00"
    }
  ],
  "next": 4815,
  "hasMore": false
}
```

| Alan | Anlamı |
|---|---|
| `items[].id` | Kaydın anahtarı, metin olarak. |
| `items[].deleted` | Kayıt artık bulunamıyor: silindi ya da isteği yapana görünmez oldu (yumuşak silinmiş, süresi dolmuş). `record` boştur. |
| `items[].record` | Kaydın şimdiki hali, kaynağın yanıt biçiminde. |
| `items[].changedAt` | Kaydın bu sayfadaki son değişikliğinin zamanı. |
| `next` | Bir sonraki istekte `since` olarak gönderilecek imleç. |
| `hasMore` | Bu imleçten sonra okunmayı bekleyen değişiklik var mı; varsa hemen yeniden isteyin. |

### Kurallar

- Bir kayıt sayfada kaç kez değişirse değişsin bir kez yer alır ve şimdiki haliyle döner. `pageSize` kayıtları değil
  günlük satırlarını sayar; bu yüzden sayfa `pageSize` değerinden az kayıt taşıyabilir.
- Akış isteği yapanın görebildiklerini verir: kiracıya ait kaynakta yalnızca o kiracının, kullanıcıya ait kaynakta
  yalnızca o kullanıcının değişiklikleri. Ebeveyn altındaki bir kaynakta akış ebeveynin adresindedir
  (`/discussions/{discussionId}/replies/changes`) ve ebeveyn görünmüyorsa `404` döner.
- Yeni bir değişiklik yoksa sayfa boştur ve `next` ilerlemez.
- `since` sayı değilse `400` döner.

### Güvenlik payı (`SafetyLag`)

Günlükteki sıra numarası satır eklenirken, görünürlük ise transaction bitince belirlenir. Uzun süren bir
transaction'ın satırı, kendisinden sonra başlayıp önce biten bir transaction'ın satırının arkasında görünür olabilir;
akış hemen ilerleseydi o satırı atlardı. Bu yüzden akış en yeni değişikliklerin `HaldusChanges.SafetyLag` kadar
(varsayılan iki saniye) gerisinden gelir ve yeterince eskimemiş ilk satırda durur; imleç o satırı atlamaz.

`HaldusChanges.SafetyLag` süreç genelinde bir ayardır. Testlerde beklememek için sıfırlanabilir:

<!-- example: safety-lag -->
```csharp
HaldusChanges.SafetyLag = TimeSpan.Zero;

app.MapCrud<Article>()
    .ChangeFeed();

public partial class Article
{
    [MaxLength(200)]
    public string Title { get; set; } = "";
}
```

Bu paydan daha uzun süren bir transaction'ın satırları yine atlanabilir; bkz. [Sınırlar](#sınırlar).

## Okundu takibi

`ReadTracking()` bir kullanıcıya ait kayıtların (bildirim, mesaj) okunup okunmadığını kaydın kendisinde tutar.
`ReadAt` alanı üretilir: kaydın okundu işaretlendiği an; okunmadıysa boştur. Kayıt tek bir kullanıcıya ait
olmalıdır; bu yüzden `OwnedByCurrentUser()` ister. Paylaşılan kayıtlar için [okundu işaretleri](#okundu-işaretleri)
kullanılır.

<!-- example: read-tracking -->
```csharp
// Bildirimi sistem yazar; kullanıcı yalnızca okur ve okundu işaretler: /me/notifications
app.MapReadOnly<Notification>()
    .OwnedByCurrentUser()
    .ReadTracking();

public partial class Notification
{
    [MaxLength(200)]
    public string Title { get; set; } = "";
}

// Başka bir kullanıcıya ait kaydı oluşturan sistem kodu o kullanıcı olarak çalışır; sahibi Haldus atar.
public sealed class Notifier(AppDbContext db)
{
    public async Task NotifyAsync(Guid? tenantId, string userId, string title, CancellationToken cancellationToken)
    {
        using (HaldusAmbient.Use(tenantId, userId))
        {
            db.Set<Notification>().Add(new Notification { Id = Guid.CreateVersion7(), Title = title });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
```

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `POST /me/notifications/{id}/read` | Kaydı okundu işaretler; kaydı döndürür. Zaten okunmuşsa ilk okunma zamanı değişmez. |
| `POST /me/notifications/{id}/unread` | İşareti kaldırır (`readAt` boşalır); kaydı döndürür. |
| `POST /me/notifications/read-all` | Kullanıcının okunmamış bütün kayıtlarını okundu işaretler. |
| `GET /me/notifications/unread-count` | Okunmamış kayıtların sayısı. |
| `GET /me/notifications?unread=true` | Liste yalnızca okunmamışlar (`false`: yalnızca okunmuşlar). |

```http
POST /me/notifications/0193a5c9-4000-7d00-8e00-4f5061728001/read
```

```json
{
  "id": "0193a5c9-4000-7d00-8e00-4f5061728001",
  "title": "Order shipped",
  "readAt": "2026-11-25T13:05:44.019+00:00"
}
```

```http
POST /me/notifications/read-all
```

```json
{ "updated": 2 }
```

```http
GET /me/notifications/unread-count
```

```json
{ "count": 0 }
```

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Kullanıcısı çözülemeyen istek | `401` |
| Kayıt başka bir kullanıcıya ait veya yok | `404` |
| Kaynak `ConcurrencyChecked` ve `If-Match` yok / eski (`read`, `unread`) | `428` / `412` |

- Okundu bilgisi içerik değildir: salt-okunur bir kaynakta (`MapReadOnly`) da değişir. Kaynak `Auditable` ise
  işaretleme `UpdatedAt` alanını ilerletir.
- `unread-count` arşivlenmiş kayıtları (`Archivable`) saymaz.
- `read-all` tek bir toplu güncellemedir: sürümlere (`Revisioned`) ve değişiklik akışına girmez.

## Okundu işaretleri

`ReadReceipts()` paylaşılan kayıtların (duyuru, ortak belge) kullanıcı başına okundu bilgisini tutar. İşaretler
kayıtta değil ayrı bir tabloda (`HaldusReadReceipts`) durur; kaydın kendisi değişmez, yanıtında da okundu bilgisi
yoktur. Herkes aynı duyuruyu görür, her kullanıcının işareti kendisinindir.

<!-- example: read-receipts -->
```csharp
app.MapCrud<Announcement>()
    .Archivable()
    .ReadReceipts();

public partial class Announcement
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [MaxLength(4000)]
    public string? Body { get; set; }
}
```

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `POST /announcements/{id}/read` | Geçerli kullanıcı için okundu işaretler (`204`). Yinelemek bir şey değiştirmez. |
| `POST /announcements/{id}/unread` | İşareti kaldırır (`204`). |
| `GET /announcements/{id}/read-state` | Geçerli kullanıcının okuma durumu. |
| `POST /announcements/read-all` | Görünen kayıtların okunmamışlarını okundu işaretler; istek başına en çok 5000 kayıt. |
| `GET /announcements/unread-count` | Geçerli kullanıcının okumadığı kayıtların sayısı. |
| `GET /announcements?unread=true` | Liste, isteği yapanın okumadıklarıyla süzülür (`false`: okudukları). |

```http
GET /announcements/0193a5ca-5000-7e00-8f00-506172839001/read-state
```

```json
{ "read": true, "readAt": "2026-11-25T14:20:07.733+00:00" }
```

```http
POST /announcements/read-all
```

```json
{ "updated": 2 }
```

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Kullanıcısı çözülemeyen istek (bütün okundu uçları) | `401` |
| Kayıt yok veya isteği yapana görünmüyor | `404` |

- Kayıt hiç yazılmaz. Bu yüzden uçlar sürüm (`If-Match`), onay ve `EditableIn` kurallarına bakmaz; ama kaydı her zaman
  kendi filtrelerinden geçirerek arar: göremediğiniz kaydı işaretleyemez, durumunu okuyamazsınız.
- `unread-count` arşivlenmiş kayıtları saymaz.
- Aynı anda gelen iki işaretleme çakışmaz; işaret bir kez yazılır.

## Birlikte kullanım

| Bildirim | Birleştiği archetype'lar | Birleşmediği bildirimler (`HALDUS007`) |
|---|---|---|
| `Revisioned` | `MapCrud` (ve ayrı uçlu detay) | `SecretOnce`, `TokenAccess` (görüntüler saklanan sırrın kopyasını tutardı); veritabanının ürettiği `int` / `long` anahtar |
| `Publishable` | `MapCrud` (ve ayrı uçlu detay) | `Workflow`, `Document`, `Axis`, `Numbered`, `Expiring`, `SecretOnce`, `TokenAccess`, `Reversible`, `Supersedes`, `Approvable`, `EffectiveDated`, `Retention`, `ReadTracking`, `Counts`, `Sums`, `KeyStrategy.Client`; `int` / `long` anahtar; `Create` işlemi kaldırılmışsa |
| `ScheduledPublishing` | `Publishable` ile | `Publishable` olmadan |
| `ChangeFeed` | `MapCrud`, `MapReadOnly`, `MapAppendOnly` | `Under<T>(shallow: true)`; `int` / `long` anahtar; takılabilir kaynak (`AttachedTo`) |
| `ReadTracking` | `MapCrud`, `MapReadOnly`, `MapAppendOnly` | `OwnedByCurrentUser` olmadan; `ReadReceipts` (adresleri aynıdır); `Publishable` |
| `ReadReceipts` | `MapCrud`, `MapReadOnly`, `MapAppendOnly` | `ReadTracking` |

Sürüm, akış ve yayın, kaydın anahtarının kaydetmeden önce bilinmesini ister; bu yüzden anahtarı veritabanının ürettiği
(`int` / `long` `Id`) bir kaynakta bildirilemezler. Sunucunun ürettiği `Guid` (varsayılan) veya önekli anahtar
kullanın.

<!-- example: read-tracking-shared, error: HALDUS007 -->
```csharp
// Hatalı: okundu bilgisi kaydın kendisinde durur; kayıt tek bir kullanıcıya ait olmalıdır.
// Paylaşılan kayıtlar için ReadReceipts() kullanın.
app.MapCrud<Announcement>()
    .ReadTracking();

public partial class Announcement
{
    [MaxLength(200)]
    public string Title { get; set; } = "";
}
```

## Sınırlar

- **Sürümler:** kayıt silinince sürümleri kalır ve kendiliğinden temizlenmez. Ayrı uçlu detaylar (`AsSubresource`)
  görüntüye girmez. Satırın kendi referansı geri yüklemede yeniden doğrulanmaz. Değişiklik izleyicisini atlayan
  yazmalar (`ExecuteUpdate`, `ExecuteDelete`) sürüm oluşturmaz.
- **Yayın:** kopya yalnızca yazılabilir alanları ve gömülü satırları taşır; Haldus'un yönettiği alanları (durum,
  numara, ömür, onay) taşımaz. Bu yüzden onları yöneten bildirimlerle birleşmez. Ayrı uçlu detaylar kopyaya girmez.
  Yayındaki kayıtlar aranamaz ve sıralanamaz.
- **Zamanlanmış yayın:** zamanlanmış yayından kaldırma yoktur. Bir bakım turu kaynak başına en çok yüz taslak yayınlar;
  kalanlar sonraki turlara kalır. Bakım sırasında çakışan taslak bir sonraki turda yeniden denenir.
- **Değişiklik akışı:** `SafetyLag` süresinden uzun süren bir transaction'ın satırları atlanabilir. Değişiklik
  izleyicisini atlayan yazmalar (`ExecuteUpdate`, `ExecuteDelete`, `read-all`, saklama süresinin toplu silmesi) akışa
  girmez. Günlük kendiliğinden temizlenmez.
- **Okundu takibi:** okundu bilgisi kayıttadır; paylaşılan kayıtlar için değildir.
- **Okundu işaretleri:** kimin okuduğunun listesi ("görenler") yoktur. Kayıt silinince işaretleri kalır. `read-all`
  istek başına 5000 kayıt işaretler; daha fazlası için istek yinelenir.

## Ayrıca bakın

- [Bildirim dizini](../reference/declarations.md)
- [Detaylar ve hesaplanan alanlar](details-and-computed-fields.md)
- [Kiracılar ve sahiplik](tenancy-and-ownership.md)
- [Yaşam döngüsü](lifecycle.md): `ConcurrencyChecked`, `SoftDeletable`, `Archivable`
- [Alanlar](fields.md): `Translatable`, `NaturalKey`
- [Olay yayını](events.md): değişiklikleri itme yoluyla teslim etmek
- [Onay ve belge](approvals.md)
- [Hassas ve şifreli alanlar](sensitive-data.md)
- [Yapılandırma](../reference/configuration.md)
