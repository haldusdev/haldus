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
- Anahtar özelliğinin adı archetype'a göre değişir: `Id` (Crud, ReadOnly, AppendOnly; sunucu üretir),
  `Key` (Store; istemci verir), `Code` (Lookup; istemci verir). Singleton'ın anahtarı entity'de görünmez.
- `MapStore` ve `MapLookup`'ta `PUT /{key}` ekle-veya-güncelledir. `MapLookup` yazma uçlarını da üretir
  (kullanıcı kararı); listesi sayfalamasızdır ve `Code` + `Name` alanları üretilir.
- `MapSingleton` kaydı örtük var sayar: hiç yazılmadıysa `GET` entity'nin varsayılan değerlerini döner.
  `.MultiTenant()` ile kapsam kiracı olur.
- `MapAction` (`POST`) ve `MapQuery` (`GET`) entity'ye bağlı değildir. İş mantığı ya bir handler
  sınıfında (`IActionHandler`, `IQueryHandler`; Haldus bulur ve DI'a kaydeder) ya da Map çağrısına verilen
  lambda'dadır; ikisi de desteklenir (kullanıcı kararı). Lambda bağımlılık enjeksiyonu almaz; gerekirse
  `IServiceProvider` alan biçimi kullanılır.
- Yazılmamış bir entity, `Map*` çağrısının bulunduğu namespace'e üretilir.
- İlişkiler zincirde bildirilir:
  - `.References<T>()`: yabancı anahtar alanı üretilir (`CategoryId`; adı hedefin adı + anahtar adıdır)
    ve her yazmada hedefin var olduğu doğrulanır; bulunamazsa alan düzeyinde 400 döner. Varsayılan silme
    davranışı `Restrict`'tir; referans verilen kaydın silinmesi 409 döner.
  - `.Under<T>()`: kaynak ebeveynin adresi altına yerleşir (`/projects/{projectId}/tasks`); ebeveyn
    adresten gelir, istek gövdesinde yer almaz. Varsayılan silme davranışı `Cascade`'dir.
  - `.Hierarchical()`: isteğe bağlı `ParentId`; bir düğüm kendi altına taşınamaz, çocuğu olan düğüm
    silinemez.
  - `.Embeds<T>()`: o tipteki alanlar gömülü değer nesnesi olur (DTO'da iç içe nesne, EF'de complex
    property). Alan kullanıcının partial sınıfında yazılır; nullable olamaz.
  - `MapAssociation<TLeft, TRight>()`: bağ entity'si (`ProductTag`) üretilir; bağ iki yönden listelenir,
    `PUT` ile kurulur, `DELETE` ile kaldırılır. İki uç da bir `Map*` çağrısıyla eşlenmiş olmalıdır.
- `MapDetail` varsayılan olarak üst kaydın isteğinde gömülü yazılır; `.AsSubresource()` ile ayrı
  endpoint'lere geçer. Sahibin builder'ından çağrılır: `orders.MapDetail<OrderItem>()`.
- Eşzamanlılık trait'inin adı `ConcurrencyChecked` (eski adı `Versioned`). Sürüm yanıt gövdesinde değil
  `ETag` başlığında taşınır; güncelleme ve silme `If-Match` ister (eksikse 428, uyuşmazlıkta 412).
- Güncelleme için hem `PUT` (tam değiştirme) hem `PATCH` (kısmi) üretilir.
- Süzme iki yolla yapılır ve ikisi birlikte kullanılabilir. Kullanıcı, otomatik üretilen alan başına
  sorgu parametrelerini (`?priceMin=100`) ve tek `filter` ifadesini (AIP-160 tarzı) reddetti:
  - `.Filterable<TFilter>()`: kullanıcının yazdığı `IFilter<T>` sınıfı `GET` listesine bağlanır; skaler
    özellikleri camelCase, isteğe bağlı sorgu parametresi olur.
  - `.Searchable(item => item.Name, ...)`: `POST /{kaynak}/search`; tipli gövdede alan koşulları,
    `and` / `or` / `not` ve sıralama. Yalnızca sayılan alanlar aranabilir; varsayılan olarak hiçbiri
    aranamaz. Bilinmeyen alan veya operatör isteği reddeder; boyut `SearchBudget` ile sınırlıdır.
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

1–6 tamamlandı (3. aşamaya `.Searchable()` da eklendi); sıradaki 7.

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
- `Filterable` ve `Searchable` uzantı değil `ResourceBuilder<T>` üyesidir; C# uzantı metodunda tip
  argümanlarının bir kısmını çıkarsayamadığı için `.Filterable<ProductFilter>()` ancak böyle yazılır.
- Arama koşulları `HaldusSearch` ile ifade ağacına çevrilir; değerler ifadeye sabit olarak değil bir
  nesnenin özelliği olarak konur ki EF Core onları SQL parametresi yapsın. Bir alanın koşul tipi
  (`StringCondition`, `RangeCondition<T>`, `EqualityCondition<T>`) `MapCallParser.TryClassify` ile seçilir.
- Her `Map*` çağrısı tek başına ayrıştırılır; başka kaynaklara bağlı her şey (referans hedefinin anahtar
  tipi, üretilen yabancı anahtar alanları, aranabilir alanların doğrulanması, bağların uçları)
  `Linking/ResourceLinker` içinde, tüm kaynaklar bilindikten sonra çözülür.
- **Görünürlük modele konur, tek tek sorgulara değil.** Bir kaynağın görünürlüğü başka bir kayıttan
  geliyorsa (ebeveyn altındaki kaynak, çoktan-çoğa bağ) bunu adlandırılmış bir sorgu filtresi sağlar
  (`HaldusFilters.Parent`, `HaldusFilters.Association`). Böylece o kaynağa referans veren, onun altına
  yerleşen veya onu listeleyen her sorgu kendiliğinden korunur. Filtreyi kaldıran uçlar yalnızca kendi
  filtresini adıyla kaldırmalıdır (`IgnoreQueryFilters(new[] { HaldusFilters.SoftDelete })`); parametresiz
  `IgnoreQueryFilters()` kiracı ve ebeveyn korumasını da kaldırır ve üretilen kodda kullanılmaz.
- **Zincirleme silme kiracı sınırını aşmaz.** Genel bir kayıt silindiğinde kiracıya ait kayıtlar
  kendiliğinden silinmez: genel bir uç ile kiracıya ait bir uç arasındaki bağda genel taraf `Restrict`
  olur; kiracıya ait kayıtlar genel bir ebeveynin altındaysa `.Under<T>()` varsayılanı `Cascade` yerine
  `Restrict` olur (geliştirici `OnDelete.Cascade` yazarak açıkça seçebilir). Bir kaynağın kiracıya ait
  olup olmadığına `ResourceLinker.IsTenantScoped` karar verir: kendisi `MultiTenant`'tır veya öyle bir
  ebeveynin altındadır.
- **Detaylar.** `MapDetail` uzantı değil `ResourceBuilder<T>` üyesidir; sahibi, çağrıldığı builder'ın tip
  argümanından okunur, bu yüzden bir değişken üzerinden çağrılabilir (`orders.MapDetail<OrderItem>()`).
  `AsSubresource` ise detayın kendi zincirine aittir ve değişken üzerinden çağrılamaz (`HALDUS004`).
  `ResourceLinker.PrepareDetails` detaya sahibine giden zorunlu, zincirleme silinen bir ebeveyn referansı
  ekler; görünürlüğü de (`HaldusFilters.Parent`) buradan gelir.
  - Gömülü detayın kendi uçları yoktur (`Archetype.Detail`): sahibin entity'sine koleksiyon, DTO'larına
    satır listesi eklenir. Oluşturmada satırlar isteğe bağlıdır; `PUT`'ta zorunludur (gönderilmeyen
    koleksiyon "hepsini sil" sayılmaz); `PATCH`'te yalnızca gönderildiyse eşitlenir.
  - Eşitleme (`Sync{Koleksiyon}`): kimliği verilen satır güncellenir, verilmeyen eklenir, istekte
    bulunmayan silinir. Kimlik o kayda ait değilse veya iki kez geçiyorsa alan düzeyinde 400 döner ve
    hiçbir şey kaydedilmez.
  - Satırlar istekte gönderildikleri sırayla saklanır ve döner. Sırayı Haldus'a ait `Position` alanı
    tutar (kullanıcı yazdıysa o kullanılır); DTO'larda yer almaz. Kimliğe göre sıralama yetmez: aynı
    milisaniyede üretilen v7 kimlikler üretim sırasıyla sıralı değildir.
  - Yalnızca satırlar değişse de sahip `Modified` işaretlenir; böylece sürümü ve değiştirilme zamanı ilerler.
  - Sınırlar: gömülü detayın `Id`'si `Guid` olmalıdır; detayın detayı yoktur; gömülü detay `MapCrud` ve
    `MapAppendOnly` altında, ayrı uçlu detay bunlara ek olarak `MapReadOnly` altında bildirilebilir.
    Ayrı uçlu detaydaki değişiklik sahibin `Compute`'unu çalıştırmaz ve sürümünü ilerletmez.
