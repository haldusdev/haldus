# Arka plan işleri

Rapor üretmek, binlerce satırı içe aktarmak, bir dış servisle uzun bir eşitleme yapmak: bazı işler bir HTTP isteğinin
süresine sığmaz. `MapJob` böyle bir işi bir kayıt olarak modeller. İstek işi oluşturur ve hemen `202 Accepted` döner;
iş arka planda çalışır; istemci durumunu, ilerlemesini ve sonucunu aynı kayıttan okur. İşi hangi kuyruğun taşıdığını
siz seçersiniz (bellek, PostgreSQL veya Hangfire); işin yaşam döngüsü her durumda aynıdır.

Örneklerde web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs`'tir; `builder` ve `app`
satırları gösterilmediyse yazılmış sayılır.

## İçindekiler

- [Bir işi bildirmek](#bir-işi-bildirmek)
- [Bir işin mantığı](#bir-işin-mantığı)
- [Başarısızlık](#başarısızlık)
- [Bir işi iptal etmek](#bir-işi-iptal-etmek)
- [Kimlik ve dil](#kimlik-ve-dil)
- [Yeniden teslim ve dayanıklılık](#yeniden-teslim-ve-dayanıklılık)
- [Altyapı](#altyapı)
- [Yeniden çalıştırma](#yeniden-çalıştırma)
- [Dışa aktarma](#dışa-aktarma)
- [Satırları içe aktarma](#satırları-içe-aktarma)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Bir işi bildirmek

İş entity'si işin girdisini ve çıktısını taşır:

- **Girdi**, herkese açık setter'ı olan alanlardır. İş oluşturulurken istekten gelir ve doğrulanır (DataAnnotations).
- **Çıktı**, herkese açık setter'ı olmayan alanlardır. Yalnızca yanıtta görünür; işin mantığı yazar.

<!-- example: report -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddHaldusPostgreSqlJobs<AppDbContext>();

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Order>().MultiTenant();
app.MapJob<ReportExport>().MultiTenant().Auditable(trackUser: true);

app.Run();

public partial class Order
{
    [MaxLength(100)]
    public string CustomerName { get; set; } = "";
}

public partial class ReportExport
{
    [Range(2000, 2100)]
    public int Year { get; set; }

    public int OrderCount { get; private set; }

    public string? DownloadUrl { get; private set; }

    public void Complete(int orderCount, string downloadUrl)
    {
        OrderCount = orderCount;
        DownloadUrl = downloadUrl;
    }
}

public sealed class ReportExportHandler(AppDbContext db) : IJobHandler<ReportExport>
{
    public async Task HandleAsync(ReportExport job, JobContext context, CancellationToken cancellationToken)
    {
        await context.ReportProgressAsync(10, cancellationToken);

        // İş, onu başlatan kiracının kimliğiyle çalışır: yalnızca o kiracının siparişleri sayılır.
        var orders = await db.Set<Order>().CountAsync(cancellationToken);

        await context.ReportProgressAsync(90, cancellationToken);
        job.Complete(orders, $"/downloads/reports/{job.Id}.csv");
    }
}
```

