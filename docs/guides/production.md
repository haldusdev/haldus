# Üretime hazırlık

Haldus'un ürettiği uçlar sıradan bir ASP.NET Core uygulamasının parçasıdır; onu üretimde çalıştırmak için bildiğiniz
her şey (HTTPS, sağlık kontrolleri, yapılandırma, dağıtım) aynen geçerlidir. Bu sayfa Haldus'un bunlara eklediği
konuları toplar: şifreleme anahtarları, arka plan servisleri, birden çok sunucu, vekil, hız sınırı, günlük ve
veritabanı bağlantıları. Haldus'un yaptığı ile ASP.NET Core'un genel önerisi ayrı ayrı belirtilir.

Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. `var builder = WebApplication.CreateBuilder(args);` ve
`var app = builder.Build();` satırları yoksa yazılmış sayılır.

## İçindekiler

- [Data Protection anahtarları](#data-protection-anahtarları)
- [Bakım servisi ve aralıklar](#bakım-servisi-ve-aralıklar)
- [Birden çok sunucu](#birden-çok-sunucu)
- [Vekil arkasında çalışmak](#vekil-arkasında-çalışmak)
- [Hız sınırlama](#hız-sınırlama)
- [Günlük](#günlük)
- [PostgreSQL ve bağlantı havuzu](#postgresql-ve-bağlantı-havuzu)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Data Protection anahtarları

`AddHaldus` ASP.NET Core Data Protection'ı kaydeder (`AddDataProtection()`), ama anahtarların nerede saklanacağını
seçmez. Şu özellikler bu anahtarlara bağlıdır:

| Özellik | Anahtar kaybolursa veya sunucular farklı anahtar kullanırsa |
|---|---|
| Şifreli alanlar (`.Encrypted(...)`) | Alan okunamaz; kaydı okuyan istek `500` alır. Veri, anahtar geri gelmeden açılamaz. |
| Geri okunabilen sırlar (`.SecretOnce(recoverable: true)`) | Sunucu sırrı açamaz (`HaldusSecretProtection.Reveal` hata verir); sırrı kullanan iş (ör. bir gönderiyi imzalamak) durur. |
| Süreli indirme bağlantıları (`.SignedLinks()`) | Daha önce verilen bağlantılar geçersiz olur (`404`). |
| Uygulamanın kendi kullanımları | ASP.NET Core'un çerezleri, `AddBearerToken` token'ları (örnek mağaza bunları kullanır) ve antiforgery belirteçleri de aynı anahtarlara bağlıdır. |

Varsayılan `SecretOnce` (yalnızca özet saklanır) ve `TokenAccess` anahtara bağlı değildir: ikisi de SHA-256 özetiyle
çalışır.

Anahtarların kalıcı olması ve bütün sunucularca paylaşılması uygulamanın işidir. ASP.NET Core'un genel önerisi, anahtar
halkasını kalıcı ve paylaşılan bir yerde tutmak ve uygulama adını sabitlemektir:

<!-- example: data-protection -->
```csharp
using Microsoft.AspNetCore.DataProtection;

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

// Bütün sunucular aynı klasörü (ör. paylaşılan bir disk) görür ve aynı uygulama adını kullanır.
builder.Services.AddDataProtection()
    .SetApplicationName("shop")
    .PersistKeysToFileSystem(new DirectoryInfo("/var/lib/shop/keys"));

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Integration>().Encrypted(integration => integration.ApiToken);

app.Run();

public partial class Integration
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(500)]
    public string ApiToken { get; set; } = "";
}
```

Anahtarları dosya yerine bir veritabanında, bir anahtar kasasında veya bir bulut deposunda tutmak, saklanan anahtarları
ayrıca şifrelemek (`ProtectKeysWith...`) ASP.NET Core'un kendi seçenekleridir; bazıları ek paket ister. Ayrıntısı
ASP.NET Core belgelerinin "Data Protection" bölümündedir.

Şifreli alanların koruyucusu ayrıca değiştirilebilir: `IHaldusFieldProtector` servisini kendi anahtar yönetiminizle
yazarsanız `.Encrypted(...)` Data Protection yerine onu kullanır. Haldus varsayılanı `TryAdd` ile kaydeder.

> [!WARNING]
> Bir alanı sonradan `.Encrypted(...)` yaparsanız var olan düz değerler şifrelenmez ve okunamaz hale gelir. Var olan
> veriyi bir geçişle (migration veya tek seferlik bir komut) dönüştürmeniz gerekir.

## Bakım servisi ve aralıklar

Her `AddHaldus` uygulamasında iki arka plan servisi çalışır:

| Servis | Ne yapar | Ayar (`HaldusOptions`) | Varsayılan |
|---|---|---|---|
| Bakım | Saklama süresi geçmiş idempotency yanıtlarını siler; `Retention` süresi geçmiş kayıtları siler; zamanı gelen taslakları yayına alır (`ScheduledPublishing`); süresi dolan bakiye tutmalarını bırakır; `PurgeExpiredAfter` verildiyse süresi dolmuş `Expiring` kayıtlarını siler. | `MaintenanceInterval` | 1 saat |
| Olay dağıtıcısı | `PublishesEvents` kaynaklarının olaylarını kayıtlı yayıncılara teslim eder. Aynı süreçte yazılan olay beklemeden yayınlanır. | `EventPollInterval` | 1 saniye |

Bakımın kullandığı öteki ayarlar:

| Ayar | Ne belirler | Varsayılan |
|---|---|---|
| `IdempotencyRetention` | `Idempotency-Key` ile saklanan bir yanıtın ne kadar süre yeniden döndürüleceği. Süre dolunca aynı anahtar yeni bir istektir. | 24 saat |
| `PurgeExpiredAfter` | Süresi dolan kayıtların, süreleri dolduktan ne kadar sonra fiziksel olarak silineceği. `null`: kayıtlar gizlenir ama hiç silinmez. Silme geri alınamaz ve zincirleme silinen alt kayıtları da götürür. | `null` |

<!-- example: options -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHaldus<AppDbContext>(options =>
{
    options.MaintenanceInterval = TimeSpan.FromMinutes(15);
    options.IdempotencyRetention = TimeSpan.FromHours(48);
    options.PurgeExpiredAfter = TimeSpan.FromDays(30);
});

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Session>().Expiring(TimeSpan.FromHours(8)).Idempotent();

app.Run();

public partial class Session
{
    [MaxLength(200)]
    public string Device { get; set; } = "";
}
```

Bilinmesi gerekenler:

- Bakımın ilk turu uygulama açıldıktan bir aralık sonra çalışır, açılışta değil. Zamanlanmış yayının gecikmesi en çok
  bir aralıktır; yayın zamanına daha yakın olmasını istiyorsanız aralığı kısaltın.
- Bakımın bir adımı başarısız olursa (ör. silinecek kayda başka bir kayıt referans veriyor) uyarı günlüğe yazılır ve
  adım bir sonraki turda yeniden denenir. Servis durmaz.
- Bakım bir yönetim komutundan veya testten doğrudan da çağrılabilir: `HaldusMaintenance.RunAsync(services, cancellationToken)`.
- Bütün ayarların listesi [yapılandırma](../reference/configuration.md) sayfasındadır.

## Birden çok sunucu

Aynı veritabanına bağlı birden çok uygulama örneği çalıştırdığınızda:

| Konu | Davranış |
|---|---|
| Data Protection | Anahtarlar paylaşılmalıdır (yukarıda). Yoksa bir sunucunun şifrelediğini öteki açamaz. |
| Bakım | Her sunucuda ayrı çalışır; aralarında kilit yoktur. Silmeler aynı kayıtları hedefler ve tekrar edilmeleri zararsızdır. Aynı taslağı iki sunucu birlikte yayına almaya çalışırsa biri benzersiz indekse takılır ve o taslak bir sonraki turda yeniden denenir. |
| Olay dağıtıcısı | Her sunucuda çalışır ama bir veritabanında aynı anda yalnızca biri teslim eder: tur bir veritabanı kilidi altındadır, ötekiler kilidi bekler. Olaylar en az bir kez ve sırayla teslim edilir. |
| İşler (`MapJob`) | Altyapıya bağlıdır; aşağıda. |
| Dosya içerikleri (`MapFile`) | Varsayılan depo veritabanıdır ve paylaşılır. `AddHaldusFileSystemStorage` seçtiyseniz bütün sunucular aynı klasörü görmelidir. S3 uyumlu depo (`Haldus.Storage.S3`) paylaşılır. |

İş altyapıları:

| Altyapı | Birden çok sunucuda |
|---|---|
| `AddHaldusInMemoryJobs()` | İş, oluşturulduğu süreçte, bellekteki bir kuyrukta çalışır. Süreç yeniden başlarsa bekleyen işler kaybolur. Tek sunuculu veya kaybın önemsiz olduğu durumlar içindir. |
| `AddHaldusPostgreSqlJobs<TContext>()` | Kuyruk veritabanındadır; iş kaydıyla aynı transaction'da yazılır. Sunucular kuyruğu paylaşır (`FOR UPDATE SKIP LOCKED`); bir işi alan sunucu onu bir süre elinde tutar ve çalıştıkça uzatır (`Lease`, varsayılan 2 dakika). Sunucu çökerse süre dolar ve iş başka bir sunucuya yeniden teslim edilir. Sunucu başına eşzamanlılık `MaxConcurrency` (varsayılan 4), kuyruğa bakma sıklığı `PollInterval` (varsayılan 1 saniye). |
| `AddHaldusHangfireJobs()` | İşi Hangfire'ın kuyruğuna koyar; depolama, sunucular ve panel Hangfire'ın yapılandırmasıdır. Kuyruğa koyma iş kaydıyla aynı transaction'da değildir. |

Her altyapıda: yarıda kalan iş yeniden teslim edilirse baştan çalışır (`HaldusJobOptions.MaxAttempts`, varsayılan 3);
iş mantığınız yeniden çalışmaya dayanıklı olmalıdır. Başka bir sunucuda çalışan işin iptali, kayıt yoklanarak görülür
(`HaldusJobOptions.CancellationPollInterval`, varsayılan 2 saniye). Ayrıntısı [uzun süren işler](jobs.md) sayfasındadır.

## Vekil arkasında çalışmak

Uygulama bir vekilin (reverse proxy, yük dengeleyici) arkasındaysa isteğin adresi ve şeması vekilinkidir. ASP.NET
Core'un önerisi, vekili tanıtıp `X-Forwarded-For` / `X-Forwarded-Proto` başlıklarını okumaktır. Başlıklara yalnızca
tanıtılan vekillerden geldiklerinde güvenin; yoksa isteği yapan, adresini kendisi yazabilir:

<!-- example: forwarded-headers -->
```csharp
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

var app = builder.Build();

var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwarded.KnownProxies.Add(IPAddress.Parse("10.0.0.5"));
app.UseForwardedHeaders(forwarded);

app.UseHaldus();
app.MapCrud<Product>();

app.Run();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

Haldus açısından önemli olanlar:

- Haldus'un ürettiği adresler (`201` yanıtının `Location` başlığı, süreli indirme bağlantıları) sunucunun köküne
  görelidir ve isteğin `PathBase` değeriyle başlar; ana makine adı yazılmaz. Vekil uygulamayı bir alt yolda sunuyorsa
  (`/api/...`) o yolu `PathBase` olarak ayarlayın (ör. `app.UsePathBase("/api")`), yoksa adresler önek olmadan döner.
- Adrese göre hız sınırı (aşağıda) vekil tanıtılmadan herkesi tek bir adres sayar.

Örnek mağaza vekilleri yapılandırmadan okur (`Shop:KnownProxies`) ve yalnızca liste boş değilse
`UseForwardedHeaders` çağırır.

## Hız sınırlama

Haldus'un kendi hız sınırı yoktur. Token ile erişilen uçlar (`TokenAccess`) ve süreli bağlantılar oturumsuzdur; onlara
da bir sınır koymak sizin işinizdir. Her `Map*` builder'ı bir `IEndpointConventionBuilder` olduğu için ASP.NET
Core'un hız sınırlayıcısı kaynağın bütün uçlarına tek satırla bağlanır:

<!-- example: rate-limiting -->
```csharp
using System.Threading.RateLimiting;

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

// Adres başına dakikada 60 istek.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("per-address", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseHaldus();
app.UseRateLimiter();

app.MapCrud<Comment>().RequireRateLimiting("per-address");

app.Run();

public partial class Comment
{
    [MaxLength(2000)]
    public string Text { get; set; } = "";
}
```

Örnek mağaza parola ve kod deneyen uçlarını adres başına sınırlar; IPv6'da tek bir adres yerine `/64` bloğunu sayar
(`AuthEndpoints.PartitionOf`).

## Günlük

Haldus'un günlük kategorileri `Haldus` ad alanındadır (`Haldus.HaldusMaintenance`, `Haldus.HaldusEventDispatcher`...);
düzeylerini yapılandırmadan ayarlarsınız:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Haldus": "Information"
    }
  }
}
```

| Düzey | Ne yazılır |
|---|---|
| `Error` | Başarısız iş (`Job {JobId} of type '{JobType}' failed.`, ayrıntısıyla), yeniden deneme sınırını aşıp bırakılan iş, tanınmayan iş tipi. |
| `Warning` | Teslim edilemeyen olay (deneme sayısı ve yeniden denenip denenmeyeceği), başarısız bakım adımı, okunamayan iş kuyruğu. |
| `Debug` | Bakımın her turunda silinen, yayınlanan veya bırakılan kayıt sayıları. |

Bir işin mantığı `JobFailedException` fırlatırsa mesajı iş kaydının `Error` alanına yazılır ve istemci onu görür. Başka
bir hatanın ayrıntısı istemciye gösterilmez, yalnızca günlüğe yazılır.

Uçlarda beklenmeyen bir hata (`500`) Haldus'a özgü bir şekilde günlüğe yazılmaz; ASP.NET Core'un hata işleyicisi
yazar. `AddHaldus` `AddProblemDetails()` çağırır; `app.UseExceptionHandler()` eklerseniz `500` yanıtları da
problem details biçiminde döner (ASP.NET Core davranışı).

## PostgreSQL ve bağlantı havuzu

Haldus'un bazı özellikleri doğruluğu PostgreSQL'in kilitleriyle sağlar. Bu kilitler transaction'a bağlıdır
(`pg_advisory_xact_lock`): transaction bitince bırakılırlar ve oturuma bağlı bir kilit kullanılmaz. Kilidi bekleyen
istek, beklediği süre boyunca açık bir transaction ve bir veritabanı bağlantısı tutar.

| Kilit | Kapsamı | Kullananlar |
|---|---|---|
| Boşluksuz numara (`Numbered`) | Kaynak, kiracı ve desendeki tarih parçaları başına bir sayaç | Oluşturma (veya numaranın atandığı geçiş) |
| Sıra (`Orderable`) | Kapsam başına (bütün ağaç tek kilittir) | Ekleme, silme, taşıma |
| Bakiye (`MapBalance`) | Anahtar başına | `adjust`, `set`, `move`, tutmalar |
| Toplanan alanlar (`Counts`, `Sums`) | Üst kayıt başına | Alt kaydın her yazması |
| Türetme (`DerivedFrom`) | Kaynak tipi başına | Kaynağın her oluşturması |
| Dönemli kayıtlar (`EffectiveDated`) | Kapsam başına | Dönemli kaydın her yazması |
| Olay dağıtıcısı | Veritabanı başına | Her teslim turu |

Aynı kilidi bekleyen yazmalar sıraya girer: eşzamanlılık arttıkça verim değil yanıt süresi büyür. Depodaki yük ölçümü
([tools/Haldus.LoadTest](../../tools/Haldus.LoadTest)) tek bir makinede, tek kilidi paylaşan yazmalar için saniyede
yaklaşık 140–150 işlem gösterdi; anahtar başına kilitlenen bakiye ve toplanan alanlar farklı anahtarlarda ölçeklendi.
Bu sayılar o makinenin göreli kıyasıdır, bir kapasite vaadi değildir. Yazma hacmi yüksek bir kaynakta kilidi geniş olan
`DerivedFrom` ve `Orderable` dar boğaz olur.

Bağlantı havuzu için:

- Yük altında kilidi bekleyen her istek bir bağlantı tutar. Havuzun boyutunu (Npgsql'de bağlantı dizesindeki
  `Maximum Pool Size`; Npgsql'in varsayılanı 100'dür) eşzamanlı yazma sayınıza ve sunucu sayınıza göre seçin;
  PostgreSQL'in `max_connections` sınırı bütün sunucuların havuzlarının toplamını taşımalıdır.
- Arka plan servisleri de bağlantı kullanır: PostgreSQL iş kuyruğu her `PollInterval`'de kuyruğa bakar ve aynı anda en
  çok `MaxConcurrency` iş çalıştırır; olay dağıtıcısı her sunucuda çalışır ve kilidi beklerken bir bağlantı tutar.
  Kayıtlı bir olay yayıncısı yoksa dağıtıcı veritabanına hiç dokunmaz.
- Haldus'un PgBouncer gibi bir bağlantı havuzlayıcıyla (özellikle transaction kipinde) çalışması denenmedi.

## Sınırlar

- Bakım servisinin sunucular arasında kilidi yoktur; her sunucu kendi turunu çalıştırır.
- Haldus sağlık kontrolü (`/health`), ölçüm (metrics) veya izleme (tracing) uçları üretmez; bunlar ASP.NET Core'un ve
  Npgsql'in kendi araçlarıyla eklenir.
- Kalıcı Data Protection anahtarlarını Haldus kurmaz.
- Olay dağıtıcısı yavaş bir yayıncıyı beklerken veritabanı kilidini ve transaction'ı tutar; yavaş bir alıcı bütün
  teslimatı yavaşlatır.

## Ayrıca bakın

- [Yapılandırma](../reference/configuration.md)
- [Uzun süren işler](jobs.md)
- [Olay yayını](events.md)
- [Hassas veriler](sensitive-data.md)
- [Sırlar](secrets.md)
- [Dosyalar](files.md)
- [Veritabanı ve migration'lar](database.md)