- **Hesaplanmış alanlar.** Entity `IComputed` uygularsa yazma uçları isteği uyguladıktan ve gömülü
  satırları yükledikten sonra, kaydetmeden önce `Compute()` çağırır. Hesaplanan alan herkese açık
  setter'ı olmayan bir alandır; bu yüzden yalnızca yanıtta görünür ve veritabanında saklanır (süzülebilir,
  sıralanabilir). Arayüz biçimini kullanıcı ayrıca onaylamadı; değiştirilmesi istenirse tek yer
  `EndpointEmitter.Compute`'tur.
- Üretilen kod, tüketen projede örtük `using`'ler kapalıyken de derlenmelidir; generator testleri bunu
  doğrular (örnek uygulama doğrulamaz, çünkü onda örtük `using`'ler açıktır).
- Yeni bir archetype eklemek: `Model/ResourceModel.cs` içindeki `Archetype` enum'u ve uzantıları (temel
  işlemler, anahtar adı), `MapCallParser.ArchetypeOf` ve `Conflicts`, `EndpointEmitter`,
  `ModuleEmitter.AppendModel` ve çalışma zamanındaki `Map*` metodu.
- Archetype'ın anlamıyla çelişen zincir bildirimleri sessizce yok sayılmaz, `HALDUS007` olur. Özellikle
  liste işlemi kaldırılmış bir kaynakta `Searchable` arama ucu açamaz.