Bir iş altyapısı kaydedilmelidir (burada `AddHaldusPostgreSqlJobs`); kaydedilmemişse ya da işin mantığı bulunamazsa
uygulama ilk istekte değil, `MapJob` satırında, açılırken hata verir. Altyapıların farkı [Altyapı](#altyapı)
bölümündedir.

Haldus entity'ye şu alanları ekler. Hiçbiri istekle yazılamaz; gövdede gönderilseler de yok sayılırlar.

| Alan | Yanıtta | Anlamı |
|---|---|---|
| `Id` | evet | `Guid` (v7). Kendiniz yazarsanız `Guid` olmalıdır; başka bir tip `HALDUS003` olur. |
| `Status` | evet | `Queued`, `Running`, `Succeeded`, `Failed`, `Cancelled`. JSON'da adıyla yazılır. |
| `Progress` | evet | 0–100 arası ilerleme. İş başarıyla bitince 100 olur. |
| `Error` | evet | İş başarısız olduysa, mantığının bildirdiği neden. |
| `CreatedAt`, `StartedAt`, `CompletedAt` | evet | Oluşturma, ilk başlama ve sonuçlanma anları. |
| `Attempts` | hayır | İşin kaç kez çalıştırılmaya başlandığı. |
| `CancelRequested` | hayır | Çalışan iş için iptal istendi mi. |
| `Culture` | hayır | İşi oluşturan isteğin dili. |

Üretilen uçlar:

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/report-exports` | İşleri listeler (sayfalı, oluşturulma sırasıyla). `?status=Failed` ile süzülür. |
| `GET` | `/report-exports/{id}` | İşi okur; istemci durumu buradan izler. |
| `POST` | `/report-exports` | İşi oluşturur, kuyruğa koyar ve hemen 202 döner. |
| `POST` | `/report-exports/{id}/cancel` | İşi iptal eder. |

İş güncellenemez ve silinemez: yaşam döngüsü sabittir.

```http
POST /report-exports
Content-Type: application/json

{ "year": 2026 }
```

```http
HTTP/1.1 202 Accepted
Location: /report-exports/01936f80-5d6e-7f70-8a1b-2c3d4e5f6a7b
```

```json
{
  "id": "01936f80-5d6e-7f70-8a1b-2c3d4e5f6a7b",
  "year": 2026,
  "orderCount": 0,
  "downloadUrl": null,
  "createdAt": "2026-11-25T11:00:00.104512+00:00",
  "updatedAt": null,
  "createdBy": "ada",
  "updatedBy": null,
  "status": "Queued",
  "progress": 0,
  "error": null,
  "startedAt": null,
  "completedAt": null
}
```

İstemci `Location` adresini aralıklarla okur. İş bitince:

```http
GET /report-exports/01936f80-5d6e-7f70-8a1b-2c3d4e5f6a7b
```

```json
{
  "id": "01936f80-5d6e-7f70-8a1b-2c3d4e5f6a7b",
  "year": 2026,
  "orderCount": 2,
  "downloadUrl": "/downloads/reports/01936f80-5d6e-7f70-8a1b-2c3d4e5f6a7b.csv",
  "createdAt": "2026-11-25T11:00:00.104512+00:00",
  "updatedAt": null,
  "createdBy": "ada",
  "updatedBy": null,
  "status": "Succeeded",
  "progress": 100,
  "error": null,
  "startedAt": "2026-11-25T11:00:00.210771+00:00",
  "completedAt": "2026-11-25T11:00:01.004218+00:00"
}
```

Girdinin doğrulaması oluşturma isteğinde yapılır: `{ "year": 1900 }` 400 ve alan düzeyinde `year` hatası döner, iş
oluşmaz.

## Bir işin mantığı

Mantık iki yoldan birinde yazılır.

**Handler sınıfı.** `IJobHandler<TJob>` uygulayan bir sınıf. Haldus onu derleme zamanında bulur ve bağımlılık
enjeksiyonuna kendisi kaydeder (scoped); kurucusu servis alır. Yukarıdaki `ReportExportHandler` böyledir.

**Lambda.** `MapJob`'a verilen bir metot. Lambda bağımlılık enjeksiyonu almaz; servislerini `JobContext.Services`
üzerinden ister. Kısa işler için uygundur.

<!-- example: lambda -->
```csharp
builder.Services.AddHaldusInMemoryJobs();

var app = builder.Build();

app.MapCrud<Product>();

app.MapJob<CatalogRecount>(async (job, context, cancellationToken) =>
{
    var db = context.Services.GetRequiredService<AppDbContext>();
    job.Record(await db.Set<Product>().CountAsync(cancellationToken));
});

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class CatalogRecount
{
    public int ProductCount { get; private set; }

    public void Record(int productCount) => ProductCount = productCount;
}
```

Bir iş için hem lambda hem handler varsa lambda çalışır.

Mantık şunları bilir:

| Ne | Nasıl |
|---|---|
| İşin girdisi | `job` parametresinin alanları. |
| Çıktıyı yazmak | `job`'ın çıktı alanları; setter'ları `private` olduğu için entity'ye bir metot yazılır (`Complete`, `Record`). |
| İlerleme | `await context.ReportProgressAsync(50, cancellationToken)`. Değer 0–100 arasına çekilir ve hemen kaydedilir; istemci işi okuduğunda görür. |
| Servisler | Handler'da kurucu; lambda'da `context.Services`. İkisi de işin kendi kapsamıdır. |
| İptal | `cancellationToken`. İş iptal edilince veya süreç kapanırken tetiklenir. |

Mantık başarıyla dönerse Haldus işi `Succeeded` yapar, `Progress` alanını 100'e çeker ve bağlamı kaydeder. İşin
çıktısı ve aynı kapsamdan aldığınız `DbContext` üzerinde yapıp henüz kaydetmediğiniz değişiklikler birlikte kalıcı olur.
Mantığın kendisi de `SaveChangesAsync` çağırabilir; o zaman o değişiklikler o anda kalıcı olur ve sonradan başarısız
olan bir iş onları geri almaz.

## Başarısızlık

| Mantık ne yaparsa | Sonuç |
|---|---|
| `JobFailedException` fırlatır | `Failed`. Mesaj `Error` alanına yazılır (en çok 2000 karakter) ve istemciye gösterilir. |
| Başka bir istisna fırlatır | `Failed`. `Error` boş kalır; ayrıntı yalnızca günlüğe yazılır (iç bilgileri sızdırabilir). |

İki durumda da mantığın bağlamı atılır: kaydedilmemiş, yarım kalan değişiklikler kaydedilmez. Hata fırlatan iş yeniden
denenmez.

`JobFailedException` mesajı son kullanıcıya gösterildiği için onu çevirmek fırlatanın işidir. İş, onu oluşturan
isteğin diliyle çalıştığı için `IStringLocalizer` doğru dilde metin verir (bkz. [Kimlik ve dil](#kimlik-ve-dil)).

<!-- fragment -->
```csharp
if (orders == 0)
{
    throw new JobFailedException(messages["NothingToExport"]);
}
```

```json
{
  "id": "01936f81-6e7f-7081-9b2c-3d4e5f6a7b8c",
  "status": "Failed",
  "progress": 10,
  "error": "Dışa aktarılacak sipariş yok.",
  "completedAt": "2026-11-25T11:05:02.330019+00:00"
}
```

(Yanıtın öteki alanları kısalık için gösterilmedi.)

## Bir işi iptal etmek

`POST /{id}/cancel`:

| İşin durumu | Sonuç |
|---|---|
| `Queued` | Hemen `Cancelled` olur ve hiç çalışmaz. 200. |
| `Running` | İptal istenir ve mantığın `cancellationToken`'ı tetiklenir. 200; yanıtta durum henüz `Running`'dir. |
| `Succeeded`, `Failed`, `Cancelled` | 409: `'cancel' is not allowed while the record is in the 'Cancelled' state.` |

Çalışan bir işin iptali, aynı süreçteki yürütücüye hemen ulaşır; başka bir sunucudaki yürütücü bunu iş kaydına
aralıklarla bakarak görür (`HaldusJobOptions.CancellationPollInterval`, varsayılan 2 saniye).

İptal ancak mantık belirteci dinlerse işler. Mantık `OperationCanceledException` ile dönerse iş `Cancelled` olur ve
yarım kalan değişiklikleri kaydedilmez. Mantık belirteci hiç denetlemeden sonuna kadar çalışıp başarıyla dönerse iş
`Succeeded` olur. Uzun döngülerde `cancellationToken.ThrowIfCancellationRequested()` çağırın ve belirteci her
asenkron çağrıya geçirin.

## Kimlik ve dil

İş bir isteğin dışında çalışır, ama onu oluşturan isteğin kimliğini taşır:

- **Kiracı.** İş `MultiTenant` ise kiracısının adına çalışır: sorgu filtreleri, kayıt oluşturma ve numaralama o
  kiracıyı görür. Yukarıdaki örnekte `db.Set<Order>()` yalnızca o kiracının siparişlerini sayar.
- **Kullanıcı.** İş kaydı bir kullanıcı taşıyorsa onun adına çalışır: `OwnedByCurrentUser()` ise sahibi, değilse
  `Auditable(trackUser: true)` ile yazılan oluşturanı. İkisi de yoksa iş kullanıcısız çalışır.
- **Dil.** İş, oluşturulduğu isteğin diliyle (`Accept-Language`) çalışır. Mantığın ürettiği metinler (başarısızlık
  nedeni) sunucunun değil, işi isteyen kişinin dilinde olur.

İşler kiracılar ve kullanıcılar arasında da ayrılır: kiracıya ait bir işi başka bir kiracı listeleyemez, okuyamaz ve
iptal edemez (404).

## Yeniden teslim ve dayanıklılık

Süreç bir iş çalışırken kapanırsa (yeniden dağıtım, çökme) iş `Running` olarak kalır. Altyapı onu yeniden teslim
ederse iş **baştan** çalışır; nerede kaldığını hatırlamaz. Bu yüzden işin mantığı yeniden çalışmaya dayanıklı
olmalıdır: aynı işi iki kez çalıştırmak, bir kez çalıştırmakla aynı sonucu vermelidir (yazdığı kayıtları önce aramak,
dış servise bir tekrar anahtarı göndermek gibi).

- Bir iş en çok `HaldusJobOptions.MaxAttempts` kez (varsayılan 3) başlatılır. Daha fazla yarıda kalan iş `Failed` olur;
  `Error` boş kalır ve günlüğe bir hata yazılır.
- İki yürütücü aynı işi sahiplenemez: işin durumu bir eşzamanlılık belirtecidir. İptal ile başlatma da yarışmaz.
- Kuyruk kaydı iş kaydından önce görünebilir (iş kaydını yazan transaction henüz bitmemiş olabilir); yürütücü kaydı
  kısa aralıklarla yeniden arar (`LookupAttempts`, `LookupDelay`).
- Hangi altyapının yeniden teslim ettiği [Altyapı](#altyapı) tablosundadır. Bellek içi kuyruk yeniden teslim etmez.

<!-- example: job-options -->
```csharp
builder.Services.AddHaldusInMemoryJobs();
builder.Services.Configure<HaldusJobOptions>(options =>
{
    options.MaxAttempts = 5;
    options.CancellationPollInterval = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

app.MapJob<CatalogRecount>((job, context, cancellationToken) => Task.CompletedTask);

public partial class CatalogRecount;
```

| `HaldusJobOptions` | Varsayılan | Anlamı |
|---|---|---|
| `MaxAttempts` | 3 | Yarıda kalan bir işin en çok kaç kez başlatılacağı. Mantığı hata fırlatan iş bu sayıya bakılmadan başarısız sayılır. |
| `CancellationPollInterval` | 2 saniye | Başka bir sunucudan gelen iptalin ne sıklıkla denetleneceği. |
| `LookupAttempts` | 20 | İş kaydı henüz görünmüyorsa kaç kez yeniden aranacağı. |
| `LookupDelay` | 250 ms | Bu aramalar arasındaki bekleme. |

## Altyapı

İşi yürütücüye ulaştıran kuyruk ayrı bir pakettir. Uygulama tam olarak birini kaydeder:

| Paket | Kayıt | Kuyruk nerede | Yeniden başlatmada | Sunucular arasında |
|---|---|---|---|---|
| `Haldus.Jobs.PostgreSql` | `AddHaldusPostgreSqlJobs<TContext>()` | Uygulamanın veritabanında, iş kaydıyla aynı transaction'da | İşler kaybolmaz; yarıda kalan iş yeniden teslim edilir | Paylaşılır |
| `Haldus.Jobs.InMemory` | `AddHaldusInMemoryJobs()` | Sürecin belleğinde | Bekleyen ve çalışan işler kaybolur | Paylaşılmaz |
| `Haldus.Jobs.Hangfire` | `AddHaldusHangfireJobs()` | Hangfire'ın deposunda | Hangfire yeniden kuyruğa alır | Hangfire'ın kurulumuna bağlı |

### PostgreSQL

<!-- example: postgresql -->
```csharp
builder.Services.AddHaldusPostgreSqlJobs<AppDbContext>(options =>
{
    options.MaxConcurrency = 8;
    options.Lease = TimeSpan.FromMinutes(5);
});

var app = builder.Build();

app.MapJob<CatalogRecount>((job, context, cancellationToken) => Task.CompletedTask);

public partial class CatalogRecount;
```

- Kuyruk kaydı `HaldusJobQueue` tablosuna, iş kaydıyla aynı kaydetmede yazılır: iş ya kuyruğuyla birlikte kalıcı olur
  ya da hiç olmaz. Tablo modele girer; migration'larınızda görünür.
- İş entity'leri `TContext` bağlamında olmalıdır. Başka bir bağlamda duran bir iş oluşturulurken açık bir hata verir
  (bkz. [modüler yapı](modules.md)).
- Sunucular kuyruğu `FOR UPDATE SKIP LOCKED` ile paylaşır: aynı kaydı iki sunucu almaz, biri ötekini beklemez.
- İşi alan sunucu onu bir süre elinde tutar ve çalıştıkça süreyi uzatır. Sunucu çökerse süre dolar ve iş başka bir
  sunucuya (veya yeniden başlayınca aynısına) teslim edilir. İş sonuçlanınca kuyruk kaydı silinir.

| `PostgreSqlJobOptions` | Varsayılan | Anlamı |
|---|---|---|
| `MaxConcurrency` | 4 | Bu sunucuda aynı anda en çok kaç iş çalışır. |
| `PollInterval` | 1 saniye | Kuyruğa ne sıklıkla bakılır. Aynı sunucuda oluşturulan iş beklemeden alınır. |
| `Lease` | 2 dakika | İşi alan sunucunun onu elinde tuttuğu süre; çalıştıkça uzatılır. |
| `MaxDeliveries` | 10 | Çalıştırılamayan bir kuyruk kaydının en çok kaç kez teslim edileceği; sonra kuyruktan bırakılır. |

### Bellek içi

<!-- example: in-memory -->
```csharp
builder.Services.AddHaldusInMemoryJobs(options => options.MaxConcurrency = 2);

var app = builder.Build();

app.MapJob<CatalogRecount>((job, context, cancellationToken) => Task.CompletedTask);

public partial class CatalogRecount;
```

Geliştirme ve tek sunuculu küçük kurulumlar içindir. Kuyruk tablosu yoktur. Süreç yeniden başlarsa bekleyen ve
çalışan işler kaybolur; kayıtları `Queued` veya `Running` olarak kalır ve kendiliğinden düzelmez.
`InMemoryJobOptions.MaxConcurrency` (varsayılan 4) aynı anda çalışan iş sayısını sınırlar.

### Hangfire

`Haldus.Jobs.Hangfire` yalnızca `Hangfire.Core` paketine bağlıdır. Hangfire'ın kendisini (depolama, sunucu, panel)
uygulama kendi paketleriyle kurar; Haldus işi kuyruğa koyar ve Hangfire onu çalıştırmak istediğinde Haldus'un
yürütücüsünü çağırır. İşin durumu, ilerlemesi ve iptali yine iş kaydındadır.

<!-- example: hangfire -->
```csharp
// Hangfire'ın kendi kurulumu (ör. Hangfire.AspNetCore ve bir depolama paketiyle):
//   builder.Services.AddHangfire(configuration => configuration.UsePostgreSqlStorage(...));
//   builder.Services.AddHangfireServer();
builder.Services.AddHaldusHangfireJobs();

var app = builder.Build();

app.MapJob<CatalogRecount>((job, context, cancellationToken) => Task.CompletedTask);

public partial class CatalogRecount;
```

- Kuyruğa koyma iş kaydının kaydedilmesiyle aynı transaction'da değildir. Süreç ikisinin arasında çökerse iş `Queued`
  olarak kalır ve hiç çalışmaz. Bu kabul edilemiyorsa PostgreSQL altyapısını kullanın.
- Sunucu kapanırken yarıda kalan iş Hangfire'a başarısız bildirilir; Hangfire onu kendi yeniden deneme kuralıyla yeniden
  kuyruğa alır.
- Hangfire işi bağımlılık enjeksiyonundan çözülen bir sınıfla (`HaldusHangfireJob`) çalıştırır; Hangfire'ın
  ASP.NET Core entegrasyonu bunu kendiliğinden yapar.

Build sırasında `openapi.json` üretilirken uygulama başlatılır; üç altyapı da o sırada veritabanına bağlanmaz.

## Yeniden çalıştırma

`.Rerunnable()` bitmiş bir işi aynı girdilerle yeniden çalıştırır: başarısız bir raporu, dış servis düzeldikten sonra
yinelemek gibi.

<!-- example: rerun -->
```csharp
builder.Services.AddHaldusPostgreSqlJobs<AppDbContext>();

var app = builder.Build();

app.MapJob<ReportExport>()
    .MultiTenant()
    .Rerunnable();

public partial class ReportExport
{
    [Range(2000, 2100)]
    public int Year { get; set; }
}

public sealed class ReportExportHandler : IJobHandler<ReportExport>
{
    public Task HandleAsync(ReportExport job, JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
```

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/report-exports/{id}/rerun` | Girdileri kopyalanmış yeni bir iş oluşturur ve 202 döner. |

```http
POST /report-exports/01936f81-6e7f-7081-9b2c-3d4e5f6a7b8c/rerun
```

```http
HTTP/1.1 202 Accepted
Location: /report-exports/01936f85-0a1b-7c2d-8e3f-4a5b6c7d8e9f
```

```json
{
  "id": "01936f85-0a1b-7c2d-8e3f-4a5b6c7d8e9f",
  "year": 2026,
  "status": "Queued",
  "progress": 0,
  "error": null,
  "createdAt": "2026-11-25T11:20:00.441003+00:00",
  "startedAt": null,
  "completedAt": null,
  "rerunOfId": "01936f81-6e7f-7081-9b2c-3d4e5f6a7b8c"
}
```

Kurallar:

- Asıl iş olduğu gibi kalır. Yeni iş ayrı bir kayıttır ve `rerunOfId` ile asıl işe işaret eder.
- Girdiler (yazılabilir alanlar) kopyalanır; çıktılar, durum, ilerleme ve zamanlar baştan başlar.
- Bitmiş iş (`Succeeded`, `Failed` veya `Cancelled`) yeniden çalıştırılabilir. Bekleyen veya çalışan iş için 409 döner:
  aynı iş istenmeden iki kez çalışırdı.
- Yeni iş, yeniden çalıştırmayı isteyenin kimliği ve diliyle çalışır.
- Asıl iş bulunamazsa (veya isteği yapan göremiyorsa) 404.

## Dışa aktarma

`MapJob<T>().Exports<TResource>()` işi bir dışa aktarma yapar: iş, kaynağın işi başlatana görünen bütün kayıtlarını
CSV veya JSON dosyasına döker. Mantığı Haldus üretir; handler yazılmaz.

<!-- example: export -->
```csharp
builder.Services.AddHaldusPostgreSqlJobs<AppDbContext>();

var app = builder.Build();

app.MapCrud<Product>();
app.MapJob<ProductExport>().Exports<Product>();

public partial class Product
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }
}

// Alanlarını Haldus üretir.
public partial class ProductExport;
```

İş entity'sine şu alanlar eklenir:

| Alan | Girdi / çıktı | Anlamı |
|---|---|---|
| `Format` | girdi | `csv` (varsayılan) veya `json`. En çok 10 karakter. |
| `Rows` | çıktı | Dışa aktarılan kayıt sayısı. |
| `Size` | çıktı | Dosyanın bayt cinsinden boyutu. |
| `FileName` | çıktı | Dosyanın adı: `ProductExport-20261125112233.csv`. |

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/product-exports` | Dışa aktarmayı başlatır (202). |
| `GET` | `/product-exports/{id}` | İşi okur. |
| `GET` | `/product-exports/{id}/content` | Dosyayı indirir. |
| `POST` | `/product-exports/{id}/cancel` | İptal eder. |

```http
POST /product-exports
Content-Type: application/json

{ "format": "csv" }
```

İş bitince dosya indirilir:

```http
GET /product-exports/01936f90-1b2c-7d3e-8f4a-5b6c7d8e9fa0/content
```

```http
HTTP/1.1 200 OK
Content-Type: text/csv
Content-Disposition: attachment; filename=ProductExport-20261125112233.csv; filename*=UTF-8''ProductExport-20261125112233.csv
X-Content-Type-Options: nosniff

id,name,price,description
01936f8a-2c3d-7e4f-9a5b-6c7d8e9f0a1b,Kalem,10,
01936f8b-3d4e-7f5a-8b6c-7d8e9f0a1b2c,"'=SUM(1,2)",2.5,"line one
line two"
```

Kurallar:

- **Kayıtlar, işi başlatanın gördükleridir.** İş onun kiracısı ve kullanıcısı adına çalışır ve kaynağın kendi sorgu
  filtrelerinden geçer; silinmiş, süresi dolmuş veya başka kiracıya ait kayıtlar dosyaya girmez.
- **Satırlar kaynağın yanıt biçimindedir.** Yanıtta olmayan bir alan (hassas alanlar, içe aktarmanın içeriği gibi)
  dosyada da yoktur.
- CSV'nin ilk satırı alan adlarıdır (camelCase); dosya UTF-8'dir ve Excel'in tanıması için BOM ile başlar. Virgül,
  tırnak veya satır sonu taşıyan değer tırnak içine alınır (RFC 4180). İç içe değerler (gömülü satırlar, `metadata`
  çantası) hücrede JSON olarak durur.
- `=`, `+`, `-`, `@`, sekme veya satır başı ile başlayan metinlerin önüne kesme işareti (`'`) konur: dosya bir
  tablolama programında açıldığında hücre formül olarak çalışmaz. Sayılar bundan etkilenmez.
- JSON biçimi, kayıtların yanıt biçimindeki bir dizisidir.
- Dosya, iş başarıyla bitene kadar yoktur: bekleyen, çalışan veya başarısız işin `GET /{id}/content` isteği 404 döner.
- İndirme `attachment` ve `nosniff` ile yapılır. Dosya, dosya kaynaklarıyla aynı depoya yazılır (bkz.
  [dosyalar](files.md#depolama)); veritabanı ve dosya sistemi depolarında `Range` isteği desteklenir.
- Bilinmeyen bir `format` işi başarısız yapar: `The format must be 'csv' or 'json'.`

## Satırları içe aktarma

`MapJob<T>().Imports<TResource>()` işi bir içe aktarma yapar: iş, isteğin `content` alanındaki satırlardan kaynakta
kayıt oluşturur. Her satır tekil oluşturmanın bütün kurallarından geçer. Mantığı Haldus üretir.

<!-- example: import -->
```csharp
builder.Services.AddHaldusPostgreSqlJobs<AppDbContext>();

var app = builder.Build();

app.MapCrud<Category>();
app.MapCrud<Product>()
    .References<Category>()
    .Batch();
app.MapJob<ProductImport>().Imports<Product>();

public partial class Category
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}

public partial class Product
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}

public partial class ProductImport;
```

İş entity'sine şu alanlar eklenir:

| Alan | Girdi / çıktı | Anlamı |
|---|---|---|
| `Format` | girdi | `csv` (varsayılan) veya `json`. |
| `Content` | girdi | Satırlar; zorunlu, en çok 5 000 000 karakter. Yanıtta hiç görünmez ve iş bitince boşaltılır. |
| `Imported` | çıktı | Oluşturulan kayıt sayısı. Bir satır bile reddedildiyse 0'dır. |
| `Failed` | çıktı | Reddedilen satır sayısı. |
| `Errors` | çıktı | Reddedilen satırların nedenleri, her biri bir satırda (ilk 50 satır). |

### Biçim

**CSV:** ilk satır başlıktır ve sütun adları oluşturma isteğinin alan adlarıdır (`name`, `price`, `categoryId`).
Sayılar, evet/hayır değerleri (`true` / `false` / `1` / `0`) ve enum adları metinden okunur. Boş hücre alanın
gönderilmediği anlamına gelir. İç içe bir değer (gömülü satırlar, `metadata`) hücreye JSON olarak yazılır.

**JSON:** oluşturma isteği biçimindeki nesnelerin bir dizisi.

```http
POST /product-imports
Content-Type: application/json

{
  "format": "csv",
  "content": "name,price,categoryId\nKalem,10,\nDefter,25.5,01936f95-6a7b-7c8d-9e0f-1a2b3c4d5e6f\n"
}
```

```json
{
  "id": "01936f96-7b8c-7d9e-8f0a-1b2c3d4e5f6a",
  "format": "csv",
  "status": "Queued",
  "progress": 0,
  "error": null,
  "createdAt": "2026-11-25T11:40:00.019284+00:00",
  "startedAt": null,
  "completedAt": null,
  "imported": 0,
  "failed": 0,
  "errors": null
}
```

### Atomiklik ve hata raporu

İçe aktarma atomiktir: bütün satırlar tek bir transaction içinde işlenir. **Bir satır bile reddedilirse hiçbir kayıt
oluşturulmaz.** Yine de bütün satırlar denenir ve iş, nedenleri yazarak başarıyla (`Succeeded`) biter; böylece
istemci düzeltilmesi gereken bütün satırları tek seferde görür:

```json
{
  "status": "Succeeded",
  "imported": 0,
  "failed": 2,
  "errors": "Row 2: One or more validation errors occurred.; categoryId: Category '5b0e…' was not found.\nRow 4: The value at '$.price' cannot be read."
}
```

(Yanıtın öteki alanları kısalık için gösterilmedi; hata metinleri kısaltıldı.)

- Satır numaraları veri satırlarını sayar: başlık sayılmaz, ilk veri satırı 1'dir.
- Bir satırın nedeni, tekil oluşturma ucunun o satır için vereceği hatadır: doğrulama, bulunamayan referans, doğal
  anahtar çakışması... Okunamayan bir değer (`price` sütununda bir metin) okunamayan alanı söyler.
- Metinler işi başlatan isteğin dilindedir.
- Atomiklik, yarıda kalıp yeniden çalışan bir işin aynı kayıtları iki kez eklemesini de önler.

İşin kendisini başarısız yapan (`Failed`) durumlar:

| Durum | `error` |
|---|---|
| İçerik geçerli bir JSON dizisi değil | `The content is not a valid JSON list of records.` |
| 10 000 satırdan fazla | `An import can contain at most 10000 rows.` |
| Bilinmeyen `format` | `The format must be 'csv' or 'json'.` |

`content` gönderilmezse iş hiç oluşmaz: 400.

### Kurallar

- **Hedef `.Batch()` bildirmiş olmalıdır.** Her satır toplu oluşturmanın öğe yoluyla oluşturulur; bu yüzden hedefin
  `Create` işlemi olmalı ve hedef kendi adresinde yaşamalıdır (bir ebeveynin altında olmamalıdır). Değilse `HALDUS007`.
- İçe aktarma yalnızca kayıt oluşturur; var olan kayıtları güncellemez.
- 20 satırdan uzun içeriklerde ilerleme yüzde beşlik adımlarla bildirilir.
- İş, işi başlatanın kimliğiyle çalışır: kiracıya ait bir hedefte kayıtlar onun kiracısına yazılır.

### Kapsam kuralı

İşin kaydı, hata raporu ve (dışa aktarmada) ürettiği dosya, işi görebilen herkese açıktır. Bu yüzden iş, aktardığı
kaynağın kapsamını taşımalıdır. Hem `Exports` hem `Imports` için:

| Hedef | İş | Değilse |
|---|---|---|
| Kiracıya ait (`MultiTenant` veya kiracıya ait bir ebeveynin altında) | `MultiTenant()` olmalı | `HALDUS007`: bir kiracının verisi ötekilere görünürdü. |
| Kullanıcıya ait (`OwnedByCurrentUser`) | `OwnedByCurrentUser()` olmalı | `HALDUS007` |
| Yalnızca token ile erişilen (`TokenOnly`) | aktarılamaz | `HALDUS007` |

<!-- example: tenant-export -->
```csharp
builder.Services.AddHaldusPostgreSqlJobs<AppDbContext>();

var app = builder.Build();

app.MapCrud<Customer>().MultiTenant();
app.MapJob<CustomerExport>().MultiTenant().Exports<Customer>();

public partial class Customer
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class CustomerExport;
```

## Birlikte kullanım

| Bildirim | Birleşir mi |
|---|---|
| `MultiTenant`, `OwnedByCurrentUser`, `Auditable`, `Idempotent` | Evet. `Idempotent()` aynı oluşturma isteğinin iki iş açmasını önler (bkz. [yaşam döngüsü](lifecycle.md)). |
| `Rerunnable` | Evet; `Imports` ile hayır (`HALDUS007`): içe aktarmanın içeriği iş bitince boşaltılır. |
| `Exports` ile `Imports` birlikte | Hayır (`HALDUS007`): bir iş tek bir kaynağı ya dışa ya içe aktarır. |
| `Exports` / `Imports` başka bir `Map*` çağrısında | Hayır (`HALDUS007`): yalnızca `MapJob` ile yazılır. |
| `ConcurrencyChecked`, `SoftDeletable`, `Archivable` | Hayır (`HALDUS007`): işin yaşam döngüsü sabittir; istemci onu güncelleyemez veya silemez. |
| `KeyStrategy` | Hayır (`HALDUS007`): işin kimliği kuyruklarda `Guid` olarak taşınır. |
| Aktarma hedefi `MapCrud`, `MapReadOnly` veya `MapAppendOnly` değilse ya da takılabilir bir kaynaksa | Hayır (`HALDUS007`). |

Tanıların listesi: [tanılar](../reference/diagnostics.md).

## Sınırlar

- Mantığı hata fırlatan iş yeniden denenmez; yeniden denemek için `Rerunnable` kullanılır.
- Yarıda kalan iş kaldığı yerden değil baştan çalışır.
- Bellek içi kuyrukta süreç yeniden başlarsa işler kaybolur ve kayıtları `Queued` / `Running` olarak kalır.
- Hangfire'da kuyruğa koyma iş kaydıyla aynı transaction'da değildir.
- Zamanlanmış veya yinelenen işler (cron) yoktur; iş yalnızca bir istekle oluşur.
- İşler ve dışa aktarılan dosyalar kendiliğinden silinmez.
- Dışa aktarma dosyayı bellekte kurar. İlerleme yalnızca içe aktarmada bildirilir.
- İçe aktarma güncelleme yapmaz ve bir ebeveynin altındaki kaynağa yazamaz; en çok 10 000 satır, 5 milyon karakter.
- PostgreSQL kuyruğu tek bir bağlamdadır; işleri başka bir bağlamda duran bir modül onu kullanamaz.

## Ayrıca bakın

- [Dosyalar](files.md): dışa aktarılan dosyaların saklandığı depo.
- [Kayıt işlemleri](record-operations.md): içe aktarmanın dayandığı toplu oluşturma (`Batch`).
- [Çok kiracılık ve sahiplik](tenancy-and-ownership.md)
- [Modüler yapı](modules.md)
- [Paketler](../reference/packages.md)
- [Bildirim dizini](../reference/declarations.md)
