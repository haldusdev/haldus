# Sık sorulan sorular

Haldus'la çalışırken en sık sorulan sorular ve karşılaşılan sorunlar. Her cevap kısadır ve ayrıntının bulunduğu sayfaya
bağlanır.

Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır; `builder` ve `app` değişkenlerini kuran satırlar da öyle.

## İçindekiler

- [Genel](#genel)
- [Model ve zincir](#model-ve-zincir)
- [HTTP ve hata yanıtları](#http-ve-hata-yanıtları)
- [Veritabanı](#veritabanı)
- [Arayüzler](#arayüzler)
- [Sorun giderme](#sorun-giderme)

## Genel

### Haldus hangi .NET sürümüyle çalışır?

.NET 10 ile. Paketler `net10.0` hedefler; EF Core ve ASP.NET Core 10.x hattındadır. Bkz.
[kurulum](getting-started/installation.md).

### SQL Server, MySQL veya SQLite destekleniyor mu?

Hayır, yalnızca PostgreSQL. Haldus'un kilitleri (danışma kilitleri), boşluksuz numara sayacı
(`INSERT ... ON CONFLICT`), iş kuyruğu (`FOR UPDATE SKIP LOCKED`), `jsonb` kolonları ve benzersizlik ihlalinin
tanınması PostgreSQL'e özgüdür. Uygulama `Npgsql.EntityFrameworkCore.PostgreSQL` sağlayıcısını kullanır. Bkz.
[veritabanı ve migration'lar](guides/database.md).

### Lisansı nedir?

MIT. Lisans metni deponun kökündeki [LICENSE](../LICENSE) dosyasındadır; paketler de lisansı taşır
(`PackageLicenseExpression`).

### Paketler NuGet'te mi?

Henüz değil. Paketler depodan üretilir ve yerel bir klasörden tüketilir. Bkz. [kurulum](getting-started/installation.md)
ve [paketler](reference/packages.md).

## Model ve zincir

### Üretilen kodu nerede görürüm?

Proje dosyasına `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` ekleyin ve derleyin. Dosyalar
`obj/Generated/Haldus.Generators/Haldus.Generators.HaldusGenerator/` altındadır: entity'nin öteki yarısı
(`*.Entity.g.cs`), DTO'lar (`*.Dtos.g.cs`), uçlar (`*.Endpoints.g.cs`) ve model yapılandırması
(`HaldusModule.g.cs`). Bu dosyaları elle değiştirmeyin; her derlemede yeniden üretilirler. Bkz.
[Haldus nasıl çalışır](concepts/how-it-works.md).

### Entity'ye `Id`, `Status`, `TenantId` gibi alanları yazmalı mıyım?

Hayır. Anahtarı ve bildirimlerin eklediği alanları (`Status`, `TenantId`, `OwnerId`, `CreatedAt`, `CategoryId`,
satır koleksiyonları...) Haldus üretir. Siz yalnızca kendi alanlarınızı yazarsınız. Anahtarın tipini değiştirmek
istiyorsanız `Id`'yi `Guid`, `int` veya `long` olarak yazabilirsiniz; bkz. [archetype'lar](guides/archetypes.md#mapcrud).

### Bir alanı neden istekte yazamıyorum?

İstek DTO'larına yalnızca herkese açık setter'ı olan skaler alanlar girer. Bir alan istekte yoksa nedeni şunlardan
biridir:

| Neden | Ne yapılır |
|---|---|
| Setter'ı herkese açık değil (`{ get; private set; }`) | Bilerek yalnızca yanıtta görünen alandır; değerini `Compute()` veya iş mantığı yazar. |
| Haldus üretiyor (`Status`, `Number`, `CreatedAt`, `TenantId`, `OwnerId`) | `Status` yalnızca geçişlerle değişir; öteki alanları Haldus doldurur. |
| Yalnızca oluştururken yazılıyor (`CreateOnly`) | Alan `POST` isteğinde vardır, `PUT` ve `PATCH` isteklerinde yoktur. |
| Archetype yazmaya izin vermiyor (`MapReadOnly`, `MapAppendOnly`'de güncelleme) | Kayıtları kendi kodunuz yazar ya da archetype'ı değiştirirsiniz. |
| Koleksiyon veya iç içe tip | Satırlar için `MapDetail`, değer nesneleri için `Embeds<T>()` kullanılır; bkz. [ilişkiler](guides/relationships.md#gömülü-değer-nesnesi). |

İstek tipinde olmayan bir alanı göndermek hata değildir; alan yok sayılır. Ayrıntısı:
[yalnızca oluştururken yazılan alanlar](guides/fields.md#yalnızca-oluştururken-yazılan-alanlar),
[durum makineleri](guides/state-machines.md).

### Bir alan neden yanıtta görünmüyor?

`Sensitive` ve `Encrypted` alanlar yalnızca yazılır, hiçbir yanıtta görünmez. Sürüm kontrollü kaynağın sürümü gövdede
değil `ETag` başlığındadır. `OwnerId` yanıtta yinelenmez. Bkz. [hassas alanlar](guides/sensitive-data.md) ve
[`ETag` ve `If-Match`](reference/http.md#etag-ve-if-match).

### Zinciri koşullu kurabilir miyim?

Hayır. Generator zinciri derleme zamanında, kaynak koddan okur; bir değişken üzerinden sonradan eklenen bildirimi
göremez ve `HALDUS004` verir:

<!-- example: conditional-chain, error: HALDUS004 -->
```csharp
var products = app.MapCrud<Product>();

if (app.Environment.IsProduction())
{
    products.ConcurrencyChecked();
}

public partial class Product
{
    public string Name { get; set; } = "";
}
```

Bildirimin argümanları da derleme zamanında okunur; yapılandırmadan okunan bir değer verilemez (`HALDUS010`). Kaynağın
şekli (alanlar, uçlar, tablo) her ortamda aynıdır. Ortama göre değişmesi gereken şey bir kural değil bir davranışsa
onu iş mantığında yazın; yetkilendirme gibi Minimal API ayarları ise builder'a çalışma zamanında eklenebilir (aşağıda).
Bkz. [tanılar](reference/diagnostics.md#haldus004).

### Kendi ucumu nasıl eklerim?

Haldus'un uçları sıradan Minimal API uçlarıdır; kendi uçlarınızı yanlarına her zamanki gibi yazarsınız.
`Map*` çağrısının döndürdüğü builder bir route group gibi davranır: ona eşlediğiniz uç kaynağın adresi altına girer,
ona eklediğiniz kural (yetkilendirme, hız sınırı) kaynağın bütün uçlarına uygulanır.

<!-- example: own-endpoint -->
```csharp
builder.Services.AddAuthorization();

var app = builder.Build();

var products = app.MapCrud<Product>();

// GET /products/featured: kaynağın adresi altında, sizin yazdığınız bir uç.
products.MapGet("/featured", async (AppDbContext db, CancellationToken cancellationToken) =>
    await db.Set<Product>()
        .Where(product => product.Featured)
        .Select(product => new { product.Id, product.Name })
        .ToListAsync(cancellationToken));

// Kaynağın bütün uçları giriş ister.
products.RequireAuthorization();

// Haldus'tan bağımsız bir uç.
app.MapGet("/health", () => Results.Ok());

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public bool Featured { get; set; }
}
```

Bu çağrılar bir bildirim değildir; değişken üzerinden yapılabilir ve `HALDUS004` vermez. Bir kayıt üzerinde çalışan iş
mantığı için kendi ucunuzu yazmak yerine geçiş veya entity aksiyonu bildirin; böylece durum, sürüm ve tekrar kuralları
o uca da uygulanır. Bkz. [iş mantığı](guides/business-logic.md).

### İş mantığını nereye yazarım?

Üretilen koda değil, Haldus'un sözleşmelerine: hesaplanan alanlar için `IComputed`, geçişler için `ITransition<T>` veya
`ITransitionHandler<T, TRequest>`, aksiyonlar için `IEntityAction<T>`, işler için `IJobHandler<T>`, liste süzmesi için
`IFilter<T>`. Haldus handler sınıflarını bulur ve DI'a kaydeder. Bkz. [iş mantığı](guides/business-logic.md).

### Bir trait'i neden birleştiremiyorum? (`HALDUS007`)

Bazı bildirimler archetype'la veya birbiriyle çelişir: eklenen ve hiç değişmeyen kaydın (`MapAppendOnly`) sürümü
olmaz, tek kaydın (`MapSingleton`) listesi süzülmez, yayın kopyası durum makinesini taşımaz. Haldus böyle bir bileşimi
sessizce yok saymak yerine derlemeyi durdurur. Hatanın mesajı iki bildirimi, nedeni ve çoğu zaman yerine ne
yazılacağını söyler. Bkz. [tanılar](reference/diagnostics.md#haldus007) ve
[archetype'lar](guides/archetypes.md#birlikte-kullanım).

### Liste ucunu `?name=...` ile neden süzemiyorum?

Haldus alan başına sorgu parametresi üretmez. Liste ucunu süzmek için bir `IFilter<T>` sınıfı yazıp
`.Filterable<TFilter>()` ile bağlarsınız; gövdeli, birleştirilebilir koşullar için `.Searchable(...)` vardır. Bkz.
[süzme](guides/querying.md#süzme) ve [arama](guides/querying.md#arama).

## HTTP ve hata yanıtları

### Neden 409 alıyorum?

Kaydın şimdiki hali isteğe izin vermiyor. En sık nedenler: başka kayıtların referans verdiği kaydı silmek, benzersiz bir
değerin (doğal anahtar) zaten alınmış olması, durum makinesinde kaydın yanlış durumda olması (`EditableIn`,
`DeletableIn`, geçişin `from:` durumları), istek işlenirken durumun başka bir istekle değişmesi, yetersiz bakiye.
Yanıtın `detail` alanı nedeni söyler. Bkz. [durum kodları](reference/http.md#durum-kodları).

### Neden 428 alıyorum?

Kaynak `.ConcurrencyChecked()` ile sürüm kontrollüdür ve isteğinizde `If-Match` başlığı yok. Kaydı okuyun, yanıttaki
`ETag` değerini `If-Match` ile gönderin. Bu, `PUT` ve `PATCH`'in yanında silme, geçişler, aksiyonlar ve `archive`,
`restore` gibi uçlar için de geçerlidir. Bkz. [`ETag` ve `If-Match`](reference/http.md#etag-ve-if-match).

### Neden 412 alıyorum?

Gönderdiğiniz `If-Match` sürümü kaydın şimdiki sürümü değil: siz okuduktan sonra kaydı başka biri değiştirdi (ya da
başlık okunamadı). Kaydı yeniden okuyun, değişikliği yeni hal üzerinde yapın ve yeni `ETag` ile gönderin. Bkz.
[eşzamanlılık denetimi](guides/lifecycle.md#eşzamanlılık-denetimi).

### Kiracıya ait bir kaynakta neden 403 alıyorum?

İstekte kiracı çözülemedi. Varsayılan olarak kiracı, token'daki `tenant_id` claim'inden `Guid` olarak okunur; claim
yoksa veya `Guid` değilse kiracıya ait (`MultiTenant`) kaynaklar 403 döner. Claim'in adını `HaldusOptions.TenantClaimType`
ile değiştirebilir ya da `ITenantProvider` servisini değiştirebilirsiniz. Bkz.
[yapılandırma](reference/configuration.md#değiştirilebilen-servisler).

### Kayıt var ama neden 404 alıyorum?

Kayıt isteği yapana görünmüyor: başka bir kiracının veya kullanıcının kaydı, silinmiş (`SoftDeletable`), süresi dolmuş
(`Expiring`) ya da görünmeyen bir ebeveynin altındaki kayıt. Haldus bunları "yok" sayar; yanıt kaydın var olup
olmadığını ele vermez. Adresteki kimlik anahtarın tipine uymuyorsa (`/products/abc`) da 404 döner. Bkz.
[kiracılar ve sahiplik](guides/tenancy-and-ownership.md).

### Hata mesajları neden İngilizce?

Mesajların dili `Accept-Language` başlığından çözülür; İngilizce ve Türkçe desteklenir. `app.UseHaldus()` çağrılmadıysa
istek dili hiç çözülmez. Metinleri değiştirmek veya dil eklemek için bkz.
[mesajlar ve dil](reference/configuration.md#mesajlar-ve-dil).

### Sayılar istemcide neden `number | string` görünüyor?

ASP.NET Core'un varsayılan JSON ayarı sayıları metin olarak da kabul eder ve API şemasına `integer | string` yazar.
Sayıları katı okutursanız şema yalnızca sayı der:

<!-- example: strict-numbers -->
```csharp
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict);

var app = builder.Build();
app.MapCrud<Product>();

public partial class Product
{
    public decimal Price { get; set; }
}
```

Bu, uygulamanın ayarıdır; Haldus onu dayatmaz. Bkz. [frontend ve mobil](guides/frontend.md).

## Veritabanı

### Migration nasıl yapılır?

Sıradan EF Core migration'larıyla. Haldus'un ürettiği model sıradan bir EF Core modelidir ve araç onu olduğu gibi
görür; Haldus'un kendi tabloları (`HaldusCounters`, `HaldusIdempotencyRecords`...) yalnızca onları kullanan bir bildirim
varsa aynı migration'a girer:

```bash
dotnet ef migrations add AddProducts
dotnet ef database update
```

Haldus tabloları kendiliğinden oluşturmaz. Zincire bir trait eklemek modeli değiştirebilir (yeni kolon, indeks veya
tablo); her zincir değişikliğinden sonra yeni bir migration üretin ve inceleyin. Bkz.
[veritabanı ve migration'lar](guides/database.md).

### EF Core'u doğrudan kullanabilir miyim?

Evet. Bağlam sizindir; eşlenen entity'lere `Set<T>()` ile (bağlam aynı projede `partial` ise `DbSet` özellikleriyle)
ulaşırsınız ve kendi sorgularınızı yazarsınız. Haldus'un sorgu filtreleri (kiracı, sahip, yumuşak silme, süre dolumu)
sizin sorgularınıza da uygulanır. Bir filtreyi kaldırmanız gerekiyorsa adıyla kaldırın
(`IgnoreQueryFilters([HaldusFilters.SoftDelete])`); parametresiz `IgnoreQueryFilters()` kiracı ve sahiplik korumasını
da kaldırır. Bkz. [kiracılar ve sahiplik](guides/tenancy-and-ownership.md).

## Arayüzler

### Haldus frontend veya mobil kodu üretiyor mu?

Hayır. Haldus backend'de kalır. Arayüzlere verdiği şey sözleşmedir: uygulamanın OpenAPI belgesi
(`Microsoft.AspNetCore.OpenApi`). İstemci tiplerini ve hook'ları o belgeden bir araçla üretirsiniz; örnekler orval ile
TanStack Query hook'ları üretir. Bkz. [frontend ve mobil](guides/frontend.md) ve [örnekler](samples.md).

## Sorun giderme

### "No Haldus endpoints were generated for ..." hatası

Uygulama açılırken bu `InvalidOperationException` geliyorsa `Map*` çağrısının bulunduğu projede generator
çalışmamıştır. O projenin `Haldus.AspNetCore` paketine (veya bir `Haldus.Jobs.*` / `Haldus.Storage.S3` paketine)
başvurduğundan emin olun; yalnızca `Haldus.Abstractions` veya `Haldus.EntityFrameworkCore` paketine başvuran projede
generator çalışmaz. Bkz. [paketler](reference/packages.md).

### "MapJob<...> needs a job infrastructure" hatası

`MapJob` kullanıyorsunuz ama bir iş altyapısı kaydetmediniz. `Haldus.Jobs.InMemory`, `Haldus.Jobs.PostgreSql` veya
`Haldus.Jobs.Hangfire` paketlerinden birini ekleyin ve kayıt metodunu çağırın (`AddHaldusInMemoryJobs()` gibi). Bkz.
[iş altyapısı](guides/jobs.md#altyapı).

### Derleme `HALDUS0xx` hatası veriyor

Her tanının ne demek olduğu ve nasıl düzeltileceği [tanılar](reference/diagnostics.md) sayfasındadır. IDE'nizdeki
yardım bağlantısı aynı tanının İngilizce başvurusuna gider.

### Paketi yeniden ürettim ama değişiklik projeme gelmiyor

NuGet bir paketi sürümüyle önbelleğe alır. Sürümü ilerletin (`--version-suffix preview.2`) ya da önbellekteki
`haldus.*` klasörlerini silin. Bkz. [paketler](reference/packages.md#sürüm-ve-önizleme-durumu).