- `MapQuery` generator gerektirir (istek tipinin özellikleri sorgu parametrelerine tek tek bağlanır);
  `MapAction` gerektirmez (gövdeden bağlanır, endpoint çalışma zamanında kurulur). Handler eksikse hata
  ilk istekte değil uygulama açılırken verilir.
- Eşzamanlılık kontrollü kaynakta durum değiştiren her uç (`restore`, `purge`, `archive` dahil) `If-Match`
  ister; yeni bir uç eklerken `ReadIfMatch` / `ExpectVersion` / `Save` üçlüsünü kullan.
- Üretilen kod çalışma zamanına bir module initializer ile bağlanır: `HaldusEndpointRegistry` (entity →
  endpoint eşleyici) ve `HaldusModelRegistry` (assembly → model katkısı). `MapCrud<T>()` çalışma
  zamanında yalnızca bu kayda bakar.
- Kullanıcı `Id` yazmadıysa `Guid` (v7) üretilir; yazdıysa `Guid`, `int` veya `long` olmalıdır.
- DTO'lara yalnızca skaler alanlar girer; herkese açık setter'ı olmayan alanlar yalnızca yanıtta yer alır.
- `AddHaldus<TContext>()` aynı projedeki partial bir bağlamı gösteriyorsa DbSet özellikleri eklenir.
- Tanılar `HALDUS001`+ kodlarıyla `Diagnostics/HaldusDiagnostics.cs` ve `AnalyzerReleases.Unshipped.md`
  dosyalarında birlikte tutulur.

## Kurallar

- Commit'ten önce `dotnet test` çalıştır ve commit'i sonucuna bağla; test çıktısına bakmadan zincirleme
  commit atma. Bekleyen `.received.cs` dosyası varken de commit atma.
- Üretilen kodda ve kütüphanelerde kod İngilizce, yorumlar Türkçe.
- Generator testleri üretilen çıktının hatasız derlendiğini de doğrulamalıdır.
- .NET 11 yayınlandı ama LTS değil; paketler 10.x hattında tutulur.
