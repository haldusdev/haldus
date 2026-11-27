# Paketler

Haldus on NuGet paketinden oluşur. Çoğu uygulama yalnızca `Haldus.AspNetCore` paketini ekler; öteki paketler bir
altyapı seçimidir (iş kuyruğu, dosya deposu, olay yayıncısı) ve yalnızca o özellik kullanılıyorsa eklenir. Bu sayfa
her paketin ne içerdiğini, neye bağlı olduğunu, ne zaman eklendiğini ve hangi metotla kaydedildiğini listeler.

## İçindekiler

- [Özet](#özet)
- [Çekirdek](#çekirdek)
- [Arka plan işi altyapıları](#arka-plan-işi-altyapıları)
- [Dosya deposu](#dosya-deposu)
- [Olay yayıncıları](#olay-yayıncıları)
- [Bir arada](#bir-arada)
- [Sürüm ve önizleme durumu](#sürüm-ve-önizleme-durumu)
- [Ayrıca bakın](#ayrıca-bakın)

## Özet

| Paket | Ne zaman eklenir | Kayıt metodu |
|---|---|---|
| `Haldus.AspNetCore` | Her zaman. Generator bu paketin içindedir. | `AddHaldus<TContext>()`, `AddHaldusModule<TContext>()`, `UseHaldus()` |
| `Haldus.EntityFrameworkCore` | Doğrudan eklenmez; `Haldus.AspNetCore` ile gelir. | — |
| `Haldus.Abstractions` | Doğrudan eklenmez; `Haldus.AspNetCore` ile gelir. | — |
| `Haldus.Jobs.InMemory` | `MapJob` kullanılıyor; geliştirme veya tek sunuculu küçük kurulum. | `AddHaldusInMemoryJobs()` |
| `Haldus.Jobs.PostgreSql` | `MapJob` kullanılıyor; işler kalıcı olmalı ve birden çok sunucu paylaşmalı. | `AddHaldusPostgreSqlJobs<TContext>()` |
| `Haldus.Jobs.Hangfire` | `MapJob` kullanılıyor ve uygulama zaten Hangfire kullanıyor. | `AddHaldusHangfireJobs()` |
| `Haldus.Storage.S3` | `MapFile` içerikleri S3 uyumlu bir nesne deposunda (AWS S3, MinIO) tutulacak. | `AddHaldusS3Storage(...)` |
| `Haldus.Events.Sqs` | `PublishesEvents` olayları Amazon SQS'e taşınacak. | `AddHaldusSqsPublisher(...)` |
| `Haldus.Events.RabbitMq` | Olaylar RabbitMQ'ya taşınacak. | `AddHaldusRabbitMqPublisher(...)` |
| `Haldus.Events.Kafka` | Olaylar Kafka'ya taşınacak. | `AddHaldusKafkaPublisher(...)` |

Hiçbir paket veritabanı sağlayıcısı taşımaz. Uygulama `Npgsql.EntityFrameworkCore.PostgreSQL` paketini kendisi ekler;
Haldus yalnızca PostgreSQL ile çalışır.

## Çekirdek

### `Haldus.AspNetCore`

Uygulamanın eklediği asıl paket.

- **İçerir:** `AddHaldus`, `AddHaldusModule`, `UseHaldus`, bütün `Map*` giriş noktaları ve zincir metotları,
  doğrulama filtresi, hata yanıtları, Türkçe ve İngilizce mesajlar (`.resx`), idempotency filtresi, iş yürütücüsü,
  bakım ve olay teslim servisleri, dosya sistemi deposu (`AddHaldusFileSystemStorage`) ve webhook yayıncısı
  (`AddHaldusWebhookPublisher`).
- **Generator:** kaynak üreticisi (`Haldus.Generators`) ayrı bir paket değildir; bu paketin `analyzers/dotnet/cs`
  klasöründe gelir. Üretilen kod bu paketin çalışma zamanı API'lerini çağırdığı için ikisi her zaman aynı sürümdedir.
- **Kılavuz:** İngilizce başvuru ([docs/guide.md](../guide.md)) paketin `docs` klasöründe de gelir; kodlama ajanları
  kurulu sürümün kılavuzunu ağa çıkmadan okuyabilir.
- **Bağımlılıklar:** ASP.NET Core paylaşılan çatısı (`Microsoft.AspNetCore.App`), `Haldus.Abstractions`,
  `Haldus.EntityFrameworkCore`.

`Map*` çağrısı içeren her proje, sınıf kütüphanesi olan modüller dahil, bu pakete başvurur. Yalnızca
`Haldus.Abstractions` veya `Haldus.EntityFrameworkCore` paketine başvuran bir projede generator çalışmaz.

### `Haldus.EntityFrameworkCore`

EF Core katmanı: model kayıt defteri, kayıt kaydedilirken trait alanlarını dolduran interceptor, adlandırılmış sorgu
filtreleri (`HaldusFilters`), numaralama, sıra, bakiye ve olay kutusu gibi veritabanına yazan çalışma zamanı kodu,
dosya deposu ve alan şifreleme arayüzleri (`IHaldusFileStorage`, `IHaldusFieldProtector`) ve
`DbContextOptionsBuilder.UseHaldus()`.

- **Bağımlılıklar:** `Microsoft.EntityFrameworkCore.Relational` (10.x), `Haldus.Abstractions`.
- **Ne zaman eklenir:** doğrudan eklenmez; `Haldus.AspNetCore` ile gelir.

### `Haldus.Abstractions`

Bağımlılığı olmayan sözleşmeler: sayfalama tipleri (`PagedResult<T>`), trait arayüzleri (`IMultiTenant`,
`IAuditable`...), iş mantığı sözleşmeleri (`IComputed`, `IFilter<T>`, `ITransition<T>`, `IActionHandler`,
`IJobHandler<T>`...), kimlik servisleri (`ITenantProvider`, `ICurrentUser`, `ICurrentUserRoles`) ve olay sözleşmesi
(`IHaldusEventPublisher`, `HaldusEvent`).

- **Bağımlılıklar:** yok.
- **Ne zaman eklenir:** doğrudan eklenmez; `Haldus.AspNetCore` ile gelir. Olay yayıncısı paketleri yalnızca buna
  başvurur.

## Arka plan işi altyapıları

`MapJob` işlerini yürütücüye ulaştıran altyapılar. Çekirdek yalnızca `IHaldusJobDispatcher` arayüzünü bilir; uygulama
`MapJob` kullanıyorsa bu üç paketten birini ekler. Hiçbiri kayıtlı değilse `MapJob` çağrısı uygulama açılırken
hata verir. Üçü de `Haldus.AspNetCore`'a, generator'ı da taşıyacak biçimde başvurur: yalnızca bir iş paketi eklemek
`Map*` çağrılarının derlenmesi için yeterlidir.

| Paket | Bağımlılıklar | Kayıt | Seçenekler |
|---|---|---|---|
| `Haldus.Jobs.InMemory` | `Haldus.AspNetCore` | `AddHaldusInMemoryJobs()` | `InMemoryJobOptions` |
| `Haldus.Jobs.PostgreSql` | `Haldus.AspNetCore` | `AddHaldusPostgreSqlJobs<TContext>()` | `PostgreSqlJobOptions` |
| `Haldus.Jobs.Hangfire` | `Haldus.AspNetCore`, `Hangfire.Core` (1.8.x), `Newtonsoft.Json` (13.0.4) | `AddHaldusHangfireJobs()` | Yok |

- **Bellek içi:** işler sürecin belleğinde bekler ve aynı süreçte çalışır. Süreç yeniden başlarsa bekleyen işler
  kaybolur; kuyruk sunucular arasında paylaşılmaz.
- **PostgreSQL:** kuyruk kaydı iş kaydıyla aynı transaction'da yazılır; yeniden başlatmada iş kaybolmaz ve birden çok
  sunucu aynı kuyruğu paylaşır. İş entity'leri verilen bağlamda olmalıdır.
- **Hangfire:** Haldus işi Hangfire'ın kuyruğuna koyar; depolamayı, sunucuyu ve paneli (`AddHangfire`,
  `AddHangfireServer`) uygulama kurar. Kuyruğa koyma iş kaydıyla aynı transaction'da değildir. `Newtonsoft.Json`,
  Hangfire'ın istediği en düşük sürümdeki güvenlik açığı yüzünden yamalı sürüme sabitlenmiştir.

Ayrıntısı: [iş altyapısı](../guides/jobs.md#altyapı).

## Dosya deposu

Varsayılan olarak `MapFile` içerikleri veritabanında (`HaldusFileContents` tablosu) durur ve paket gerekmez.
`AddHaldusFileSystemStorage(root)` içerikleri bir klasöre yazar; o da çekirdektedir.

| Paket | Bağımlılıklar | Kayıt | Seçenekler |
|---|---|---|---|
| `Haldus.Storage.S3` | `Haldus.AspNetCore`, `AWSSDK.S3` (4.x) | `AddHaldusS3Storage(...)` | `HaldusS3StorageOptions` |

S3 uyumlu servislerle (AWS S3, MinIO) konuşur. Erişim anahtarı verilmezse kimlik bilgisi AWS SDK'nın varsayılan
zincirinden (ortam, profil, rol) okunur. Kovayı Haldus oluşturmaz. Ayrıntısı: [depolama](../guides/files.md#depolama).

## Olay yayıncıları

`.PublishesEvents()` olayları bir kutuya yazar ve kayıtlı `IHaldusEventPublisher` servislerine teslim eder. Webhook
yayıncısı (`AddHaldusWebhookPublisher`) çekirdektedir; mesaj kuyrukları için ayrı paketler vardır. Bunlar
`Haldus.AspNetCore`'a değil yalnızca `Haldus.Abstractions`'a başvurur, bu yüzden generator'ı getirmezler.

| Paket | Bağımlılıklar | Kayıt | Seçenekler | Rehber |
|---|---|---|---|---|
| `Haldus.Events.Sqs` | `AWSSDK.SQS` (4.x) | `AddHaldusSqsPublisher(...)` | `HaldusSqsOptions` | [Amazon SQS](../guides/events.md#amazon-sqs) |
| `Haldus.Events.RabbitMq` | `RabbitMQ.Client` (7.x) | `AddHaldusRabbitMqPublisher(...)` | `HaldusRabbitMqOptions` | [RabbitMQ](../guides/events.md#rabbitmq) |
| `Haldus.Events.Kafka` | `Confluent.Kafka` (2.x) | `AddHaldusKafkaPublisher(...)` | `HaldusKafkaOptions` | [Kafka](../guides/events.md#kafka) |

Uygulama başına her yayıncıdan tek bir hedef kaydedilir. Exchange, kuyruk ve konuları Haldus oluşturmaz.
`Confluent.Kafka` yerel bir kütüphane (`librdkafka`) taşır.

## Bir arada

PostgreSQL iş kuyruğu, S3 deposu ve Kafka yayıncısı kullanan bir uygulama:

```xml
<ItemGroup>
  <PackageReference Include="Haldus.AspNetCore" Version="0.1.0-preview.1" />
  <PackageReference Include="Haldus.Jobs.PostgreSql" Version="0.1.0-preview.1" />
  <PackageReference Include="Haldus.Storage.S3" Version="0.1.0-preview.1" />
  <PackageReference Include="Haldus.Events.Kafka" Version="0.1.0-preview.1" />
  <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.4" />
</ItemGroup>
```

<!-- example: together -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddHaldusPostgreSqlJobs<AppDbContext>();
builder.Services.AddHaldusS3Storage(options =>
{
    options.Region = "eu-central-1";
    options.Bucket = "shop-files";
});
builder.Services.AddHaldusKafkaPublisher(options =>
{
    options.BootstrapServers = "localhost:9092";
    options.Topic = "shop-events";
});

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Product>().PublishesEvents();
app.MapFile<Attachment>(maxBytes: 20 * 1024 * 1024, "application/pdf");
app.MapJob<ReportExport>((job, context, cancellationToken) => Task.CompletedTask);

app.Run();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class Attachment;

public partial class ReportExport
{
    public int Year { get; set; }
}
```

## Sürüm ve önizleme durumu

- **Sürüm:** `0.1.0-preview.1`. Bütün paketler aynı sürümü taşır ve birlikte ilerler; paketleri karıştırmayın.
- **Önizleme:** sürüm bir ön sürümdür; API değişebilir.
- **Yayın:** paketler nuget.org'dadır (`dotnet add package Haldus.AspNetCore --prerelease`). Yayınlanmamış bir
  değişiklik depodan üretilip yerel bir klasörden de kullanılabilir; bkz. [kurulum](../getting-started/installation.md).
- **Önbellek:** NuGet bir paketi sürümüyle önbelleğe alır. Depodan aynı sürümle yeniden üretilen paket projenize
  ulaşmaz; sürümü ilerletin (`--version-suffix local.2`) veya önbellekteki `haldus.*` klasörlerini silin.
- **Hedef çatı:** `net10.0`. Generator `netstandard2.0`'dır ve Roslyn 5.0.0'a karşı derlenir; .NET 10 SDK'nın
  derleyicisiyle yüklenir.
- **Semboller ve belgeler:** semboller derlemenin içindedir (ayrı sembol paketi yoktur); XML belge yorumları pakete
  girer. Her paketin README'si deponun kökündeki `README.md`'dir.
- **Lisans:** MIT ([LICENSE](../../LICENSE)); her paket lisansı `PackageLicenseExpression` ile taşır.

## Ayrıca bakın

- [Kurulum](../getting-started/installation.md)
- [Yapılandırma](configuration.md)
- [Arka plan işleri](../guides/jobs.md)
- [Dosyalar](../guides/files.md)
- [Olay yayını](../guides/events.md)
