# Yapılandırma

Haldus iki çağrıyla uygulamaya bağlanır: servisleri kaydeden `AddHaldus<TContext>()` ve isteğin dilini çözen
`UseHaldus()`. Bu sayfa ikisinin ne yaptığını, `HaldusOptions`'ın bütün ayarlarını, değiştirebileceğiniz servisleri ve
mesajların dilini anlatır. Altyapı paketlerinin (iş kuyrukları, dosya depoları, olay yayıncıları) kendi seçenekleri
ilgili rehberlerdedir.

Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır; `builder` ve `app` değişkenlerini kuran satırlar da öyle.

## İçindekiler

- [`AddHaldus`](#addhaldus)
- [`HaldusOptions`](#haldusoptions)
- [Arka plan işlerinin seçenekleri](#arka-plan-işlerinin-seçenekleri)
- [`UseHaldus`](#usehaldus)
- [Değiştirilebilen servisler](#değiştirilebilen-servisler)
- [Mesajlar ve dil](#mesajlar-ve-dil)
- [Altyapı paketlerinin seçenekleri](#altyapı-paketlerinin-seçenekleri)
- [Ayrıca bakın](#ayrıca-bakın)

## `AddHaldus`

<!-- example: setup -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHaldus<AppDbContext>(options =>
{
    options.TenantClaimType = "org_id";
    options.UserClaimType = "sub";
    options.IdempotencyRetention = TimeSpan.FromHours(48);
    options.PurgeExpiredAfter = TimeSpan.FromDays(30);
});

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Product>();

app.Run();

public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

`AddHaldus<TContext>()` bağlamın kendisini kaydetmez; onu `AddDbContext` ile siz kaydedersiniz. `TContext` ortak
bağlamdır: kendi bağlamı olmayan bütün modüllerin entity'leri onun modeline eklenir. Bağlam `partial` yazılırsa ve
aynı projedeyse Haldus eşlenen entity'lerin `DbSet` özelliklerini öteki yarısına ekler.

`AddHaldus` şunları yapar:

| Ne | Ayrıntı |
|---|---|
| Bağlamı bağlar | DI'dan kurulan `TContext` örneklerine Haldus'un model katkısını ve trait alanlarını dolduran interceptor'ı ekler. |
| Modülleri yükler | Bağlamın assembly'sinden başvurularla ulaşılan ve Haldus'a başvuran assembly'leri modül sayar; onların ürettiği kayıt kodunu çalıştırır. |
| Handler'ları kaydeder | Generator'ın bulduğu `IActionHandler`, `IQueryHandler`, `ITransitionHandler`, `IJobHandler` ve benzeri sınıfları DI'a ekler. |
| Varsayılan servisleri ekler | Kimlik, mesajlar, dosya deposu, alan şifreleme ve zaman; hepsi değiştirilebilir (aşağıda). |
| ASP.NET Core servislerini ekler | `AddProblemDetails`, `AddDataProtection`, `AddHttpContextAccessor` ve `en` / `tr` dilleriyle istek yerelleştirmesi. |
| Arka plan servislerini başlatır | Bakım (süresi geçmiş idempotency yanıtları, süresi dolmuş kayıtlar, saklama süresi, zamanlanmış yayın) ve olay teslimi. |

Modüler bir uygulamada kendi bağlamı olan modül `AddHaldusModule<TContext>()` ile bildirilir; aynı seçenekleri alır.
Bkz. [modüler yapı](../guides/modules.md).

Bağlamı DI dışında kuruyorsanız (`new AppDbContext(options)`), Haldus'un modelini ve interceptor'ını
`DbContextOptionsBuilder.UseHaldus()` ile siz eklersiniz.

## `HaldusOptions`

| Özellik | Varsayılan | Ne yapar |
|---|---|---|
| `TenantClaimType` | `"tenant_id"` | Kiracı kimliğini taşıyan claim. Değeri bir `Guid` olmalıdır; okunamayan değer "kiracı yok" sayılır. |
| `UserClaimType` | `ClaimTypes.NameIdentifier` | Kullanıcı kimliğini taşıyan claim. |
| `IdempotencyRetention` | 24 saat | `Idempotency-Key` ile saklanan yanıtların ne kadar süre yeniden döndürüleceği. Süre dolunca aynı anahtar yeni bir istek sayılır. |
| `PurgeExpiredAfter` | `null` | Süresi dolan (`Expiring`) kayıtların, süreleri dolduktan ne kadar sonra fiziksel olarak silineceği. `null`: kayıtlar gizlenir ama hiç silinmez. Silme geri alınamaz ve zincirleme silinen alt kayıtları da götürür. |
| `MaintenanceInterval` | 1 saat | Bakımın ne sıklıkla çalışacağı: saklama süresi geçmiş idempotency yanıtları, süresi dolmuş kayıtlar (`PurgeExpiredAfter` verildiyse), `Retention` süresi geçmiş kayıtlar, süresi dolan bakiye tutmaları ve zamanı gelen zamanlanmış yayınlar. |
| `EventPollInterval` | 1 saniye | Bekleyen olayların (`PublishesEvents`) en geç ne kadar sonra teslim edileceği. Aynı süreçte yazılan olay beklemeden teslim edilir; bu süre başka bir sunucunun yazdığı olaylar içindir. |

Seçenekler ASP.NET Core'un seçenek sistemiyle tutulur; `AddHaldus`'a lambda vermek yerine
`builder.Services.Configure<HaldusOptions>(...)` de yazabilirsiniz.

İki ayar seçenek nesnesinde değil, statik özelliktedir ve uygulama açılırken bir kez değiştirilir:

| Özellik | Varsayılan | Ne yapar |
|---|---|---|
| `HaldusEvents.MaxAttempts` | 10 | Bir olayın kaç kez denendikten sonra bırakılacağı. |
| `HaldusChanges.SafetyLag` | 2 saniye | Değişiklik akışının (`ChangeFeed`) ne kadar geriden geleceği; bu süreden uzun süren transaction'ların satırları atlanabilir. |

## Arka plan işlerinin seçenekleri

`MapJob` işlerinin yürütücüsü `HaldusJobOptions` ile ayarlanır. Bu seçenekler hangi iş altyapısı seçilirse seçilsin
geçerlidir:

<!-- example: job-options -->
```csharp
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddHaldusInMemoryJobs();
builder.Services.Configure<HaldusJobOptions>(options =>
{
    options.MaxAttempts = 5;
    options.CancellationPollInterval = TimeSpan.FromSeconds(5);
});

var app = builder.Build();
app.UseHaldus();
app.MapJob<ReportExport>((job, context, cancellationToken) => Task.CompletedTask);

public partial class ReportExport
{
    public int Year { get; set; }
}
```

| Özellik | Varsayılan | Ne yapar |
|---|---|---|
| `MaxAttempts` | 3 | Yarıda kalan (süreci çöken veya kapanan) bir işin en fazla kaç kez çalıştırılmaya başlanacağı. Mantığı hata fırlatan iş yeniden denenmez. |
| `CancellationPollInterval` | 2 saniye | Çalışan bir iş için, başka bir sunucudan gelen iptalin ne sıklıkla kontrol edileceği. |
| `LookupAttempts` | 20 | İş kaydı henüz görünmüyorsa (onu oluşturan transaction bitmemiş olabilir) kaç kez yeniden bakılacağı. |
| `LookupDelay` | 250 ms | Bu bakışlar arasındaki süre. |

Altyapıların kendi seçenekleri (`InMemoryJobOptions`, `PostgreSqlJobOptions`) için bkz.
[iş altyapısı](../guides/jobs.md#altyapı).

## `UseHaldus`

`app.UseHaldus()` isteğin dilini çözer: ASP.NET Core'un istek yerelleştirmesini (`UseRequestLocalization`) `AddHaldus`'un
kaydettiği dillerle (`en` varsayılan, `tr`) açar. Haldus'un hata ve doğrulama mesajları bu dilde döner. Başka bir şey
yapmaz; kimlik doğrulama ve yetkilendirme ara katmanları sizindir.

Uçları eşleyen `Map*` çağrılarından önce yazın. Çağırmazsanız istek dili çözülmez; mesajlar sunucu sürecinin dilinde
(desteklenmiyorsa İngilizce) döner.

## Değiştirilebilen servisler

`AddHaldus` varsayılan servisleri `TryAdd` ile kaydeder. Kendi uygulamanızı `AddHaldus`'tan önce kaydederseniz
Haldus'unki eklenmez; sonra kaydederseniz sizinki geçerli olur.

| Servis | Varsayılan | Ne zaman değiştirilir |
|---|---|---|
| `ITenantProvider` | `TenantClaimType` claim'ini `Guid` olarak okur. | Kiracı token'da değil (alt alan adında, bir başlıkta, kullanıcının kaydında). |
| `ICurrentUser` | `UserClaimType` claim'ini okur. | Kullanıcı kimliği başka bir yerden geliyor. |
| `ICurrentUserRoles` | `HttpContext.User.IsInRole(role)`. | Onay zincirinin (`ApprovalRoles`) rolleri claim'lerde değil kendi kimlik modelinizde. |
| `IHaldusMessages` | Paketteki `.resx` dosyaları (`en`, `tr`). | Metinleri değiştirmek veya dil eklemek; bkz. [mesajlar ve dil](#mesajlar-ve-dil). |
| `IHaldusFileStorage` | İçerikler veritabanında (`HaldusFileContents` tablosu). | İçerikleri bir klasörde (`AddHaldusFileSystemStorage(root)`), S3 uyumlu bir depoda (`AddHaldusS3Storage`) veya kendi deponuzda tutmak. |
| `IHaldusFieldProtector` | ASP.NET Data Protection. | Şifreli alanları (`Encrypted`) kendi anahtar yönetiminizle şifrelemek. |
| `TimeProvider` | `TimeProvider.System`. | Testlerde zamanı ilerletmek. Haldus'un yazdığı zaman damgaları (`CreatedAt`, süre dolumu, işler) buradan okunur. |
| `IHaldusEventPublisher` | Yok. | Olayları dışarı taşımak; bir veya birkaç yayıncı kaydedilir. Yayıncı yoksa olaylar kutuda bekler. |
| `IHaldusJobDispatcher` | Yok. | İş altyapısı paketlerinden biri kaydeder (`AddHaldusInMemoryJobs()` ve benzerleri). `MapJob` varken hiçbiri yoksa uygulama açılmaz. |

Örnek: kiracıyı bir başlıktan okumak.

<!-- example: tenant-provider -->
```csharp
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddSingleton<ITenantProvider, HeaderTenantProvider>();

var app = builder.Build();
app.UseHaldus();
app.MapCrud<Project>().MultiTenant();

public sealed class HeaderTenantProvider(IHttpContextAccessor accessor) : ITenantProvider
{
    public Guid? TenantId =>
        Guid.TryParse(accessor.HttpContext?.Request.Headers["X-Tenant-Id"], out var tenantId) ? tenantId : null;
}

public partial class Project
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

Kimliği doğrulanmamış bir başlığa güvenmek herkesin her kiracıya ulaşması demektir; bu örnek yalnızca servisin
nasıl değiştirildiğini gösterir. Gerçek uygulamada kiracıyı doğrulanmış kimlikten okuyun.

Kiracı ve kullanıcı yalnızca bu servislerden okunur; Haldus hangi kimlik doğrulama şemasının kullanıldığına bakmaz.
Ayrıntısı: [kiracılar ve sahiplik](../guides/tenancy-and-ownership.md).

## Mesajlar ve dil

Haldus'un son kullanıcıya döndürdüğü her metin (`title`, `detail`, doğrulama mesajları) `IHaldusMessages` servisinden,
bir anahtarla okunur. Anahtarlar `HaldusMessageKeys` sınıfındaki sabitlerdir (`NotFoundTitle`, `ValidationRequired`,
`StateConflictDetail`...). Varsayılan uygulama paketin içindeki `.resx` dosyalarını, isteğin diline
(`CultureInfo.CurrentUICulture`) göre okur.

Bazı metinleri değiştirmek için kendi uygulamanızı yazın ve değiştirmediğiniz anahtarları varsayılana bırakın:

<!-- example: messages -->
```csharp
using System.Globalization;
using Microsoft.Extensions.DependencyInjection.Extensions;

builder.Services.AddHaldus<AppDbContext>();

// Varsayılan uygulama paketin içindedir; kaydını bulup sizinkinin arkasına koyarsınız.
var defaults = builder.Services.Single(service => service.ServiceType == typeof(IHaldusMessages));
builder.Services.Replace(ServiceDescriptor.Singleton<IHaldusMessages>(provider =>
    new ShopMessages((IHaldusMessages)ActivatorUtilities.CreateInstance(provider, defaults.ImplementationType!))));

var app = builder.Build();
app.UseHaldus();
app.MapCrud<Product>();

public sealed class ShopMessages(IHaldusMessages defaults) : IHaldusMessages
{
    public string Format(string key, params object?[] arguments) =>
        (key, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) switch
        {
            (HaldusMessageKeys.NotFoundTitle, "tr") => "Aradığınız kayıt yok",
            (HaldusMessageKeys.NotFoundTitle, _) => "Nothing here",
            _ => defaults.Format(key, arguments),
        };
}

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

Yeni bir dil eklemek için iki şey gerekir: dilin istek yerelleştirmesine eklenmesi ve o dilin metinlerini veren bir
`IHaldusMessages`:

<!-- example: language -->
```csharp
builder.Services.AddHaldus<AppDbContext>();
builder.Services.Configure<RequestLocalizationOptions>(options => options
    .AddSupportedCultures("en", "tr", "de")
    .AddSupportedUICultures("en", "tr", "de"));

var app = builder.Build();
app.UseHaldus();
app.MapCrud<Product>();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

Doğrulama mesajları da bu servisten gelir: entity'deki `[Required]`, `[MaxLength]`, `[MinLength]`,
`[StringLength]`, `[Range]`, `[EmailAddress]` gibi kurallar `Validation.*` anahtarlarıyla yazılır
(`The Name field is required.`, `Name alanı zorunludur.`). Bir attribute'a kendi `ErrorMessage` metninizi (veya
`ErrorMessageResourceType`) yazarsanız o alan için sizin metniniz kullanılır ve Haldus ona dokunmaz. Haldus'un tanımadığı
bir doğrulama attribute'u da kendi mesajıyla döner.

İş mantığınızın döndürdüğü metinler (`TransitionRejectedException` mesajı, `JobFailedException` mesajı) Haldus'tan
geçmez; onları çevirmek sizin işinizdir. Log ve exception mesajları İngilizcedir ve çevrilmez.

## Altyapı paketlerinin seçenekleri

| Kayıt | Seçenek sınıfı | Rehber |
|---|---|---|
| `AddHaldusInMemoryJobs(...)` | `InMemoryJobOptions` | [İş altyapısı](../guides/jobs.md#altyapı) |
| `AddHaldusPostgreSqlJobs<TContext>(...)` | `PostgreSqlJobOptions` | [İş altyapısı](../guides/jobs.md#altyapı) |
| `AddHaldusHangfireJobs()` | Yok; Hangfire'ı uygulama kurar | [İş altyapısı](../guides/jobs.md#altyapı) |
| `AddHaldusFileSystemStorage(root)` | Yok | [Depolama](../guides/files.md#depolama) |
| `AddHaldusS3Storage(...)` | `HaldusS3StorageOptions` | [Depolama](../guides/files.md#depolama) |
| `AddHaldusWebhookPublisher(...)` | `HaldusWebhookOptions` | [Webhook](../guides/events.md#webhook) |
| `AddHaldusSqsPublisher(...)` | `HaldusSqsOptions` | [Amazon SQS](../guides/events.md#amazon-sqs) |
| `AddHaldusRabbitMqPublisher(...)` | `HaldusRabbitMqOptions` | [RabbitMQ](../guides/events.md#rabbitmq) |
| `AddHaldusKafkaPublisher(...)` | `HaldusKafkaOptions` | [Kafka](../guides/events.md#kafka) |

Hangi paketin hangi kaydı getirdiği: [paketler](packages.md).

## Ayrıca bakın

- [Kurulum](../getting-started/installation.md)
- [Paketler](packages.md)
- [HTTP sözleşmesi](http.md)
- [Üretime çıkmak](../guides/production.md)
