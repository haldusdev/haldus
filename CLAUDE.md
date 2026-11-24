# Haldus

Archetype ve trait'lerle tekrarlayan backend kodunu derleme zamanında üreten .NET 10 framework'ü.
`app.MapCrud<Product>().MultiTenant()` gibi bir çağrıdan entity, DTO, Minimal API endpoint'leri ve
EF Core yapılandırması üretilir.

## Komutlar

- Derleme: `dotnet build`
- Testler: `dotnet test` (entegrasyon testleri Docker ister; Testcontainers PostgreSQL başlatır)
- Tek test projesi: `dotnet test --project tests/Haldus.Generators.Tests`
- Snapshot'ları onaylamak: `HALDUS_ACCEPT_SNAPSHOTS=1` ortam değişkeniyle generator testlerini çalıştır.
  Fark olduğunda test, `Snapshots/` altına `.received.cs` yazar; önce onu incele.
- Test çalıştırıcısı Microsoft.Testing.Platform'dur (`global.json`); xUnit v3, .NET 10 SDK'da VSTest ile çalışmaz.
- Örnek uygulamanın `openapi.json` dosyası build sırasında yeniden üretilir.
- Örnek uygulamada üretilen kod `samples/Haldus.Sample.Shop/obj/Generated` altında incelenebilir.

## Yapı

- `src/Haldus.Abstractions`: bağımlılıksız sözleşmeler (sayfalama tipleri, trait arayüzleri).
- `src/Haldus.Generators`: Roslyn incremental generator (netstandard2.0, Roslyn 5.0.0'a sabit).
- `src/Haldus.EntityFrameworkCore`: model kayıt defteri, interceptor'lar, sorgu filtreleri.
- `src/Haldus.AspNetCore`: `AddHaldus`, `Map*` giriş noktaları, validasyon, hata yanıtları, `.resx`.
- `tests/Haldus.Generators.Tests`: generator çıktısı testleri.
- `tests/Haldus.IntegrationTests`: örnek uygulamaya karşı gerçek PostgreSQL testleri.
- `samples/Haldus.Sample.Shop`: örnek uygulama ve modelin sınandığı yer.
- `reports/`, `research_notes/`: archetype/trait taksonomisi araştırması. Tasarım kararlarının dayanağı
  `reports/Haldus archetype ve trait taksonomisi.md` dosyasındadır.

## Tasarım kararları

Bunlar kullanıcıyla kararlaştırıldı; yeniden sorma.

### Model

- Her entity dört katmanla tanımlanır: tam olarak bir archetype (`Map*`), sıfır veya daha çok ilişki
  bildirimi, en fazla bir durum modeli, serbestçe birleşen trait'ler.
- Her archetype kendi `Map*()` metodudur. `MapArchetype` gibi genel bir giriş noktası yok.
- Trait'ler, ilişkiler ve durum modeli zincir metodudur: `app.MapCrud<Order>().Workflow<OrderStatus>()`.
  `MapWorkflow` kısaltması yok.
- Entity'yi Haldus üretir; kullanıcı kendi alanlarını `partial class` ile ekler.
- Zincir derleme zamanında okunur; statik olmalıdır (koşullu veya değişken üzerinden sonradan yapılan
  zincir çağrıları desteklenmez ve analyzer hatası olmalıdır).
- İlk sürüm archetype'ları: `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapStore`, `MapSingleton`,
  `MapLookup`, `MapDetail`, `MapAssociation`, `MapAction`, `MapQuery`, `MapJob`.
- `MapDetail` varsayılan olarak üst kaydın isteğinde gömülü yazılır; `.AsSubresource()` ile ayrı
  endpoint'lere geçer.
- Eşzamanlılık trait'inin adı `ConcurrencyChecked` (eski adı `Versioned`). Sürüm yanıt gövdesinde değil
  `ETag` başlığında taşınır; güncelleme ve silme `If-Match` ister (eksikse 428, uyuşmazlıkta 412).
- Güncelleme için hem `PUT` (tam değiştirme) hem `PATCH` (kısmi) üretilir.
- Geçiş endpoint'leri `/orders/{id}/cancel` biçimindedir.
- `MultiTenant` kiracıyı token'daki claim'den okur.

### Proje tipleri

- Tek projeli ve modüler (her özellik ayrı sınıf kütüphanesi) yapı birlikte desteklenir; tek proje,
  tek modüllü haldir.
- Veritabanı bağlamı proje başına seçilir: ortak tek `DbContext` (`AddHaldus<TContext>()`) veya modül
  başına ayrı (`AddHaldusModule<TContext>()`).
- Bu yüzden üretilen kod somut `DbContext` tipine bağlanmaz; `IHaldusDbContextProvider` üzerinden
  çalışır ve model katkıları modül (assembly) anahtarıyla kaydedilir.

### Teknoloji

- Mekanizma: Roslyn incremental source generator. Çalışma zamanı reflection'ı yalnızca validasyon
  filtresinde var.
- API stili: Minimal API.
- PATCH DTO'sunda "gönderilmedi" ile "null gönderildi" ayrımı, init erişimcisinin yalnızca JSON'da bulunan
  alanlar için çağrılmasına dayanır (`IHaldusPatch`); şemada sarmalayıcı tip görünmez.
- Validasyon: entity'deki DataAnnotations üretilen istek DTO'larına kopyalanır. .NET 10'un
  `AddValidation` generator'ı başka bir generator'ın ürettiği DTO'ları göremediği için kurallar
  Haldus'un kendi endpoint filtresinde çalıştırılır.
- i18n: son kullanıcıya dönen mesajlar `.resx` dosyalarından gelir; diller en (varsayılan) ve tr.
  Metinler `IHaldusMessages` servisi üzerinden okunur; tüketen proje servisi değiştirerek ezebilir.
- Veritabanı: PostgreSQL. ORM: EF Core.
- Test: xUnit v3, entegrasyon testlerinde Testcontainers. Snapshot karşılaştırması repo içindeki
  `Snapshot` yardımcısıyla yapılır; Verify kullanılmaz (33.x sürümü sponsorluk lisansı istiyor).

## Aşamalar

1 ve 2 tamamlandı. 3. aşamada `Filterable` dışındaki her şey tamamlandı; sıradaki 4.

1. İskelet
2. Generator çekirdeği ve `MapCrud`
3. Temel trait'ler: `MultiTenant`, `Auditable`, `SoftDeletable`, `ConcurrencyChecked`, `Archivable`,
   `Filterable`, `.Without()` / `.Only()`
4. Yalın archetype'lar: `MapReadOnly`, `MapAppendOnly`, `MapStore`, `MapSingleton`, `MapLookup`,
   `MapAction`, `MapQuery`
5. İlişkiler: `.References<T>()`, `.Embeds<T>()`, `.Under<T>()`, `.Hierarchical()`, `MapAssociation`
6. `MapDetail`, hesaplanmış alanlar
7. `.Workflow()`, `Numbered`, `Expiring`, `Idempotent`
8. `MapJob`, `Batch`
9. Modüler yapı ve iki `DbContext` modu
10. Örnek mağaza uygulaması ve araştırmadaki entity tablosunun sınanması

## Açık kararlar

- `Filterable` sözdizimi: kullanıcı hem alan başına sorgu parametresini (`?priceMin=100`) hem tek
  `filter` ifadesini (AIP-160 tarzı) reddetti ve başka bir yöntem istedi. Karar verilene kadar üretilmez.
- Frontend ve mobil desteğinin kapsamı (backend API şekli oturunca ele alınacak).
- `MapJob` altyapısı (8. aşamada sorulacak).

## Generator'ın işleyişi

- `Parsing/MapCallParser` her `Map*` çağrısını `ResourceModel`'e çevirir; `Parsing/ChainReader` onu
  izleyen zinciri trait ve işlem seçimlerine dönüştürür. Zincir metotları çalışma zamanında boştur.
- Yeni bir trait eklemek: `Model/TraitCatalog` (arayüz ve alanlar), `ChainReader` (zincir adı),
  `ResourceBuilderTraitExtensions` (boş zincir metodu), gerekiyorsa `EndpointEmitter` ve
  `ModuleEmitter.AppendModel`.
- Trait davranışları çalışma zamanında arayüz üzerinden işler: `HaldusSaveChangesInterceptor` alanları
  doldurur ve soft delete'i güncellemeye çevirir; sorgu filtreleri EF Core 10'un adlandırılmış
  filtreleridir (`HaldusFilters`), böylece tek bir filtre yok sayılabilir.
- Kiracı filtresi modelin kurulduğu bağlamı yakalar (`HaldusScope.TenantId(context)`); EF Core bu
  başvuruyu her sorguda geçerli bağlamla değiştirir. Entegrasyon testleri bunu iki kiracıyla doğrular.
- Zaman damgaları `HaldusTime.UtcNow` ile mikrosaniyeye kırpılır; PostgreSQL daha hassasını saklamaz.
- Üretilen kod çalışma zamanına bir module initializer ile bağlanır: `HaldusEndpointRegistry` (entity →
  endpoint eşleyici) ve `HaldusModelRegistry` (assembly → model katkısı). `MapCrud<T>()` çalışma
  zamanında yalnızca bu kayda bakar.
- Kullanıcı `Id` yazmadıysa `Guid` (v7) üretilir; yazdıysa `Guid`, `int` veya `long` olmalıdır.
- DTO'lara yalnızca skaler alanlar girer; herkese açık setter'ı olmayan alanlar yalnızca yanıtta yer alır.
- `AddHaldus<TContext>()` aynı projedeki partial bir bağlamı gösteriyorsa DbSet özellikleri eklenir.
- Tanılar `HALDUS001`+ kodlarıyla `Diagnostics/HaldusDiagnostics.cs` ve `AnalyzerReleases.Unshipped.md`
  dosyalarında birlikte tutulur.

## Kurallar

- Üretilen kodda ve kütüphanelerde kod İngilizce, yorumlar Türkçe.
- Generator testleri üretilen çıktının hatasız derlendiğini de doğrulamalıdır.
- .NET 11 yayınlandı ama LTS değil; paketler 10.x hattında tutulur.
