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
- Örnek mağazanın istemcisi (`samples/Haldus.Sample.Shop.Client`): `pnpm install`, `pnpm generate:api`,
  `pnpm typecheck`. **Örnek mağazada bir uç veya DTO değiştiğinde (üretilen kod değiştiğinde de) backend derlendikten
  sonra istemci aynı görevde yeniden üretilir ve tip denetimi çalıştırılır;** `openapi.json` ile `src/api` birlikte
  commit'lenir. Aynı kural örnek arayüzlerin kendi istemcileri için de geçerlidir (aşağıda).
- Örnek yönetim paneli (`samples/Haldus.Sample.Shop.Web`): `pnpm install`, `pnpm dev`, `pnpm generate:api`,
  `pnpm typecheck`, `pnpm lint`, `pnpm build`. Paneli çalıştırmak için backend'in de çalışması gerekir (kendi
  README'sinde; `Shop:CreateDatabase` ayarı tabloları açılışta oluşturur). Panel yalnızca kullandığı etiketlerin
  istemcisini üretir (`orval.config.ts`: `filters.tags`); yeni bir kaynak kullanılacaksa etiketi oraya eklenir.
- Örnek mobil uygulama (`samples/Haldus.Sample.Shop.Mobile`): `pnpm install`, `pnpm dev` (Expo), `pnpm generate:api`,
  `pnpm typecheck`. Backend'in adresi `EXPO_PUBLIC_API_URL` ile verilir. Tarayıcıda çalıştırılırken backend'e kaynak
  izni gerekir (`Shop:CorsOrigins`). pnpm burada düz `node_modules` kurar (`pnpm-workspace.yaml`: `nodeLinker: hoisted`);
  Metro, sembolik bağlı düzende dolaylı bağımlılıkları (`react-native-css-interop`) çözemiyor.
- Örnek uygulamada üretilen kod `samples/Haldus.Sample.Shop/obj/Generated` altında incelenebilir.
- Yük ölçümü: `dotnet run -c Release --project tools/Haldus.LoadTest` (çalışan bir örnek mağaza ister; backend
  `Shop:DemoIdentity=true` ile ve Release kipinde başlatılır; ayrıntısı aracın README'sinde). Sonuçlar makineye bağlıdır;
  rapordaki tablo değiştirilecekse ölçüm boş bir veritabanıyla yeniden yapılır.

## Yapı

- `src/Haldus.Abstractions`: bağımlılıksız sözleşmeler (sayfalama tipleri, trait arayüzleri).
- `src/Haldus.Generators`: Roslyn incremental generator (netstandard2.0, Roslyn 5.0.0'a sabit).
- `src/Haldus.EntityFrameworkCore`: model kayıt defteri, interceptor'lar, sorgu filtreleri.
- `src/Haldus.AspNetCore`: `AddHaldus`, `Map*` giriş noktaları, validasyon, hata yanıtları, `.resx`.
- `src/Haldus.Storage.S3`: S3 uyumlu nesne deposu (`IHaldusFileStorage`); AWS SigV4 imzası elle yazıldı, paket taşımaz.
- `src/Haldus.Jobs.InMemory`, `src/Haldus.Jobs.PostgreSql`, `src/Haldus.Jobs.Hangfire`: `MapJob` işlerini
  yürütücüye ulaştıran altyapı paketleri. Çekirdek yalnızca `IHaldusJobDispatcher` arayüzünü bilir.
- `tests/Haldus.Generators.Tests`: generator çıktısı testleri.
- `tests/Haldus.IntegrationTests`: örnek uygulamaya karşı gerçek PostgreSQL testleri.
- `samples/Haldus.Sample.Shop`: örnek uygulama ve modelin sınandığı yer.
- `samples/Haldus.Sample.Shop.Client`: örnek mağazanın `openapi.json` dosyasından orval ile üretilen TypeScript
  istemcisi (tipler + TanStack Query hook'ları, `fetch`). Uygulama değildir; şemanın tüketilebildiğini kanıtlar.
- `samples/Haldus.Sample.Shop.Web`: örnek mağazanın yönetim paneli (Vite + React + TypeScript, shadcn/ui `sidebar-07`
  ve `login-01`, Tailwind). Ürünler, kategoriler ve siparişlerin iş akışı; kendi orval istemcisi `src/api` altındadır.
- `samples/Haldus.Sample.Shop.Mobile`: örnek mağazanın mobil uygulaması (Expo, Expo Router, React Native Reusables,
  NativeWind). Ürünler, favoriler ve kullanıcının sepeti; kendi orval istemcisi `api` altındadır.
- `samples/Haldus.Sample.Modular`: modüler yapının sınandığı küçük uygulama. `Catalog` ve `Sales` kendi
  bağlamına sahip modüllerdir; `Support` modülü ve `Host` ortak bağlamı kullanır.
- `tools/Haldus.LoadTest`: çalışan örnek mağazayı HTTP üzerinden yük altında ölçen konsol aracı (bağımlılığı yok).
  Kilide dayanan özellikleri kilitsiz bir yazmayla yan yana ölçer.
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
  `MapLookup`, `MapDetail`, `MapAssociation`, `MapAction`, `MapQuery`, `MapJob`. Sonradan eklenenler: `MapFile`
  (19. aşama), `MapBalance` (25. aşama).
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
- Sahiplik (kullanıcı kararı): `.OwnedByCurrentUser()` kaydı onu oluşturan kullanıcıya bağlar ve kaynağı
  varsayılan olarak `/me` altına alır (`/me/carts`); `underMe: false` adresi değiştirmez (`/notes`). İkisi de
  desteklenir. `OwnerId` alanı üretilir, istekte verilemez, yanıtta yer almaz ve sonradan değişmez; başkasının
  kaydı 404, kullanıcısı çözülemeyen istek 401 alır.
- Geçerli kullanıcıya bağlı bağ adlandırılarak yazılır (kullanıcı kararı):
  `app.MapAssociation<Favorite, Product>().ForCurrentUser()`. İlk tip argümanı bir uç değil, üretilecek bağ
  entity'sinin adıdır. Uçlar `/me/favorites` altındadır: `PUT` / `DELETE` / `GET /{productId}` ve bağlı
  kayıtları (bağları değil) döndüren `GET /`.
- Alan eklentileri (12. aşama, kullanıcı kararları):
  - Yalnızca oluştururken yazılan alan zincirde sayılır: `.CreateOnly(coupon => coupon.Code, ...)`. Alan `POST`
    isteğinde yer alır, `PUT` ve `PATCH` isteklerinde yer almaz.
  - `.NaturalKey(coupon => coupon.Code)` alanı benzersiz yapar (çakışma 409) ve ek bir okuma ucu üretir:
    `GET /coupons/by-code/{code}`. Diğer uçlar `Id` ile kalır; doğal anahtar adreste `Id`'nin yerine geçmez.
  - Liste ucunda sıralama `?sort=-createdAt,name` biçimindedir (eksi azalan). Sıralanabilir alanlar
    `.Sortable(...)` ile sayılır; sayılmayan alan 400 döner.
  - `.Metadata()` değerleri metin olan bir anahtar-değer çantasıdır (jsonb). `PATCH` birleştirir (gönderilen
    anahtar eklenir veya değişir, `null` gönderilen silinir), `PUT` tamamını değiştirir. Sınır: 50 anahtar, anahtar
    40, değer 500 karakter.
  - `KeyStrategy` için kullanıcı tercih belirtmedi ("No preference"); önerilen önekli kimlik (`ord_…`) ve rastgele
    GUID (v4) yazıldı. Planlardan sonra ULID (`KeyStrategy.Ulid`; 26 karakterlik metin) ve istemcinin verdiği kimlik
    (`KeyStrategy.Client`) eklendi. İstemcinin kimliği, AIP-133'teki gibi, gövdede değil sorguda gelir
    (`POST /enrollments?id={guid}`) ve bir `Guid`'dir; aynı kimlikle ikinci oluşturma 409 alır.
- Kayıt işlemleri (13. aşama, kullanıcı kararları):
  - `.Cloneable()`: `POST /{id}/clone`. Kopyaya yazılabilir alanlar ve gömülü satırlar taşınır; geri kalanı yeni
    kayıt gibi atanır (yeni kimlik, başlangıç durumu, numara kendi kuralıyla, zaman damgaları, sahip). İstek
    `PATCH` biçiminde, isteğe bağlı bir gövde alır: gönderilen alanlar kopyada kaynaktakinin yerine geçer.
  - `.Orderable()`: sıra tam sayı `Position` alanıyla tutulur (0'dan başlar, boşluksuzdur). Taşıma
    `POST /{id}/move` ile, gövdede `{ "position": 2 }` verilerek istenir; aynı kapsamdaki kayıtlar tek
    transaction'da yeniden numaralanır. Araya eklenen sıra anahtarı (rank) reddedildi.
  - `.Previewable()`: isteği doğrulayıp hesaplanan alanlarla sonucu döndürür ama kaydetmez. Hem oluşturma
    (`POST /preview`, oluşturma gövdesi) hem güncelleme (`POST /{id}/preview`, güncelleme gövdesi) için üretilir.
- Kayıtlar arası zincir (14. aşama). Kullanıcı dört soruda da "önerilerinle devam et" dedi; yazılan, sorulan
  seçeneklerin önerilenleridir:
  - İçerik: Haldus kopyalar, sözleşme düzeltir. Aynı tipteki karşı kayıt (ters kayıt, yerine geçen) kaynağın
    kopyası olarak doğar; entity sözleşmeyi (`IReversal<T>`, `ISupersession<T>`) uygularsa kopyadan sonra çağrılır.
    Farklı tipten üretilen kayıtta aynı adlı alanlar kopyalanır, gerisini `ICreatedFrom<TSource>` doldurur.
  - `.CreatedFrom<TSource>()` ucu hedef koleksiyondadır: `POST /orders/from-cart/{cartId}`.
  - `.Supersedes()`: `POST /{id}/supersede`. Eski kayıt kalır, `SupersededById` ile işaretlenir, artık
    değiştirilemez (409) ve listede görünmeye devam eder.
  - `.DerivedFrom<TSource>()`: her kaynak kayıt oluşturulduğunda, aynı transaction'da kullanıcının
    `IDerivation<TSource, TTarget>` sınıfı çağrılır; hedef salt-okunurdur. Baştan yeniden kurma üretilmez.
- Sırlar (15. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca onaylanmadı:
  - `.SecretOnce(prefix: "sk")`: sır sunucuda üretilir (256 bit, `önek_…`), yalnızca oluşturma ve döndürme yanıtında
    görünür, listede son dört karakteri (`SecretHint`) gösterilir. Varsayılan saklama SHA-256 özetidir;
    `recoverable: true` ile sır ayrıca ASP.NET Data Protection ile şifrelenir (webhook imza sırrı gibi, sunucunun
    kendisinin kullanması gereken sırlar için).
  - `.Rotatable()`: `POST /{id}/rotate` yeni sır üretir; eski sır hemen geçersiz olur. `overlap:` verilirse eski sır
    o süre boyunca geçerli kalır.
  - `.TokenAccess(prefix: "inv", "accept")`: kayıt, oturumu olmayan biri tarafından gizli bir token ile okunur
    (`GET /by-token`) ve yalnızca sayılan geçişler/aksiyonlar çalıştırılır (`POST /by-token/accept`). Token adreste
    değil `X-Access-Token` başlığında taşınır.
  - `.TokenOnly()` (üçüncü plandan sonra, güvenlik incelemesi üzerine eklendi; ayrıca onaylanmadı): kayda yalnızca
    token ile ulaşılır. `TokenAccess` tek başına kimlikli adresleri de bırakır (davet: yönetici kimlikle iptal eder,
    davetli token ile kabul eder); sahibi bir kullanıcı olmayan kayıtta (misafir sepeti) bu, kimliği bilen herkese
    yazma izni demekti.
- Bağlar ve kapsam (16. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca onaylanmadı:
  - Bağların topluca değiştirilmesi: `PUT /products/{id}/tags` gövdesinde `{ "ids": [...] }` alır ve o ucun
    bağlarını listeyle aynı yapar (iki yönden de). Yalnızca yükü ve durumu olmayan bağlarda üretilir.
  - Yük taşıyan bağ: kullanıcı bağ sınıfını (`{Sol}{Sağ}`, sol ucun namespace'inde) kendisi yazarsa skaler alanları
    bağın yüküdür. `PUT` çifti gövdede yükü alır ve bağı döndürür; bağlar yükleriyle `…/links` altında listelenir.
  - Durumu olan bağ: `app.MapAssociation<Organization, User>().Workflow<MembershipStatus>()` ve builder'ında
    gövdesiz `MapTransition("activate", from, to)`. Geçişin iş mantığı (lambda, handler, istek tipi) yoktur.
  - Ebeveyn altında tek kayıt: `app.MapSingleton<SupplierTerms>().Under<Supplier>()`
    (`/suppliers/{supplierId}/supplier-terms`); anahtarı ebeveynin anahtarıdır.
  - Kısa adres: `.Under<T>(shallow: true)` kaynağı kendi adresinde bırakır (`/supplier-contacts/{id}`); ebeveyn
    oluşturma isteğinde gelir, sonradan değişmez, liste `?supplierId=` ile süzülür. Rails'teki gibi iç içe
    koleksiyon adresi ayrıca üretilmez.
  - Detayın komutla yazılan modu: `orders.MapDetail<OrderItem>().AsCommands()`; satırlar yanıtta ve oluşturma
    isteğinde gömülü kalır, `PUT`/`PATCH` isteklerinde taşınmaz ve `POST /{id}/order-items`,
    `PUT` / `DELETE /{id}/order-items/{rowId}` ile tek tek yazılır.
  - Raporun v1 listesindeki "uzatma kancaları" yazılmadı: raporda tanımı yok ve kullanıcı istemedi.
- Onay ve belge (17. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca onaylanmadı:
  - Onay tek adımdır; `.Approvable(levels: n)` ile birbirinden farklı n kişinin sırayla onayı istenir. Planlardan
    sonra onay zinciri eklendi: `.ApprovalRoles("manager", "finance")` düzeyleri rollere bağlar (rol sayısı düzey
    sayısına eşit olmalıdır) ve entity `IApprovalLevels` uygularsa kaç düzey gerektiğini kayıt söyler (koşullu adım).
    Roller `ICurrentUserRoles` servisine sorulur; varsayılanı rol claim'leridir.
  - `POST /{id}/approve`, `POST /{id}/reject` (isteğe bağlı `{ "note" }`), `GET /{id}/approvals`. Kaydın içeriğini
    şimdiye kadar yazmış hiç kimse (`Contributors`; yalnızca son yazan değil) karar veremez (403); aynı kişi iki
    düzeyi onaylayamaz (403); herhangi bir düzeydeki ret kaydı reddeder.
  - İçerik hangi yoldan değişirse değişsin (`PUT`, `PATCH`, satır komutu, toplu güncelleme, aksiyon, uygulamanın
    kendi kodu) toplanan onaylar geçersiz olur ve kayıt yeniden karar bekler. Onaylanmış kayıt güncelleme
    uçlarından değiştirilemez (409), reddedilmiş kayıt düzenlenebilir. Hiçbir şeyi değiştirmeyen yazma içerik
    yazmak sayılmaz.
  - `.Approvable(levels, "order", ...)` sayılan geçişleri ve aksiyonları kayıt onaylanana kadar durdurur (409);
    onayın izin verdiği işlemin kendi yazdıkları onayı düşürmez.
  - `.Document()` durumları ve kuralları sabit bir durum makinesidir (`DocumentStatus`: `Draft`, `Submitted`,
    `Cancelled`; yalnızca taslak düzenlenir ve silinir). `submit` ve `cancel` geçişleri kendiliğinden gelir; aynı
    adla yazılan `MapTransition` onların yerine geçer. `POST /{id}/amend` iptal edilmiş belgeden, ona işaret eden
    (`AmendedFromId`) yeni bir taslak üretir; entity `IAmendment<T>` uygularsa kopyadan sonra çağrılır.
- Zaman ve sürüm (18. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca onaylanmadı:
  - `.Revisioned()` anlık görüntü saklar (fark günlüğü değil): kaydın her yazmadan sonraki hali, gömülü satırlarıyla
    birlikte. `GET /{id}/revisions`, `GET /{id}/revisions/{revisionId}` (kaydın kendi yanıt biçiminde),
    `POST /{id}/revisions/{revisionId}/rollback` (bir güncellemedir; yeni bir sürüm oluşturur).
  - `.Publishable()` çift kopyadır (sürüm tablosu değil): yayındaki hal aynı tabloda, taslağına işaret eden ayrı
    bir satırdır; böylece yayındaki kayıtlar da süzülür ve sayfalanır. `POST /{id}/publish`, `POST /{id}/unpublish`,
    `GET /published`, `GET /published/{id}`. Yayındaki kayıt silinemez (409).
  - `.EffectiveDated(per...)`: `EffectiveFrom` (dahil) ve `EffectiveTo` (hariç; boşsa açık uçlu) istemcinin yazdığı
    alanlardır. Kapsamda çakışan dönem 409 alır; açık uçlu tek bir dönemin içinde başlayan yeni kayıt öncekini
    kendi başladığı gün kapatır. Liste `?asOf=` ile süzülür.
  - `.ChangeFeed()`: `GET /changes?since={imleç}`; imleç günlükteki sıra numarasıdır. Yanıt değişen kayıtların
    şimdiki halini ve artık bulunamayanları (`deleted: true`) verir.
- Dosyalar ve takılabilirler (19. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca
  onaylanmadı:
  - `MapFile<T>(maxBytes, türler...)` içeriği ayrıca yüklenen bir `MapCrud` kaynağıdır: `POST` kaydı oluşturur,
    `PUT /{id}/content` içeriği yükler, `GET /{id}/content` indirir. Planlardan sonra iki şey eklendi: yükleme ucu
    bir multipart formun dosya parçasını da kabul eder, ve `.SignedLinks()` içeriğe süreli, oturumsuz açılan
    bağlantılar üretir. Devam ettirilebilir (parça parça) yükleme ve nesne deposuna doğrudan yükleme yazılmadı.
  - Depolama yeni bağımlılık eklemeden yazıldı: varsayılan veritabanı (`HaldusFileContents`), isteğe bağlı dosya
    sistemi (`AddHaldusFileSystemStorage`). Nesne depoları (S3 vb.) `IHaldusFileStorage` uygulanarak eklenir; Haldus
    onlar için paket taşımaz.
  - `.AttachedTo<T>()` her hedef için isteğe bağlı gerçek bir yabancı anahtar üretir ("tip + kimlik" çifti değil)
    ve kaynağı her hedefin adresi altında ayrı ayrı sunar. `Attachable<TFile>()` ve `Commentable<TComment>()` aynı
    bildirimin hedef tarafında yazılan biçimidir; `Taggable<TTag>()` bir `MapAssociation`'dır.
  - Yükleme bekleyen dosya kayıtları kendiliğinden silinmez (süresi dolan kayıtlardaki gerekçeyle).
- Özel alanlar ve aktarma (20. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca
  onaylanmadı:
  - `.CustomFields()`: tanımlar kaynak tipi ve kiracı başınadır ve kaynağın adresinde yönetilir
    (`GET /custom-fields`, `PUT` / `DELETE /custom-fields/{key}`); türler metin, sayı, evet/hayır, gün ve seçenektir.
    Değerler kayıttaki `customFields` çantasında (jsonb) durur ve her yazmada tanımlara göre doğrulanır; tanımı
    kaldırılan alanın değeri kaydın bir sonraki yazmasında düşer.
  - Aktarma biçimleri CSV (varsayılan) ve JSON'dur; CSV yeni bir bağımlılık eklenmeden, elle yazıldı.
  - `MapJob<T>().Exports<TResource>()`: dosya içerik deposuna yazılır ve işten indirilir.
  - `MapJob<T>().Imports<TResource>()`: içerik isteğin gövdesinde gelir (en çok 5 milyon karakter, 10 000 satır)
    ve atomiktir: bir satır bile reddedilirse hiçbiri oluşturulmaz.
- Saklama, okundu takibi ve toplama (22. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve
  ayrıca onaylanmadı:
  - `.Retention(süre)`: yaş `CreatedAt` üzerinden ölçülür (`Auditable` kendiliğinden eklenir) ve süresi geçen kayıtlar
    bakım sırasında fiziksel olarak silinir. Süreyi geliştirici açıkça yazdığı için, `Expiring` kayıtlarının aksine
    ayrıca bir seçenekle açılmaz.
  - `.ReadTracking()` okundu bilgisini kaydın kendisinde tutar (`ReadAt`), bu yüzden `OwnedByCurrentUser` ister.
    Paylaşılan bir kaydın kullanıcı başına okundu bilgisi planlardan sonra ayrı bir bildirim olarak yazıldı:
    `.ReadReceipts()` işaretleri ayrı bir tabloda (`HaldusReadReceipts`) tutar.
  - `.Counts<TChild>()` / `.Sums<TChild>(...)`: alan artırılıp azaltılmaz, alt kayıt her yazıldığında baştan sayılır.
    Türetilmiş durum ayrı bir bildirim değildir: toplamdan sonra üst kaydın `Compute()` metodu çağrılır.
- Yerelleştirilmiş alanlar (23. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca
  onaylanmadı:
  - Araştırmanın açık bıraktığı iki depolama modelinden alan-dil haritası seçildi: çeviriler kayıttaki bir jsonb
    çantadadır (dil → alan → metin), yan tablo yoktur. Alanın kendisi kaynak dildeki metni taşır.
  - `.Translatable(item => item.Title, ...)`; `PUT` / `DELETE /{id}/translations/{locale}`. Çeviriler oluşturma ve
    güncelleme isteklerinde taşınmaz.
  - Okuma ancak `?locale=` verilirse çevrilir; `Accept-Language` veriyi değiştirmez (okuduğunu geri yazan istemci
    kaynak metni ezmesin diye). Yanıt çevirilerin tamamını her zaman `translations` altında taşır.
- Durum eksenleri (24. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca onaylanmadı:
  - "En fazla bir durum modeli" kuralı korunur: asıl durum yine tektir (`Workflow`, `Document`...). Ek eksenler
    `.Axis<TStatus>()` ile bildirilir, alanı durum tipinin adını alır (`PaymentState`) ve yalnızca geçişlerle değişir.
  - Geçişin ekseni ayrıca yazılmaz: durumlarının enum tipinden bulunur. `EditableIn`, `DeletableIn`,
    `Numbered(assignOn:)` ve zincir işlemlerinin durum listeleri yalnızca asıl duruma bakar.
  - Başka kayıtlardan türeyen durum bir eksen değildir; 22. aşamadaki toplanan alan + `Compute()` ile yazılır.
- Bakiye (25. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca onaylanmadı:
  - `MapBalance<T>()` raporun uçlarını üretir (`adjust`, `set`, `entries`) ve raporda eksik sayılan iki şeyi ekler:
    adlandırılmış kovalar (`.Buckets("available", "reserved")`, her biri ayrı bir `decimal` alan) ve kovalar arası
    `move`.
  - Anahtarı istemci verir (`Key`, metin); hiç yazılmamış anahtarın bakiyesi sıfırdır ve ilk yazma kaydı oluşturur,
    ama okuması 404'tür.
  - Bakiye varsayılan olarak sıfırın altına düşemez (409); `.AllowNegative()` kuralı kaldırır.
  - Defter kaynak başına ayrı tablo değil, tek bir `HaldusBalanceEntries` tablosudur.
- Değişiklik kümesi (26. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca onaylanmadı:
  - Araştırmanın `MapChangeSet<TTarget>()` archetype'ı ayrı bir `Map*` çağrısı olarak değil, hedefin zincirinde
    `.ChangeSets(durumlar...)` olarak yazıldı: küme kendi başına bir kaynak değil, hedefin bir yazma yoludur ve
    kurallarını hedefin kısmi güncellemesinden alır.
  - Küme "aksiyon listesi" değil, hedefin birikmiş `PATCH` gövdesidir (JSON olarak saklanır). Sahneleme
    `PATCH /{id}/edits/{editId}` ile yapılır; uçlar hedefin adresi altındadır (`/orders/{id}/edits`), raporun kök
    adresi (`/edits/{id}`) kullanılmadı ki kümenin görünürlüğü hedefinkiyle aynı olsun.
  - Küme, açıldığı andaki değil uygulandığı andaki kayda uygulanır; "kayıt bu sırada değişti" çakışması üretilmez
    (sürüm kontrollü kaynakta `If-Match` zaten bunu sorar).
- Hassas alan, ölçüte göre silme, yeniden çalıştırma (27. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim
  seçeneklerdir ve ayrıca onaylanmadı:
  - `.Sensitive(alanlar...)` alanı yalnızca yazılan bir alan yapar; değer veritabanında düz saklanır. Planlardan sonra
    `.Encrypted(alanlar...)` eklendi: alan ayrıca veritabanında şifreli saklanır (ASP.NET Data Protection; yeni
    bağımlılık yok). Şifreli alan her zaman yalnızca yazılır; maskeleyerek gösterme (son dört karakter) yazılmadı.
  - `.Purgeable()` (AIP-165): `POST /purge`, ölçüt kaynağın `Filterable` filtresidir, `force` verilmedikçe yalnızca
    sayar. Eşzamanlı (arka plan işi olmayan) ve istek başına 1000 kayıtla sınırlıdır.
  - `MapJob().Rerunnable()`: AIP-152'nin "iş tanımı + çalıştırmalar" modeli yazılmadı; bizde iş kaydı zaten bir
    çalıştırmadır, yeniden çalıştırma girdileri kopyalanmış yeni bir iş kaydıdır.
- Olay yayını (28. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca onaylanmadı:
  - `.PublishesEvents()` bir "outbox"tur: olay kayıtla aynı kaydetmede `HaldusOutbox` tablosuna yazılır ve commit'ten
    sonra uygulamanın kaydettiği `IHaldusEventPublisher` servislerine teslim edilir. Haldus bir mesaj kuyruğu paketi
    taşımaz (yeni bağımlılık yok); kuyruğa, webhook'a ya da başka bir modüle taşımak yayıncının işidir.
  - Olay türleri: `{kaynak}.created` / `.updated` / `.deleted` ve geçiş başına `{kaynak}.{geçiş}` (geçişin adı
    olduğu gibi; geçmiş zamana çevrilmez). Veri, kaydın yanıt biçimidir ("kalın" olay).
  - Teslimat en az bir kez ve tek sıralıdır: başarısız olay arkasındakileri bekletir, on denemeden sonra bırakılır.
- Tip, rol, genelleme (29. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca onaylanmadı:
  - `.TypedBy<TType>()`: varsayılanlar okurken değil **yazarken** devralınır (kayıtta saklanır). Okurken devralma her
    listeyi tipe bağlardı; karşılığında tipin varsayılanı sonradan değişince var olan kayıtlar eski değerde kalır.
    "Tip örneği kısıtlar" yalnızca şu kadarıyla yazıldı: tip zorunludur, değişmez ve kayıtları varken silinemez.
  - `.RoleOf<TPlayer>()`: rol, oyuncuya zorunlu ve benzersiz bir referanstır; oyuncu rolün doğal anahtarı olur.
  - `.SubtypeOf<TBase>()`: üç kalıtım eşlemesinden tek tablo (TPH) seçildi ve yalnızca tek düzey desteklenir. Alt
    tipin entity sınıfı üst tipten gerçekten türer; ayırt edici alan `Kind`'dır (kaynağın kebab-case adı).
- Kalan satırlar (30. aşama). Kullanıcı sormamamı istedi; yazılan, önerdiğim seçeneklerdir ve ayrıca onaylanmadı:
  - Tekrar serisi araştırmada kapsam dışıydı; satır kapansın diye dar bir biçimiyle yazıldı. `.Recurring()` RRULE
    değildir: sıklık (günlük / haftalık / aylık / yıllık), aralık ve bitiş (zaman veya sayı). Planlardan sonra
    haftanın günleri ve ay içindeki sıra (`RecurrenceByDay`: iCalendar'ın `BYDAY` yazımı, `MO,WE,FR`, `2TU`, `-1FR`)
    ile saat dilimi (`RecurrenceTimeZone`: IANA adı; oluşlar o dilimin duvar saatini korur) eklendi. Hâlâ `RRULE`'un
    tamamı değildir: `BYMONTHDAY`, `BYSETPOS`, `BYMONTH`, `WKST`, `EXDATE` / `RDATE` yoktur.
  - Kombinatoryal varyant üretimi ve sepet birleştirme için yeni bir bildirim yazılmadı: araştırma bunları "tek seferlik,
    partial metotla yazılır" saymıştı. İkisi de entity aksiyonuyla ifade edilir; bunun için aksiyon ve geçişlerin
    eklediği gömülü satırlara kimlik ve sıra verilmesi sağlandı. Misafir sepeti `TokenAccess` + `TokenOnly` ile ifade edilir;
    ikisi de örnek mağazada çalışır (`/me/baskets`, `/guest-baskets`, `POST /me/baskets/{id}/merge`).
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
- Durum makinesi zincirde bildirilir: `.Workflow<OrderStatus>(initial: ...)`, `.EditableIn(...)`,
  `.DeletableIn(...)`. `Status` alanı üretilir, istek gövdesinden yazılamaz ve yalnızca geçişlerle değişir.
- Geçişler sahibin builder'ından bildirilir: `orders.MapTransition<CancelOrder>(from: ..., to: ...)` ya da
  gövdesiz `orders.MapTransition("ship", from: ..., to: ...)`. Geçişin iş mantığı üç yoldan biriyle verilir
  ve üçü de desteklenir (kullanıcı kararı): istek tipi `ITransition<TEntity>` uygular, bir
  `ITransitionHandler<TEntity, TRequest>` sınıfı yazılır (Haldus bulur ve kaydeder) veya çağrıya lambda
  verilir. Sıra: önce isteğin `Apply`'ı, sonra lambda, lambda yoksa kayıtlı handler.
- `Numbered` sayacı varsayılan olarak boşluksuzdur: numara kayıtla aynı transaction'da, kapsam başına
  (kaynak, kiracı, desendeki tarih parçaları) kilitlenen bir sayaç satırından alınır. `gapless: false` ile
  veritabanı sequence'ine geçilir; ikisi de desteklenir (kullanıcı kararı).
- `MapJob` altyapısı paketlerle seçilir ve üçü de desteklenir (kullanıcı kararı): bellek içi kuyruk
  (`AddHaldusInMemoryJobs()`), kendi PostgreSQL kuyruğumuz (`AddHaldusPostgreSqlJobs<TContext>()`) ve
  Hangfire (`AddHaldusHangfireJobs()`).
- `Batch` işlem başına ayrı uçlarla sunulur (`POST /products/batch-get`, `batch-create`, `batch-update`,
  `batch-delete`) ve varsayılan olarak atomiktir; `.Batch(atomic: false)` ile kısmi başarıya (207, öğe
  başına sonuç) geçilir. İkisi de desteklenir (kullanıcı kararı).
- `Expiring` süresi dolan kaydın erişimini keser (kullanıcı kararı): kayıt silinmez, sorgu filtresiyle
  okuma anında gizlenir ve yazılamaz. Fiziksel temizlik arka plan işleriyle birlikte 8. aşamadadır.
- `MultiTenant` kiracıyı token'daki claim'den okur.

### Proje tipleri

- Tek projeli ve modüler (her özellik ayrı sınıf kütüphanesi) yapı birlikte desteklenir; tek proje,
  tek modüllü haldir.
- Veritabanı bağlamı proje başına seçilir: ortak tek `DbContext` (`AddHaldus<TContext>()`) veya modül
  başına ayrı (`AddHaldusModule<TContext>()`).
- Bu yüzden üretilen kod somut `DbContext` tipine bağlanmaz; `IHaldusDbContextProvider` üzerinden
  çalışır ve model katkıları modül (assembly) anahtarıyla kaydedilir.
- İki mod aynı uygulamada birlikte kullanılabilir: `AddHaldus<TContext>()` ortak bağlamı,
  `AddHaldusModule<TContext>()` o bağlamın bulunduğu assembly'nin kendi bağlamını bildirir. Modül kendi
  generator'ını çalıştırır (sınıf kütüphanesi generator'a analyzer olarak başvurur).
- Modüllerin ayrıca kaydedilmesi gerekmez: bağlamın assembly'sinden başvurularla ulaşılan, Haldus'un çalışma
  zamanına başvuran her assembly modül sayılır ve yüklenir.
- Modüller arası ilişkiler: `.References<T>()` başka modülün entity'sine bağlanabilir; iki modül aynı
  bağlamdaysa gerçek bir yabancı anahtar kısıtı olur, ayrı bağlamlardaysa yalnızca kimlikle tutulur ve
  varlığı yazarken hedefin bağlamında doğrulanır (silme davranışı uygulanamaz). `.Under<T>()` başka modülün
  kaynağını ebeveyn alabilir, ama ikisi aynı bağlamda olmalıdır; değilse uygulama model kurulurken durur.
  `MapAssociation`'ın iki ucu da aynı modülde eşlenmiş olmalıdır.

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

Üç plan da bitti: ilk sürümün on aşaması (3. aşamaya `.Searchable()`, 8. aşamaya fiziksel temizlik de eklendi),
ikinci planın on bir aşaması ve üçüncü planın on bir aşaması (22–32). Açık bir aşama yoktur; yeni bir iş yeni bir plandır
ve tasarım soruları yeniden sorulur. Sınamanın sonucu `reports/Haldus taksonomi sınaması.md`
dosyasındadır: araştırmadaki 59 varlığın 59'u da temiz ifade ediliyor (ilk
sınamada 19 / 29 / 11). Aynı veri `tests/Haldus.Generators.Tests/TaxonomyTests.cs` içinde derlenir; yeni bir archetype
veya trait bir satırı kapatıyorsa ikisini birlikte güncelle. Tabloda eksik satır kalmadı; bilerek
yazılmayanlar raporun "Bilinen sınırlar" tablosundadır.

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

### İkinci plan

Kullanıcı 2026-11-24'te onayladı. Kapsamı araştırma raporunun v2 listesi, ilk sürümden kalan küçük kalemler
ve sınamada çıkan küçük eksiklerdir. Raporun v3 listesi (`MapBalance`, `MapChangeSet`, birden çok durum
ekseni, çeviri, saklama süresi, okundu takibi) ve tekrar serisi bu planın dışındadır. Her aşama testleriyle
birlikte ayrı commit oldu; 11–21. aşamaların tamamı bitti.

11. Sahiplik: `OwnedByCurrentUser`, `MapAssociation().ForCurrentUser()`
12. Alan eklentileri: `Metadata`, yalnızca oluştururken yazılan alan, `NaturalKey`, `KeyStrategy`, liste
    ucunda sıralama
13. Kayıt işlemleri: `Cloneable`, `Orderable`, `Previewable`
14. Kayıtlar arası zincir: `Reversible`, `.Supersedes()`, `.CreatedFrom<T>()` ve geçişte başka kayıt
    oluşturma, `.DerivedFrom<T>()`
15. Sırlar: `SecretOnce`, `Rotatable`, `TokenAccess`
16. Bağlar ve kapsam: yük taşıyan ve durumu olan bağ, bağların topluca değiştirilmesi, ebeveyn altında tek
    kayıt, `.Under()` kısa adresi, detayın komutla yazılan modu
17. Onay ve belge: `Approvable`, `.Document()`
18. Zaman ve sürüm: `Revisioned`, `.Publishable()`, `EffectiveDated`, `ChangeFeed`
19. Dosyalar ve takılabilirler: `MapFile`, `.AttachedTo()`, `Attachable`, `Commentable`, `Taggable`
20. Özel alanlar ve aktarma: kiracının tanımladığı `CustomFields`, `MapJob` üzerinde içe/dışa aktarma
21. Sınamanın yinelenmesi: 59 satırlık tablo, rapor ve README

**Kullanıcı 2026-11-24'te kalan aşamalar için soru sorulmamasını istedi** ("projenin tamamen bitmesini istiyorum,
bana soru sorma"). 15–20. aşamaların tasarım kararları bu yüzden sorulmadı: önerilen seçenek uygulandı ve "Tasarım
kararları"na, kullanıcı ayrıca onaylamadı notuyla yazıldı. Kullanıcı bu kararlardan birini değiştirmek isterse ilgili
madde oradadır. Yeni bağımlılık gerektiren hiçbir yol seçilmedi; depolama ve aktarma yeni paket eklemeden yazıldı.
Bu talimat projenin bitirilmesi içindir ve üçüncü planı da kapsar (aşağıda); proje bittikten sonra açılan yeni bir işte
tasarım soruları yeniden sorulur.

Raporun v1 listesindeki "uzatma kancaları" plana girmedi ve yazılmadı: raporda tanımı yok.

### Üçüncü plan

İkinci plan bitince iş bitmiş sayılmıştı; oturumun hedefi ("projenin tamamen bitmesini istiyorum, bana soru sorma")
bunu kabul etmedi: araştırmanın üçüncü sürüm listesi, sınamada kalan satırlar ve açık duran frontend/mobil kapsamı
da "tamamen"in parçasıdır. Bu planı kullanıcı ayrıca onaylamadı; kapsamı o hedeften çıkarıldı ve kararları, önceki
aşamalardaki gibi, sorulmadan önerilen seçenekle verildi. Her aşama testleriyle birlikte ayrı commit oldu; 22–32.
aşamaların tamamı bitti.

22. Saklama, okundu takibi, toplama: `Retention`, `ReadTracking`, `Counts` / `Sums` (türetilmiş durum dahil)
23. Yerelleştirilmiş alanlar: `Translatable`
24. Birden çok durum ekseni: `Axis`
25. `MapBalance`
26. `MapChangeSet`
27. `Sensitive` alan, `Purgeable`, yeniden çalıştırılabilir `MapJob`
28. `PublishesEvents`
29. `TypedBy`, `RoleOf`, `SubtypeOf`
30. Kalan satırlar: sepet, varyant üretimi, tekrar serisi
31. Frontend ve mobil kapsamı
32. Sınamanın yinelenmesi: tablo, rapor ve README

27–29. aşamalar plana 24. aşamada eklendi: raporun üçüncü sürüm satırı, daha önce bu dosyada sayılanlardan fazlasını
içeriyor (ilişki bildirimleri, `Sensitive`, `PublishesEvents`, `Purgeable`, yeniden çalıştırılabilir iş).

### Planlardan sonra

Kullanıcı üç plan bittikten sonra hedefi aynı sözlerle yeniden kurdu ("projenin tamamen bitmesini istiyorum bana soru
sorma"). Son raporda "yazılmadı" veya "yalnızca derleniyor" denen kalemler bunun üzerine kapatıldı; bunlar bir plan
aşaması değildir ve kararları yine sorulmadan verildi:

- Araştırmadaki sepet satırının çalışan hali: örnek mağazada `/me/baskets`, `/guest-baskets` ve birleştirme aksiyonu.
- `TokenOnly` (güvenlik incelemesinin bulgusu üzerine; "Tasarım kararları"nda ve "Sırlar" altında).
- Örnek yönetim paneli (`samples/Haldus.Sample.Shop.Web`) ve mobil uygulama (`samples/Haldus.Sample.Shop.Mobile`);
  seçimleri "Açık kararlar"dadır.

- Raporun "Bilinen sınırlar" tablosundan yeni bağımlılık gerektirmeyenler: tekrar serisinde haftanın günleri ve saat
  dilimi, `Encrypted`, onay zinciri (`ApprovalRoles`, `IApprovalLevels`), `ReadReceipts`, `ScheduledPublishing`,
  `KeyStrategy.Ulid` / `Client`, bakiyede `Holds`, dosyada multipart yükleme ve `SignedLinks`.
- Örnekte gerçek kimlik doğrulama (aşağıda, "Açık kararlar").
- Yük ölçümü: `tools/Haldus.LoadTest` ve raporun "Yük altında ölçüm" bölümü. Örnek mağaza Release kipinde, yerel
  bir PostgreSQL kapsayıcısına karşı ölçüldü; sayılar o makinenin göreli kıyasıdır, bir kapasite vaadi değildir.
  Ölçümün gösterdikleri: aynı kilidi bekleyen yazmalar sıraya girer (o ortamda ~150 işlem/sn; tavanı commit süresi
  belirler); `MapBalance` ve toplanan alanlar anahtar / üst kayıt başına kilitlendiği için farklı anahtarlarda
  ölçeklenir; `DerivedFrom` (kaynak tipi başına tek kilit) ve `Orderable` (ağaç başına tek kilit) en dar boğazlardır.
  Ölçüm boşluksuz `Numbered`'da bir kusur da gösterdi: sayacın satır kilidini bekleyenler sırayla uyanmıyordu
  (32 istemcide p99 1,5 sn). `HaldusNumbering.AllocateAsync` artık önce kapsamın danışma kilidini alıyor
  (`HaldusOrdering.LockAsync`); p99 239 ms'ye indi, verim değişmedi.

- Kimlik doğrulamanın eksikleri: sunucuda oturum ve çıkış, parola sıfırlama, e-posta doğrulama, hesap kilidi, hız sınırı.
- S3 uyumlu dosya deposu (`src/Haldus.Storage.S3`; `AddHaldusS3Storage`) ve webhook olay yayıncısı
  (`AddHaldusWebhookPublisher`); ikisi de yeni bağımlılık eklemeden yazıldı (aşağıda, "Generator'ın işleyişi").
- Mobil uygulama Android'de denendi (emülatör, Android 17, Expo Go). Önceki "bu makinede emülatör yok" tespiti
  yanlıştı: SDK ve sanal cihazlar kuruluydu, yalnızca `PATH`'te değildiler (`%LOCALAPPDATA%\Android\Sdk`).

Yapılmayanlar ve nedenleri:

- Arayüzler için otomatik test: test çerçevesinin kurulumu (`pnpm add -D vitest …`) izin sistemi tarafından
  reddedildi; genel kural da test altyapısının kendiliğinden kurulmamasını söylüyor. Kullanıcı isterse kurulur.
- Yeni bağımlılık gerektirenler: mesaj kuyruğu istemcisi (ör. RabbitMQ), kimlik sağlayıcı için JWT doğrulaması
  (`Microsoft.AspNetCore.Authentication.JwtBearer`). Aynı gerekçeyle eklenmediler; onay gerekir.
- iOS: Windows'ta iOS simülatörü çalışmaz; uygulama iOS'ta denenmedi.
- Raporun "Bilinen sınırlar" tablosunda kalan satırlar bilerek dar bırakılanlardır.
## Açık kararlar

- **Frontend ve mobil desteğinin kapsamı kapandı (31. aşama; kullanıcı sormamamı istedi, ayrıca onaylanmadı):** Haldus
  backend'de kalır; frontend ve mobil için kendi kod üretimi yoktur. O tarafa verilen şey sözleşmedir (`openapi.json`)
  ve onu tüketmenin yolu kullanıcının genel kurallarındaki yoldur: orval, TanStack Query hook'ları, `fetch`. Depoda
  bunun çalışan örneği vardır (`samples/Haldus.Sample.Shop.Client`) ve şemanın orval'dan hatasız geçtiği `pnpm typecheck`
  ile doğrulanır.
  - Örnek arayüzler sonradan yazıldı (kullanıcı hedefi 2026-11-24'te yeniden kurdu: "projenin tamamen bitmesini
    istiyorum bana soru sorma"). Kullanıcının genel kurallarına göre sorulması gereken seçimler sorulamadığı için
    önerdiğim seçenekle verildi ve **ayrıca onaylanmadı**; yeniden sorma, ama kullanıcı değiştirmek isterse yer burasıdır:
    - Web'de yönlendirme: **React Router** (`react-router`; `BrowserRouter` + `Routes`).
    - i18n kütüphanesi: **i18next + react-i18next**; diller backend'deki gibi en (varsayılan) ve tr. Çeviri anahtarları
      derleme zamanında denetlenir (`src/i18n/i18next.d.ts`).
    - Form/validasyon kütüphanesi: **yok**. shadcn'in güncel CLI'ı `form` yerine `field` bileşenini veriyor ve bir
      form kütüphanesi kurmuyor; formlar `Field` + bileşen durumuyla yazıldı, doğrulama backend'de kaldı ve alan
      hataları `HttpValidationProblemDetails.errors` içinden okunuyor. Bu, Haldus'un doğrulamasını da gösteriyor.
    - Frontend test framework'ü: **kurulmadı** (genel kural: test altyapısı yoksa kendiliğinden kurma). Panel
      `pnpm typecheck`, `pnpm lint`, `pnpm build` ile ve çalışan backend'e karşı tarayıcıda elle doğrulandı.
    - Bunlar yeni bağımlılıklardır (`react-router`, `i18next`, `react-i18next`) ve onayı alınmadı. Geri kalan
      bağımlılıklar genel kurallarda adı geçen araçlardan ve onların CLI'ından gelir.
  - Kimlik doğrulama (planlardan sonra; kimlik sağlayıcı seçimi sorulamadığı için yeni bağımlılık gerektirmeyen yol
    seçildi, **ayrıca onaylanmadı**): örnek mağaza e-posta + parola ile giriş yapar ve ASP.NET Core'un yerleşik
    taşıyıcı token şemasını kullanır (`AddBearerToken`; `Features/Auth/AuthEndpoints.cs`). Hesap (`ShopAccount`) elle
    yazılmış bir tablodur, Haldus kaynağı değildir; her hesap kendi kiracısıyla doğar. Parola `PasswordHasher<T>` ile
    özetlenir; ikisi de paylaşılan çatıdadır, paket eklenmedi. ASP.NET Core Identity ve OpenID Connect kullanılmadı.
    - Haldus'ta hiçbir şey değişmedi: kullanıcıyı ve kiracıyı zaten claim'lerden okuyordu.
    - `Shop:DemoIdentity` açıksa kimlik, doğrulanmadan, başlıklardan okunur (`X-User-Id`, `X-Tenant-Id`,
      `X-User-Roles`). Varsayılan olarak kapalıdır ve **yalnızca geliştirme ortamında açılabilir**: başka ortamda
      uygulama açılmaz. Entegrasyon testleri açar (`ShopFixture`), kimlik doğrulama testleri kapalı haliyle ikinci
      bir uygulama açar (`CreateAuthenticatedAppClient`); yük ölçümü de bu kipi kullanır.
    - `UseSessionValidation`: reddedilen bir token sunan istek her kaynakta 401 alır (`AllowAnonymous` uçlar
      hariç). İlk yazımda yoktu; panel tarayıcıda denenirken çıktı: geçersiz token'lı istek anonim sayılıyor,
      kiracıya ait kaynakta 403 alıyor ve istemci oturumu yenilemiyordu. Aynı ara katman token'ın oturumunu her
      istekte veritabanına sorar (aşağıda).
    - **Giriş yapılmadan hiçbir uca ulaşılmaz** (`FallbackPolicy`; demo kimliği kipinde konmaz). Açık kalanlar bunu
      kendileri söyler: giriş/kayıt/yenileme/sıfırlama uçları, Haldus'un token ile erişilen ve süreli bağlantı
      uçları, misafir sepeti (`guests.AllowAnonymous()`), API şeması. İlk yazımda yoktu: kiracıya ait olmayan
      kaynaklar (ürünler, kategoriler) token'sız yazılabiliyordu (bağımsız güvenlik incelemesinde çıktı). **Örnek
      mağazaya herkese açık olması gereken yeni bir uç eklenirse `AllowAnonymous` ile işaretlenmelidir.**
    - Oturumlar sunucudadır (`ShopSession`; token'daki `sid` claim'i) ve en çok 30 gün yaşar. Çıkış (`/auth/logout`,
      `/auth/logout-all`), parola sıfırlama ve parola değişimi oturumu iptal eder; iptal edilen oturumun erişim
      token'ı da yenileme token'ı da hemen reddedilir. Bedeli, token'lı her istekte bir birincil anahtar sorgusudur.
    - Yenileme token'ı bir kez kullanılır (`RefreshVersion`; token'daki `rv` claim'i, tek bir koşullu `UPDATE` ile
      ilerler). Eskimiş bir yenileme token'ı yeniden sunulursa oturum çalınmış sayılır ve kapatılır. Bu yüzden web
      paneli oturumu sekmeler arasında eşitler (`storage` olayı); eşitlemeden önce eski token'ı sunan ikinci sekme
      oturumu kapatırdı.
    - Parola sıfırlama ve e-posta doğrulama, e-postayla gönderilen altı haneli kodladır (bağlantı değil: mobilde
      derin bağlantı, web'de adreste token gerekmez). Kodun özeti saklanır, 15 dakika yaşar ve bir kez kullanılır.
      **Deneme ve gönderim sayıları koda değil saatlik pencereye aittir** (hesap + amaç başına beş deneme, üç kod):
      yeni kod istemek deneme hakkını yenilemez. İlk yazımda yeniliyordu ve altı haneli kod "kod iste, beş kez dene"
      döngüsüyle bulunabiliyordu (incelemede çıktı). Sayımlar tek bir `UPDATE` ile, sınanmadan önce yapılır.
      Sıfırlama e-postası yanıt beklenmeden gönderilir (süre, adresin kayıtlı olduğunu ele vermesin diye).
      `Shop:Auth:RequireVerifiedEmail` (varsayılan kapalı) doğrulanmamış hesabı kimlik uçlarının dışında tutar.
      Zaten doğrulanmış hesapta doğrulama ucu token vermez (204).
    - Hesap kilidi: art arda beş başarısız parola denemesinden sonra 15 dakika (`Shop:Auth`). Deneme, parola
      sınanmadan önce tek bir `UPDATE` ile sayılır (eşzamanlı denemeler sınırı aşamaz); kilitli hesabın yanıtı yanlış
      paroladakiyle aynıdır. Parola değiştirirken sorulan geçerli parola da aynı sayaca yazılır. Giriş, oturumu
      yazdıktan sonra "parola bu arada değişmedi" koşuluyla tamamlanır; değiştiyse oturum geri alınır.
    - Parola ve kod deneyen uçlar ayrıca adres başına dakikada 30 istekle sınırlıdır (ASP.NET Core'un yerleşik hız
      sınırlayıcısı); IPv6'da adres değil /64 bloğu sayılır. Bir vekilin arkasında vekil tanıtılmalıdır
      (`Shop:KnownProxies`), yoksa herkes tek bir sınırı paylaşır. Kayıt, adresin alınmış olduğunu söyler (409;
      bilinçli seçim).
    - E-posta `IAccountMailer` ile gönderilir: `Shop:Smtp:Host` verildiyse SMTP (`System.Net.Mail`), verilmediyse
      gönderilmez. Yalnızca geliştirme ortamında kod günlüğe yazılır (arayüzler posta sunucusu olmadan denensin
      diye); başka ortamda kod hiçbir yere yazılmaz.
    - İstemciler token'ı `Authorization` başlığında taşır, 401'de bir kez yeniler (eşzamanlı istekler tek yenilemeyi
      bekler) ve giriş/kayıt/yenileme uçlarına token eklemez. Web'de token'lar `localStorage` içindedir (XSS'e açık;
      örnek için kabul edilmiş ödünleşim), mobilde güvenli depoda, her biri ayrı anahtarda.
    - Bilinen sınırlar: kimlik sağlayıcı (OpenID Connect) desteği yoktur (JWT doğrulaması ayrı bir paket ister);
      iki adımlı doğrulama, e-posta değiştirme ve hesap silme yoktur; hesap kilidi bir hesabın başkası tarafından
      kilitlenmesine izin verir (parola sıfırlama kilidi açar); iptal edilmiş oturum ve kullanılmış kod kayıtları
      kendiliğinden temizlenmez. Token'lar Data Protection anahtarlarına bağlıdır; anahtarların kalıcılığı ve
      paylaşımı uygulamanın işidir.
  - Arayüzün dili `Accept-Language` ile backend'e de gider; backend'in mesajları o dilde döner.
  - Mobil uygulamada (aynı gerekçeyle, **ayrıca onaylanmadı**): yönlendirme genel kurallardaki Expo Router'dır; i18n
    web'deki gibi i18next + react-i18next, cihazın dili `expo-localization` ile okunur; oturum ve dil seçimi
    `expo-secure-store` ile saklanır (tarayıcıda `localStorage`); form kütüphanesi ve test altyapısı yoktur.
    `expo-localization` ve `expo-secure-store` da onayı alınmamış yeni bağımlılıklardır.
  - Mobil uygulama Expo'nun web hedefinde ve Android'de (emülatör, Android 17, Expo Go 56) çalışan backend'e karşı
    doğrulandı: kayıt, alan hatası, ürünler, favori, sepet, e-posta doğrulama, uygulama yeniden başlatılınca
    oturumun güvenli depodan geri gelmesi, çıkış, parola sıfırlama. Emülatör `adb` ile sürüldü (`uiautomator dump`
    ile öğeler bulunup dokunuldu). iOS'ta çalıştırılmadı (Windows); gerçek bir Android cihazda da denenmedi.
  - Örnek backend üç ayar kazandı, üçü de varsayılan olarak kapalıdır: `Shop:CreateDatabase` (tabloları açılışta
    oluşturur; yalnızca arayüzleri denemek için), `Shop:CorsOrigins` (sayılan kaynaklara CORS izni) ve
    `Shop:DemoIdentity` (kimliği başlıklardan okur; testler için).
  - Örnek mağaza bu yüzden sayıları katı okur (`JsonNumberHandling.Strict`): varsayılan ayar şemaya `integer | string`
    yazıyor ve istemci tipleri her sayıyı `number | string` görüyordu. Bu, örnek uygulamanın ayarıdır; Haldus
    tüketen uygulamaya dayatmaz.

- Süresi dolmuş `Expiring` kayıtları varsayılan olarak silinmez (`HaldusOptions.PurgeExpiredAfter` boş).
  Bu varsayılanı kullanıcı ayrıca onaylamadı; kullanıcı verisini kendiliğinden silmemek için böyle seçildi.

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
- **Yaşam döngüsü bildirimleri.** `Parsing/LifecycleReader` zincirdeki `Workflow`, `EditableIn`,
  `DeletableIn`, `Numbered` ve `Expiring` çağrılarını okur. Argümanları derleme zamanında okunabilen
  sabitler olmalıdır (enum üyesi, metin sabiti, `TimeSpan.FromMinutes(15)` gibi sabit argümanlı bir ifade);
  okunamayan argüman `HALDUS010` olur. Bu bildirimlerin eklediği alanlar (`Status`, `Number`, `ExpiresAt`)
  `ResourceModel.ExtraMembers` içinde durur ve trait alanları gibi davranır: yanıtta görünür, istek
  DTO'larına girmez, kullanıcı alanı kendisi yazdıysa yeniden üretilmez. Yeni bir yaşam döngüsü alanı
  eklerken `TraitCatalog` yerine burayı kullan.
- **Durum.** `Status` veritabanında adıyla saklanır (`HasConversion<string>()`) ve yanıtta adıyla yazılır
  (yanıt özelliğine `JsonStringEnumConverter` eklenir; uygulamanın genel JSON ayarına bağlı değildir).
  Arama gövdesindeki durum koşulu ise genel JSON ayarını izler. Liste, kullanıcının filtresi aynı adı
  taşımıyorsa `?status=` ile süzülür.
- **Geçişler.** `MapTransition`, `MapDetail` gibi `ResourceBuilder<T>` üyesidir ve sahibi çağrıldığı
  builder'ın tipinden okunur (`Parsing/TransitionParser`); sahibine `ResourceLinker.AttachTransitions`
  bağlar ve doğrular. Her geçiş için `Map{Ad}Transition(group, apply)` eşleyicisi üretilir ve
  `HaldusTransitionRegistry`'ye kaydedilir; uç, `MapTransition` çağrısı çalıştığında bağlanır ve lambda
  eşleyiciye parametre olarak gelir (bu yüzden standart uçlarla birlikte kendiliğinden eşlenmez). Kayıt
  anahtarı entity, istek tipi ve çağrıda açıkça verilen addır; ad bu yüzden sabit olmalıdır.
  - Adres, verilmediyse istek tipinin adından türetilir: `Request` / `Command` soneki ve entity adı atılır
    (`CancelOrder` → `cancel`). Aynı kaynağın bir trait'inin ucuyla (`archive`, `restore`, `expire`...)
    çakışan ad `HALDUS007` olur.
  - Sıra: `If-Match`, kaydı yükle, durum kontrolü (409), sürüm beklentisi, iş mantığı, durumu yaz,
    `Compute`, numara, kaydet. İş mantığı `TransitionRejectedException` fırlatırsa hiçbir şey kaydedilmez
    ve mesajı 422 ile döner; metni üretmek (ve çevirmek) fırlatanın işidir.
  - İstek `ITransition` uygulamıyorsa ve lambda verilmediyse handler eksikliği uygulama açılırken hata olur.
  - **Entity aksiyonu** durum kontrolü ve durum ataması olmayan bir geçiştir ve aynı hattan geçer
    (`TransitionParser`, `AttachTransitions`, `EndpointEmitter.EmitTransition`; `TransitionModel.IsAction`).
    Durum makinesi gerektirmez. Bir kaynağın builder'ında çağrılan `MapAction` her zaman entity'ye bağlıdır
    ve aynı adlı, entity'ye bağlı olmayan uzantı metodunun (`app.MapAction<TRequest>()`) yerine geçer. Üç
    biçimi vardır: gövdesiz (`MapAction("ad", lambda)`), istek tipli (`MapAction<TRequest>()`; mantık
    `IEntityAction<TEntity>`, lambda veya `IEntityActionHandler<TEntity, TRequest>`) ve kendi sonucunu
    döndüren (`MapAction<TRequest, TResponse>()`; yanıt kaydın kendisi değil sonuçtur, kayıt yine kaydedilir).
    Aksiyon kaydı değiştirdiği için kaynağın yazma kurallarına uyar (`If-Match`, idempotency, durumun yazmayla
    doğrulanması), ama `EditableIn` kuralına bağlı değildir; reddetmesi gerekiyorsa mantığı
    `TransitionRejectedException` fırlatır. Singleton ve gömülü detayda kullanılamaz (anahtarı yoktur).
  - **Durum kontrolü yazmayla birlikte yinelenir.** Kontrol ile kaydetme arasında başka bir istek durumu
    değiştirebilir. Sürüm kontrollü kaynakta bunu satır sürümü yakalar (412); diğerlerinde `Status`
    eşzamanlılık belirtecidir (`IsConcurrencyToken`) ve çakışma 409 `StateChanged` olur. Durum makinesi
    olan bir kaynağa yeni bir yazma ucu eklerken kaydetmeyi `Save()` ile yap; düz `SaveChangesAsync` bu
    korumayı atlar. Bilinen açık: ayrı uçlu detayın sahip durumu kontrolü (`OwnerGuard`) sahibin satırını
    kilitlemez; sahip tam o anda durum değiştirirse detay yazması yine de geçebilir.
  - Ayrı uçlu bir detay sahibinin parçasıdır: sahibinde `EditableIn` varsa detayın yazma uçları da sahibin
    durumuna bakar (`OwnerGuardModel`). Serbestçe yazılabilen alt kayıtlar için `MapDetail` yerine
    `.Under<T>()` kullanılır.
- **Numaralama.** Numara `HaldusNumbering.AllocateAsync` ile, kaydetmeden hemen önce ayrılır. Boşluksuz
  modda bu bir transaction açar (zaten açık biri varsa ona katılır), kapsamın danışma kilidini alır (bekleyenler
  geliş sırasıyla ilerlesin diye; satır kilidi tek başına yük altında bazı istekleri saniyelerce bekletiyordu) ve
  `HaldusCounters` tablosundaki sayaç satırını tek bir `INSERT ... ON CONFLICT ... DO UPDATE ... RETURNING` ile artırır; kaydetme başarısız
  olursa ya da uç bir hata yanıtıyla dönerse `await using` transaction'ı geri alır ve numara geri verilir.
  Bu SQL PostgreSQL'e özgüdür. Sayaç kapsamı desendeki tarih parçalarını içerir; `{yyyy}` varsa sayaç her
  yıl yeniden başlar. Sequence modunda sayaç kiracı ya da dönem başına sıfırlanmaz. Desenin kuralları
  derleme zamanında (`LifecycleReader`) ve çalışma zamanında (`NumberFormat`) ayrı ayrı uygulanır; birini
  değiştirirken diğerini de değiştir.
- **Süre dolumu.** Gizleme modeldeki `HaldusFilters.Expiry` filtresidir; şimdiki zamanı kiracı filtresiyle
  aynı yoldan, her sorguda geçerli bağlamdan okur (`HaldusScope.UtcNow(context)`). Üç biçim vardır:
  `.Expiring()` (zamanı istemci verir, sıradan yazılabilir bir alandır), `.Expiring(ömür)` (sunucu
  oluştururken hesaplar) ve `sliding: true` (her `PUT`, `PATCH` ve geçişte yeniden başlar).
- **Idempotency.** `HaldusIdempotencyFilter` oluşturma ve geçiş uçlarına, doğrulama filtresinden sonra
  eklenir. Anahtarı işlemin kendisiyle aynı transaction'da `HaldusIdempotencyRecords` tablosuna yazar; aynı
  anahtarla eşzamanlı ikinci istek birincinin transaction'ı bitene kadar bekler. Yalnızca 2xx yanıtlar
  saklanır; başarısız istek anahtarla birlikte geri alınır. Anahtarın kapsamı kiracı, kullanıcı, metot ve
  adrestir. Yanıt, saklanabilmesi için belleğe yazdırılır. `MapAction` kapsam dışıdır (entity'ye bağlı
  olmadığı için hangi bağlamın kullanılacağı belli değil).
- **İşler.** `MapJob` bir archetype'tır (`Archetype.Job`): `AppendOnly` gibi liste, tekil ve oluşturma uçları
  vardır; oluşturma 202 döner ve `POST /{id}/cancel` eklenir. İş entity'si `IHaldusJob` uygular; alanları
  (`Status`, `Progress`, `Error`, `StartedAt`...) `LifecycleReader.JobMembers` ile `ExtraMembers`'a girer.
  Girdi yazılabilir alanlar, çıktı herkese açık setter'ı olmayan alanlardır. Mantık bir `IJobHandler<TJob>`
  sınıfında veya `MapJob`'a verilen lambda'dadır.
  - Yaşam döngüsünü, hangi altyapı teslim ederse etsin, `HaldusJobRunner` yönetir: kaydı yükler, `Running`
    yapar, mantığı çağırır, sonucu yazar. Başarıda mantığın bağlamı kaydedilir; başarısızlık ve iptalde o
    bağlam atılır ve sonuç ayrı bir kapsamda `ExecuteUpdate` ile yazılır (yarım kalan değişiklikler
    kaydedilmez). `JobFailedException` mesajı `Error` alanına yazılır; başka hataların ayrıntısı istemciye
    gösterilmez, yalnızca günlüğe yazılır.
  - İş bir isteğin dışında çalışır. Kiracı ve kullanıcı `HaldusAmbient` ile taşınır; sorgu filtreleri,
    interceptor ve numaralama kimliği `HaldusScope.TenantId` / `UserId` üzerinden okur (istekten çözüleni
    değil). Yeni bir yerde kiracıya ihtiyaç olursa `ITenantProvider`'ı doğrudan değil bunları kullan. İş
    ayrıca, onu oluşturan isteğin diliyle çalışır (`Culture` alanı).
  - `Status` eşzamanlılık belirtecidir: iki yürütücü aynı işi sahiplenemez, iptal ile başlatma yarışmaz.
  - Mantığı hata fırlatan iş yeniden denenmez. Süreç kapanırken yarıda kalan iş `Running` kalır ve altyapı
    yeniden teslim ederse baştan çalışır (`HaldusJobOptions.MaxAttempts`, varsayılan 3); bu yüzden
    mantığın yeniden çalışmaya dayanıklı olması gerekir.
  - İptal: bekleyen iş hemen `Cancelled` olur; çalışan iş için `CancelRequested` yazılır, aynı süreçteki
    yürütücü hemen, başka bir sunucudaki yürütücü kaydı yoklayarak (`CancellationPollInterval`) görür.
  - Altyapı arayüzü iki adımlıdır: `Prepare` kaydetmeden önce çağrılır (PostgreSQL kuyruğu satırını aynı
    bağlama ekler; iş ve kuyruk kaydı birlikte kalıcı olur), `DispatchAsync` kaydettikten sonra. Yürütücü,
    kayıt henüz görünmüyorsa (transaction bitmemiş olabilir) kısa aralıklarla yeniden bakar.
  - PostgreSQL kuyruğu `FOR UPDATE SKIP LOCKED` ile alınır, çalışırken kirası uzatılır, iş sonuçlanınca
    silinir. Bellek içi kuyrukta süreç yeniden başlarsa bekleyen işler kaybolur.
  - Hangfire paketi yalnızca `Hangfire.Core`'a bağlıdır: işi `IBackgroundJobClient` ile kuyruğa koyar ve
    Hangfire çalıştırmak istediğinde `HaldusHangfireJob` yürütücüyü çağırır. Depolamayı, sunucuyu ve paneli
    uygulama kurar. Kuyruğa koyma iş kaydıyla aynı transaction'da değildir. Hangfire'ın istediği en düşük
    `Newtonsoft.Json` sürümünde bilinen bir güvenlik açığı olduğu için o paket yamalı sürüme sabitlenir
    (`Directory.Packages.props`); Hangfire'ı yükseltirken bu satırın hâlâ gerekli olup olmadığına bak.
  - Build sırasındaki şema üretimi arka plan servislerini de başlatır; iş altyapıları
    `HaldusJobs.IsGeneratingOpenApiDocument` ile o sırada veritabanına bağlanmaz. Yeni bir arka plan
    servisi eklerken aynı kontrolü yap.
- **Toplu uçlar.** `Emit/EndpointEmitter.Batch.cs`. Her öğe, tekil ucun kullandığı kod parçalarıyla
  (`ReferenceChecks`, `StateGuard`, `SyncDetails`, `Compute`, `Save`...) ayrı bir `Batch*ItemAsync` metodunda
  işlenir ve hemen kaydedilir; kurallar bu yüzden toplu yolda da aynen geçerlidir. **Tekil bir uca yeni bir
  kural eklerken onu bu paylaşılan parçalara koy; doğrudan handler'a yazılan kural toplu yolda uygulanmaz.**
  - Atomik modda tüm öğeler tek bir `HaldusBatch.BeginAsync` sınırı içindedir: ilk hatada dönülür ve hepsi
    geri alınır; hata, öğenin sırasını (`index`) ve alan adlarını `items[2].` önekiyle taşır. Kısmi modda her
    öğe kendi sınırı içindedir ve istek, öğe başına sonuçla 207 döner. Sınır, açık bir transaction yoksa
    transaction, varsa (idempotency filtresi) kayıt noktasıdır.
  - Atomik modda öğeleri isteğin doğrulama filtresi, kısmi modda `HaldusBatch.Validate` öğe başına doğrular.
  - Ebeveynin ve sahibin durumunun kontrolü istek başına bir kez yapılır. Toplu okuma her zaman kısmidir:
    bulunanlar ve bulunamayan kimlikler ayrı döner.
  - Sınırlar: sürüm kontrollü kaynakta toplu güncelleme ve silme yoktur (öğe sürüm taşıyamaz; `HALDUS007`),
    toplu yama (`PATCH`) yoktur, geçişlerin toplu biçimi yoktur. Kısmi modda bir öğe beklenmeyen bir hatayla
    (500) düşerse ondan önceki öğeler kaydedilmiş kalır.
- **Bakım.** `HaldusMaintenanceService` (her `AddHaldus` uygulamasında çalışır) `MaintenanceInterval`
  aralığıyla `HaldusMaintenance.RunAsync` çağırır: saklama süresi geçmiş idempotency yanıtlarını her zaman,
  süresi dolmuş `Expiring` kayıtlarını yalnızca `PurgeExpiredAfter` verildiyse siler. Silinecek kaynakları
  üretilen modül `HaldusMaintenanceRegistry.RegisterExpiring<T>()` ile kaydeder. Bakım bir isteğe ait
  olmadığı için burada parametresiz `IgnoreQueryFilters()` bilerek kullanılır; üretilen uç kodunda yasak
  olmaya devam eder. Aynı yöntem testlerden ve yönetim komutlarından doğrudan çağrılabilir.
- **Sahiplik.** `Traits.Owned` (`IOwned`, `OwnerId`); `/me` dışında kalma `Traits.OwnedPlainRoute` ile tutulur.
  Görünürlük modeldeki `HaldusFilters.Owner` filtresidir ve kullanıcıyı kiracı filtresiyle aynı yoldan okur
  (`HaldusScope.UserId(context)`); bu yüzden uçlarda sahiplikle ilgili tek satır yoktur ve kaynağa dokunan her
  sorgu (referans doğrulaması, ebeveyn filtresi, bağlar) kendiliğinden korunur. Sahibi interceptor atar:
  geçerli kullanıcı varsa her zaman o yazılır (elle verilen değer ezilir), güncellemede alan değişmez.
  - Başka bir kullanıcı adına kayıt oluşturacak sistem kodu (ör. bildirim) o kullanıcı olarak çalışmalıdır:
    `HaldusAmbient.Use(tenantId, userId)`. Bir isteğin içinde `OwnerId`'yi elle vermek işe yaramaz.
  - Adres: bir kaynağın koleksiyon adresi `ResourceModel.CollectionRoute`'tur (`me/carts`); altına yerleşen
    kaynaklar adreslerini bundan kurar (`ResolvedReference.TargetRoute`). Sahipli kaynakların ve `/me` ile
    başlayan her grubun başında `HaldusUserRequiredFilter` vardır.
  - Sınırlar: yalnızca `MapCrud`, `MapReadOnly`, `MapAppendOnly` ve `MapJob` ile birleşir (kullanıcı başına tek
    kayıt, yani `/me` altında `MapSingleton`, yok). `.Under<T>()` ile birlikte `underMe: false` gerekir. `/me`
    altındaki bir kaynak `MapAssociation`'ın ucu olamaz (ters yöndeki listenin adresi belirsiz olurdu).
  - Sahiplik silme davranışını değiştirmez: "zincirleme silme kiracı sınırını aşmaz" kuralı yalnızca kiracı
    içindir. Ortak bir ebeveyn silinince altındaki, başka kullanıcılara ait kayıtlar da silinir. Bunu kullanıcı
    ayrıca onaylamadı; sahiplik bir yetki sınırı değil görünürlük kuralı olduğu için böyle bırakıldı.
  - İş (`MapJob`) sahipliyse yürütücü onu sahibinin kimliğiyle çalıştırır (`HaldusJobs`).
- **Geçerli kullanıcıya bağlı bağ.** `MapAssociation` çağrısını `.ForCurrentUser()` izliyorsa
  `ResourceLinker.LinkUserLinks` onu `UserLinkModel`'e çevirir ve `Emit/UserLinkEmitter` üretir; sıradan bağların
  yolundan (`LinkAssociations`, `AssociationEmitter`) geçmez. Bağ entity'si `IOwned` uygular, anahtarı
  `(OwnerId, {Uç}{Anahtar})` çiftidir ve iki filtre taşır: `Owner` ile `Association` (karşı uç görünür olmalı).
  Sahip anahtarın parçası olduğu için kayıt izlenmeden önce üretilen kodda atanır. Karşı uç silinince bağ da
  silinir (`Cascade`): bağ yalnızca bir işarettir ve bir kullanıcının işareti silmeyi engellememelidir.
  Yazılmamış bağ entity'si, her yazılmamış entity gibi, çağrının bulunduğu namespace'e üretilir. Kullanıcıya bağlı
  bağ yük ve durum taşımaz (sıradan bağlar taşır); değer taşıyan oy bu yüzden yazılamaz.
- **Alan eklentileri.** Alanları lambda ile sayan bildirimler (`Sortable`, `CreateOnly`, `NaturalKey`) `Searchable`
  gibi `ResourceBuilder<T>` üyesidir, `MapCallParser.ReadFieldRequests` ile okunur ve `ResourceLinker`'da, üretilen
  yabancı anahtarlar da bilindikten sonra çözülür. Geçersiz alan `HALDUS010` olur.
  - **Create-only.** `PropertyModel.IsCreateOnly`; güncelleme isteklerine ve atamalarına `IsUpdatable` alanlar
    girer. Yalnızca `MapCrud` (ve ayrı uçlu detay) ile birleşir. Yalnızca oluştururken yazılan bir yabancı anahtar
    güncellemede yeniden doğrulanmaz; `ParentId` öyleyse döngü kontrolü de üretilmez.
  - **Doğal anahtar.** Tek alan; null olamayan `string`, `int`, `long` veya `Guid`. Benzersiz indeks kaydın kapsamını
    izler (kiracı, sahip, ebeveyn + alan) ve adı açıkça verilir (`UX_{Tablo}_{Alan}`,
    `ResourceEmitter.NaturalKeyIndex`). Çakışma iki yerde yakalanır: yazmadan önce `NaturalKeyCheck` (silinmiş ve
    süresi dolmuş kayıtları da görür; onlar da değeri tutar) ve kaydederken `DuplicateCatch`
    (`HaldusDb.IsUniqueViolation`, PostgreSQL'in 23505 kodu ve indeks adı). İkincisi olmadan eşzamanlı iki istekten
    biri 500 alırdı. Yeni kaydı kaydeden yol `SaveNew()`, var olanı kaydeden yol `Save()` kullanmalıdır.
    Kontroller `WriteChecks` içindedir; toplu yol da onu çağırır.
  - **Sıralama.** `TrySort` üretilir; tanınmayan alan ve sıralanabilir alan sayısından çok terim 400 döner.
    Sıralama her zaman anahtarla tamamlanır. Sıralanabilir alanlar aranabilir alanlarla aynı adaylardan seçilir
    (`ResourceLinker.FieldCandidates`). `MapLookup` listesi sıralanamaz (her zaman koda göredir).
  - **Anahtar biçimi.** `ResourceModel.KeyGeneration` / `KeyPrefix`. Önekli anahtarda `IdKind.String` olur; bu
    yüzden anahtarın metin olması artık "anahtarı istemci verir" demek değildir, onu `Archetype.HasClientKey()`
    söyler. Gövde, v7 GUID'in Crockford base32 yazımıdır (`HaldusKeys.NewPrefixed`); metin sırası oluşturulma
    sırasıdır. Model ayrıca bir değer üreteci kurar (`HaldusDb.ConfigurePrefixedKey`): uygulamanın kendi eklediği,
    anahtarı boş kayıtlar da aynı biçimi alır. Önek derleme zamanında okunan bir metin sabiti olmalıdır.
    `MapCrud`, `MapReadOnly` ve `MapAppendOnly` ile birleşir; işin kimliği kuyruklarda `Guid` taşındığı için
    `MapJob` ile birleşmez. Gömülü detay satırlarının kimliği her zaman v7 GUID'dir.
  - **ULID ve istemcinin kimliği.** `KeyGeneration.Ulid` önekli anahtarla aynı yoldan geçer (`IdKind.String`,
    `HaldusKeys.NewUlid`, `HaldusDb.ConfigureUlidKey`); gerçek bir ULID'dir (48 bit zaman + 80 bit rastgele), aynı
    milisaniyede üretilen iki kimliğin sırası rastgeledir. `KeyGeneration.Client` yalnızca tekil oluşturma ucunu
    değiştirir: handler kimliği sorgudan alır (`ClientKey`, `ClientKeyCheck`), `CreateInitializer` onu atar ve alınmış
    kimlik birincil anahtarın ihlalinden tanınıp 409 olur (ön kontrol yoktur: başka bir kiracının aynı kimliği de
    çakışmadır ve sorgu filtreleri onu göstermezdi). Kayıt oluşturan öteki yollar kimlik veremediği için `Batch`,
    `Cloneable`, `CreatedFrom`, `Reversible`, `Supersedes`, `Document` ve `Publishable` ile birleşmez (`HALDUS007`).
    Görünürlüğü sınırlı kaynaklarla da birleşmez (`MultiTenant`, `OwnedByCurrentUser`, `Under`, `AttachedTo`,
    `TokenOnly`, `SoftDeletable`, `Expiring`, `SubtypeOf`; kayıtları gizleyen yeni bir bildirim bu listeye eklenmelidir): anahtar
    tablo genelinde benzersizdir ve "alınmış" yanıtı, isteği yapanın göremediği bir kaydın var olduğunu söylerdi
    (güvenlik incelemesinde çıktı). Öyle kaynaklarda yinelenen isteğe karşı `Idempotent()` kullanılır.
  - **Metadata.** `Traits.Metadata` (`IHasMetadata`). Alan trait alanı gibi üretilir ve yanıtta yer alır;
    isteklerdeki yeri ayrıca üretilir (`ResourceEmitter.AppendRequest`, `MetadataChanges`). Çanta jsonb kolonunda,
    metne çevrilerek saklanır (`HaldusDb.ConfigureMetadata`); böylece Npgsql'in dinamik JSON desteğinin açılması
    gerekmez. Sınırları `MetadataAttribute` doğrular; `PATCH` birleştirdiği için sınır birleşmiş çantaya da
    uygulanır. `PUT` isteğinde çanta gönderilmezse boşaltılır (gömülü satırların aksine zorunlu değildir).
    Yanıt DTO'su bir sözlük taşıdığı için record eşitliği artık içeriği karşılaştırmaz.
- **Kayıt işlemleri.** Üçü de trait bayrağıdır (`Traits.Orderable`, `Cloneable`, `Previewable`); uçlarının adları
  (`move`, `clone`, `preview`) geçiş ve aksiyon adı olarak kullanılamaz (`ResourceLinker.ReservedRoute`).
  - **Sıra.** `IOrderable.Position` trait alanıdır: yanıtta görünür, isteklerde yer almaz, aranabilir ve
    sıralanabilir. Kapsam, kaydın görünürlüğünü çizen şeylerdir (kiracı ve sahip sorgu filtresinden, ebeveyn
    adresten) ve ağaçta ayrıca üst düğümdür (`EndpointEmitter.Siblings`). Konumlar boşluksuz kalsın diye kapsamın
    üyelerini değiştiren her yazma `HaldusOrdering.BeginAsync` kilidinin (PostgreSQL `pg_advisory_xact_lock`;
    açık transaction yoksa yenisini açar, varsa katılır) altında yapılır: oluşturma ve geri yükleme sona ekler
    (`OrderAppend`), silme arkadakileri öne alır (`OrderCloseGap`), üst düğümü değiştiren güncelleme ikisini
    birden yapar (`OrderReparent`), taşıma kapsamı okuyup yeniden numaralar (`HaldusOrdering.MoveAsync`).
    **Sıralı bir kaynağa kapsamın üyelerini değiştiren yeni bir uç eklerken bu parçaları kullan.** Kilidin
    anahtarında üst düğüm yoktur (bütün ağaç tek kilittir), çünkü düğüm daldan dala geçerken iki dal birlikte
    değişir. Liste, başka bir sıra istenmedikçe `Position` ile döner.
    Sınırlar: süresi dolan (`Expiring`) kayıt gizlenir ama konumunu tutar, bu yüzden görünen sırada boşluk
    kalabilir; komşuları yeniden numaralamak onların `UpdatedAt` alanını ilerletmez ama sürümlerini (xmin)
    ilerletir; büyük kapsamlarda taşıma çok satır günceller (kullanıcı bunu bilerek seçti).
  - **Kopya.** `EmitClone`: kaynak izlenmeden okunur, yeni kayıt `CreateInitializer(source: "source")` ile kurulur
    (kimlik, ebeveyn, başlangıç durumu ve ömür yeni kayıttaki gibi), isteğin gönderdiği alanlar
    `PatchAssignments(_writable)` ile üstüne yazılır, satırlar yeni kimliklerle kopyalanır ve kayıt
    `SaveCreated()` ile kaydedilir (numara ve sıra oluşturmadakiyle aynı yoldan). İstek tipi
    `Clone{Entity}Request` kısmidir ve yalnızca oluştururken yazılan alanları da taşır; gövde isteğe bağlıdır.
    Doğal anahtar kopyanın değeri üzerinden denetlenir; değiştirilmediyse 409 döner. Kopyanın bütün referansları
    (kaynaktan devralınanlar dahil) kayıttan okunarak doğrulanır (`ReferenceChecks(source: "entity")`): kaynağı
    görebilmek, referans verdiği kaydı görebilmek demek değildir (güvenlik incelemesinde çıktı). **Alanlarını
    istekten değil başka bir kayıttan alan her yeni kayıt (kopya, yerine geçen, ters kayıt, üretilen kayıt)
    referanslarını böyle doğrulamalıdır.**
  - **Önizleme.** `EmitPreview`: gerçek ucun kontrol ve hesaplama parçalarını kullanır, kaydetmez. Oluşturma
    önizlemesinde kimlik üretilmez (`CreateInitializer(preview: true)`); kaydederken atanan alanlar (numara,
    zaman damgaları, kiracı, sahip, konum) boştur. Güncelleme önizlemesi kaydı izleyerek değiştirir ve sonunda
    `ChangeTracker.Clear()` çağırır; sürüm (`If-Match`) istemez. Tekil ucun kurallarını paylaşılan parçalara
    koyma kuralı burada da geçerlidir: doğrudan handler'a yazılan bir kural önizlemede uygulanmaz.
- **Kayıtlar arası zincir.** Dört bildirimin ortak kuralı: alanlarını istekten değil başka bir kayıttan alan yeni
  kayıt, oluşturma ucunun yaptığı her kontrolden geçer (referanslar kayıttan okunarak doğrulanır, doğal anahtar
  kayıt üzerinden denetlenir, `Compute` çağrılır) ve `SaveCreated()` ile kaydedilir. Sıra her yerde aynıdır:
  kopya, entity'nin sözleşmesi, isteğin gönderdiği alanlar, kontroller. Sözleşme satır ekleyip çıkarabildiği için
  ondan sonra satırlara kimlik ve sıra verilir (`NormalizeRows`); sözleşmeye verilen kaydın satırları da kendi
  sıralarına dizilir (`SortRows`), çünkü veritabanı onları belirli bir sırayla vermez.
  - **Çiftler** (`ChainOperation`: `Reverse`, `Supersede`). İki işaret alanı `ExtraMembers`'tadır ve tipi anahtarın
    tipidir; bu yüzden anahtar belli olunca eklenir. Yeni kaydın anahtarı kaydetmeden önce bilinmelidir, bu
    yüzden veritabanının ürettiği anahtarla (`int`, `long`) birleşmez. "Bir kaydın yalnızca bir karşı kaydı olur"
    kuralını ileri işaret alanındaki benzersiz indeks sağlar; ön kontrolü birlikte geçen iki istekten ikincisi
    ona takılır ve 409 alır. Ters kayıt zincirin sonudur (yeniden ters çevrilemez), yerine geçen kaydın ise yerine
    geçilebilir. Korumalar (güvenlik incelemesinden sonra sıkılaştırıldı):
    - Kapanmış kayıtta (ters çevrilmiş veya yerine geçilmiş) hiçbir yazma çalışmaz: `PUT`/`PATCH` (`ChainGuard`)
      ve geçişler ile entity aksiyonları (`ChainClosedGuard`).
    - Ters kaydın alanları asıl kayıttan türemiştir, o da `PUT`/`PATCH` ile değiştirilemez; ama geçişlerden
      geçebilir (yeni bir kayıt olarak başlangıç durumunda doğar ve ilerletilmesi gerekir).
    - Bir çiftin parçası olan kayıt silinemez (`ChainDeleteGuard`); hiçbir çifte girmemiş kayıt silinebilir.
    - Zincir işlemi, kaydın düzenlenemediği durumlarda da çalışan bir yazmadır. Kaynağın durum makinesi varsa
      işlemin yapılabildiği durumlar açıkça sayılmalıdır (`.Reversible(Status.Posted)`); sayılmazsa `HALDUS010`
      olur. Durum makinesi yoksa işlem her zaman yapılabilir.
  - **Üretme** (`ReferenceKind.Source`, `OriginModel`). Kaynağa işaret eden alan istekle yazılamaz; yalnızca üretme
    yolu doldurur. Kurma işi hedefin uç sınıfındaki herkese açık `From{Kaynak}Async` metodundadır
    (`HaldusBuilt<T>`: ya kayıt ya hata); `from-…` ucu da kaynağın geçişi de onu çağırır. `on:` verildiyse açık uç
    üretilmez, çünkü geçişin koşullarını atlamanın yolu olurdu. `from-…` ucu bir oluşturmadır: `Create` işlemi
    kaldırılmış bir kaynakta `on:` verilmeden bildirilemez (`HALDUS007`). İki kaynağı da ilgilendiren her şey
    `ResourceLinker.AttachOrigins` içinde, geçişler bağlandıktan sonra çözülür. Sınırlar: kaynak aynı projede
    eşlenmiş olmalıdır; geçişle doğan kayıt bir ebeveynin altında olamaz, oluştururken numara veya sıra alamaz ve
    kendisinden başka kaynak türeyemez; aynı kaynaktan birden çok kayıt üretilebilir (tekilliği zorlayan bir
    seçenek yok). `Reverses<T>` ayrı bir bildirim olarak eklenmedi; `CreatedFrom<T>` aynı şeyi ifade eder.
  - **Türetme.** Hedef `DerivedFrom<T>` ile bildirir; çağrı kaynağın oluşturma yollarına eklenir (`Derive`,
    `DeriveCommit`; `SaveCreated` içinde ve toplu oluşturmada). İzdüşüm kilit altında ve kaynakla aynı
    transaction'da çalışır (`HaldusDerivations.BeginAsync`, kaynak başına tek kilit): okuyup değiştiren iki
    izdüşüm aynı anda çalışsa biri ötekini ezerdi. Kaynak `MapAppendOnly`, hedef `MapReadOnly` olmalı ve ikisi aynı
    bağlamda durmalıdır. İzdüşüm sınıfı handler'lar gibi bulunur ve kaydedilir; eksikse hata ilk yazmada verilir
    (uygulama açılırken değil). Baştan yeniden kurma ve kaynağın değişmesi/silinmesi kapsam dışıdır; kilit
    kaynak başına olduğu için yazma hacmi yüksek kaynaklarda darboğaz olur.
- **Sırlar.** `LifecycleReader` `SecretOnce`, `Rotatable` ve `TokenAccess` bildirimlerini okur (`SecretModel`,
  `TokenModel`); alanları (`SecretHash`, `SecretHint`, `SecretCipher`, `PreviousSecret*`, `TokenHash`)
  `ExtraMembers`'tadır ve entity ilgili arayüzü uygular (`IHasSecret`, `IHasRotatingSecret`,
  `IHasRecoverableSecret`, `IHasAccessToken`).
  - Sırrın kendisi hiçbir alana yazılmaz. Oluşturma ucunda üretilir (`SecretVariables`), kayda özeti yazılır
    (`SecretAssignments`) ve yanıta `ToResponse(entity) with { Secret = secret }` ile eklenir; yanıt DTO'sundaki
    `Secret` / `Token` alanı null iken JSON'a hiç yazılmaz. **Bir yanıta sır eklerken yalnızca bu yolu kullan;
    sırrı entity'ye ya da `ToResponse`'a koyma.**
  - Değer yalnızca tekil oluşturma yanıtında gösterilebildiği için `SecretOnce` ve `TokenAccess`, kayıt oluşturan
    diğer yollarla (`Batch`, `Cloneable`, `Previewable`, `Reversible`, `Supersedes`, `CreatedFrom`) birleşmez.
  - Özet SHA-256'dır: sır 256 bit rastgele olduğu için yavaş bir parola özetine gerek yoktur ve özet aramada da
    kullanılır (benzersiz indeks). Döndürme kaydın içeriğine dokunmadığı için düzenleme durum kuralına bağlı
    değildir (sızmış bir sır her durumda kapatılabilmelidir); sürüm (`If-Match`) ve kapanmış kayıt korumasına ise
    bağlıdır.
  - Geri okunabilen sır `HaldusSecretProtection` ile şifrelenir (amaç dizesi entity tipini içerir); `AddHaldus`
    `AddDataProtection()` çağırır. Anahtarların kalıcılığı uygulamanın işidir.
  - **Token uçları oturumsuzdur.** `HaldusTokenAccess` metadatası taşırlar ve `AllowAnonymous` ile işaretlenirler;
    kiracı ve kullanıcı zorunluluğu filtreleri bu metadatayı görünce geçer. Kayıt token'ın özetiyle, yalnızca
    kiracı filtresi kaldırılarak bulunur (silinmiş ve süresi dolmuş kayıt bulunmaz). Token ile çalışan geçiş,
    kaydı bulduktan sonra asıl geçiş metodunu çağırır (kuralları aynen geçerlidir) ve, kayıt kiracıya aitse,
    `HaldusAmbient` ile o kiracı adına çalışır. Hangi geçişlerin token ile çalıştığı derleme zamanında sabittir;
    listede olmayan geçişin token adresi yoktur. Token ebeveyn altındaki ve kullanıcıya ait kaynaklarla
    birleşmez (görünürlükleri, token'ı taşıyanın sahip olmadığı bir kimliğe bağlıdır). Bilinen sınır: token
    kullanılınca kendiliğinden geçersiz olmaz; ömrünü `Expiring` ve durum makinesi belirler. Oran sınırlama yoktur.
  - **Yalnızca token** (`TokenModel.Only`; zincirde `TokenOnly`, sırası önemli değildir). Temel işlemler oluşturmaya
    indirilir (`MapCallParser`), `expire` ucu üretilmez ve geçiş eşleyicisi kimlikli adresi eşlemeyip token adresini
    döndürür (`EmitTransition`: `routes`). Kayda kimliğiyle ulaşacak her şey reddedilir: zincirde bir beyaz liste
    vardır (`Conflicts`; kimlikli uç üreten veya kayıtları listeleyen her bildirim `HALDUS007`), bağlayıcı ise
    `TokenAccess`'te sayılmamış geçişi, komutla yazılan detayı, kaydın altına yerleşen veya ona takılan kaynağı,
    ondan kimlikle üretilen kaynağı (`CreatedFrom`, `on:` olmadan), bağın ucu olmasını ve aktarılmasını reddeder.
    **Kimlikli bir uç üreten yeni bir bildirim beyaz listeye eklenmemelidir.** Bilinen sınır: oluşturma yanıtının
    `Location` başlığı var olmayan kimlikli adresi gösterir.
- **Tekrar serisi.** `Traits.Recurring` (`IRecurring`); yedi alan `LifecycleReader.RecurrenceFields()` ile, dönemli
  kayıtlardaki gibi sıradan, yazılabilir alan olarak eklenir. Oluşlar saklanmaz: `HaldusRecurrence.Expand` her okumada
  kuraldan hesaplar. Hesap dönem dönemdir (`Candidates`: gün, hafta, ay, yıl) ve duvar saatiyle yapılır; dönem ilk
  oluştan sayılır, gün listesi verildiyse bir dönem birden çok oluş verir, sayı sınırı oluşları sayar. Dönem başına tek
  oluş varsa veya seri açık uçluysa aralığın başına atlanır (`FirstPeriod`; bir dönem pay bırakır); günleri **ve**
  sayısı olan seri baştan gezilir (sayı en çok 1000). Hiç oluş vermeyen kural `MaxPeriods` dönemden sonra bırakılır.
  Gün listesi ve saat dilimi kendi attribute'larıyla doğrulanır (`RecurrenceDaysAttribute`, `TimeZoneIdAttribute`);
  sıralı gün (`2TU`) aylık ve yıllık olmayan seride reddedilir, ama sıklığı göndermeyen bir `PATCH`'te bu denetlenemez
  ve sıra yok sayılır. Hesaplanan oluşlar
  `HaldusOccurrenceExceptions` tablosundaki istisnalarla birleştirilir. İstisna, kuralın verdiği zamanla (UTC) anahtarlanır;
  kuralın vermediği bir zamana istisna yazılamaz (404). `RecurrenceFrequency` enum'u JSON'a her zaman adıyla yazılır
  (dönüştürücü tipin üzerindedir). Uçlar kaydı kendi filtrelerinden geçirerek okur. **Tek bir oluşu değiştirmek serinin
  bir yazmasıdır:** uç, satır komutlarıyla aynı diziden geçer (`ReadIfMatch`, `OwnerGuard`, `ChainGuard`, `StateGuard`,
  `ExpectVersion`), istisnayı kaydetmeden hazırlar (`HaldusRecurrence.ChangeAsync` kaydetmez), seriyi `Modified`
  işaretler ve `Save()` ile ikisini birlikte kaydeder; böylece serinin sürümü ilerler ve onayı düşer. İlk yazımda
  yalnızca `EditableIn` kuralına bakılıyordu (güvenlik incelemesinde çıktı). Sınırlar: yalnızca
  `MapCrud`; kural değişince eski istisnalar yok sayılır ama silinmez; kayıt silinince istisnaları kalır; oluş başına
  alan (ör. farklı başlık) taşınmaz; oluşlara göre süzme, "bu aralıkta olanlar" sorgusu ve çakışma kontrolü yoktur;
  bir istek en çok 500 oluş döndürür.
- **Mantığın eklediği satırlar.** Geçiş ve entity aksiyonu gömülü satır ekleyip çıkarabilir: üretilen uç mantıktan önce
  satırları sıralarına dizer (`SortRows`), sonra yeni satırlara kimlik ve hepsine koleksiyondaki yerlerine göre sıra
  verir (`RowsAfterLogic` → `NormalizeRows`; zincir sözleşmeleriyle aynı parça). Bundan önce mantığın eklediği iki
  satır aynı (boş) kimlikle izlenmeye çalışılıp 500 veriyordu (kalan satırlar sınanırken çıktı).
- **Tip–örnek ve rol.** İkisi de zincirde `Reference` bildirimi olarak okunur (`ReferenceDeclaration.Typed` / `Role`) ve
  `Resolve` yabancı anahtarı yalnızca oluştururken yazılan bir alan yapar (`fixedOnCreate`; kısa adresli ebeveynle
  aynı yol). Hedef aynı projede eşlenmiş olmalıdır.
  - `ResolveTypeDefaults` devralınan alanları seçer: kayıtta yazılabilir ve boş bırakılabilir, tipte aynı adla ve
    aynı değer tipiyle bulunan alanlar (`ResourceModel.TypeDefaults`). Doldurma `EndpointEmitter.TypeDefaults()`
    parçasıdır ve **`Compute()` parçasının başına eklenir**: böylece hesaplamanın çağrıldığı her yazma yolunda
    (oluşturma, güncelleme, yama, toplu yollar, kopya, önizleme, değişiklik kümesi, geçiş ve aksiyon) hesaptan önce
    çalışır. Tip, kendi sorgu filtrelerinden geçerek okunur; her yazma bir ek sorgudur. Uygulamanın kendi kodu
    kaydı doğrudan yazarsa devralma çalışmaz (uçlara bağlıdır, kaydetmeye değil).
  - `ResolveRole` oyuncunun yabancı anahtarını kaydın doğal anahtarı yapar; benzersiz indeks, çakışmanın 409'u ve
    `by-{alan}` ucu doğal anahtarın yolundan gelir. Bu yüzden rol başka bir `NaturalKey` bildiremez ve tek bir
    oyuncunun rolüdür. Silme davranışı varsayılan olarak `Cascade`'dir; ama "zincirleme silme kiracı sınırını aşmaz"
    kuralı rol için de geçerlidir: kiracıya ait rol genel bir oyuncuya bağlıysa varsayılan `Restrict` olur (`Resolve`
    artık bu kuralı ebeveyne özgü değil, varsayılanı `Cascade` olan her bildirim için uygular; güvenlik incelemesinde
    çıktı). **Varsayılanı zincirleme silme olan yeni bir ilişki bildirimi bu koşuldan geçmelidir.**
  - Hassas alanlar devralınmaz, iki yönde de: tipin yalnızca yazılan bir alanı, kaydın okunabilir aynı adlı alanına
    taşınsaydı gizli değer kaydın yanıtında görünürdü (güvenlik incelemesinde çıktı).
- **Genelleme.** `ResourceModel.BaseDeclaration` → `ResourceLinker.PrepareSubtypes` (bağlamanın ilk adımı): alt tipin
  modeli üst tipin trait'leriyle birleştirilir (`BaseType`), üst tip alt tiplerini öğrenir (`Subtypes`) ve ikisi de
  `Kind` alanını `ExtraMembers`'a alır. Alt tip için entity üretilmez (`EmitEntity` boş döner; anahtar ve trait
  alanları C# kalıtımıyla gelir) ve model yapılandırması yazılmaz; tablo, anahtar, sorgu filtreleri ve ayırt edici
  alan yalnızca üst tipte kurulur (EF Core sorgu filtresine yalnızca kök tipte izin verir). `Kind` boş metinle başlar;
  EF Core'un değeri atayabilmesi için alanın "atanmamış" değeri bildirilir (`HasSentinel`). Alt tipin uçları standart
  üreticiden, birleşmiş modelle geçer ve `Set<Car>()` üzerinde çalışır.
  - **Üst tipin zinciri bir beyaz listeyle sınırlıdır** (`MultiTenant`, `Auditable`, `SoftDeletable`, `Archivable`,
    `ConcurrencyChecked`, `Without`, `Only`, `Sortable`, `Searchable`, `Filterable`). Kayıt tipini anahtar alan
    çalışma zamanı kayıtları (sürümler, onay, olaylar, toplanan alanlar...) alt tipin kayıtlarını üst tipin adıyla
    bulamaz; listeye yeni bir bildirim eklenmeden önce bu doğrulanmalıdır.
  - Sınırlar: tek düzey; üst tip ve alt tipler `MapCrud` olmalı ve aynı projede eşlenmeli; hiyerarşideki bir kaynak
    gömülü detay taşıyamaz; alt tipin kendi referansı, durumu veya trait'i olamaz; alt tipin alanları tabloda boş
    bırakılabilir kolonlardır; üst tipin adresinden oluşturulan kayıt üst tipin kendisidir (`kind` = üst tipin adı);
    başka bir kaynağın `Counts<Üst>()` toplamı alt tipin kayıtlarını saymaz.
- **Olay yayını.** `Traits.PublishesEvents` (`IPublishesEvents`; alanı yoktur). Üretilen modül
  `HaldusEvents.Register<T>("ad", item => {Entity}Endpoints.Describe(item))` çağırır; `Describe`, `ToResponse`'un
  içeriden çağrılan halidir, yani olayın verisi yanıt izdüşümünden gelir.
  - **Yazma interceptor'ın işidir** (`HaldusEvents.Capture`; sürümler ve değişiklik günlüğünden sonra): eklenen,
    değişen ve silinen her kayıt için bir olay. Yumuşak silme bir güncellemeye çevrildiği için `DeletedAt` alanının
    dolmasından tanınır ve `deleted` olarak yazılır. Geçişin kendi olayı için üretilen uç, kaydetmeden önce
    `HaldusEvents.Raise(context, entity, "ad")` çağırır; olay yine yakalama sırasında, kaydın son haliyle yazılır.
    Entity aksiyonu ayrı bir olay üretmez. Yayın kopyaları atlanır.
  - **Teslimat** `HaldusEvents.DeliverAsync` (bağlam başına) ve onu bütün bağlamlar için çağıran
    `HaldusEventDispatcher.RunAsync`'tir; arka plandaki `HaldusEventService` onu `EventPollInterval` aralığıyla ve her
    kaydetmeden sonra uyandırılarak (`Notify`) çalıştırır. Tek bir dağıtıcı çalışır (`HaldusOrdering` kilidi,
    `Events`); olaylar kimlik sırasıyla okunur, teslim edilen silinir, başarısız olanın `Attempts` / `NextAttemptAt` /
    `LastError` alanları yazılır ve tur orada durur. `MaxAttempts` aşılınca `DeadAt` dolar ve olay atlanır.
  - Kayıtlı yayıncı yoksa dağıtıcı hiçbir olaya dokunmaz (teslim edilmemiş olayı silmek onu kaybetmek olurdu).
    Entegrasyon testleri buna dayanır: asıl uygulamanın yayıncısı yoktur, her test kendi yayıncısıyla ikinci bir
    uygulama açar.
  - Yayın, veritabanı transaction'ı ve kilit tutulurken yapılır; yavaş bir yayıncı dağıtımı yavaşlatır.
  - Sınırlar: `MapCrud`, `MapReadOnly`, `MapAppendOnly` ile birleşir; değişiklik izleyicisini atlayan yazmalar
    (`ExecuteUpdate`, `ExecuteDelete`, toplanan alanların yazılması, bakımın temizliği) olay üretmez; olay kaydın
    bağlamda yüklü halini taşır (uygulamanın kendi kodu satırları yüklemeden yazdıysa satırlar olayda eksik olur);
    eşzamanlı transaction'ların olayları arasında sıra garanti edilmez (aynı kaydınkiler sıralıdır); vazgeçilen
    olaylar kendiliğinden silinmez ve yeniden kuyruğa alınmaz; olayların okunabildiği bir uç yoktur (çekerek izlemek
    için `ChangeFeed` vardır).
- **Şifreli alanlar.** `.Encrypted(...)` alanı hem `IsInputOnly` hem `IsEncrypted` yapar (yalnızca metin alanları;
  değilse `HALDUS010`); bu yüzden hassas alanın bütün kuralları ona da uygulanır. Yanıtta görünmesine bilerek izin
  verilmedi: yanıtı saklayan her yer (olay kutusu, idempotency kaydı, sürümler) onu düz metin olarak saklardı. Model
  alanı bir değer dönüştürücüsüyle şifreler (`HaldusEncryption.Configure`; izdüşümlerde de çalışır) ve kolonun uzunluk
  sınırını kaldırır (şifreli metin uzundur; sınır istekte doğrulanmaya devam eder). Koruyucu `IHaldusFieldProtector`
  servisidir (`AddHaldus` Data Protection uygulamasını kaydeder), model kurulurken bir kez çözülür ve amaç dizesi
  kaynağı ile alanı ayırır. Sınırlar: anahtar kaybolursa okuma 500 verir; var olan düz veriyi şifrelemez (alan sonradan
  şifreli yapılırsa eski satırlar okunamaz); kullanıcının filtre sınıfı alanı süzerse şifreli metinle karşılaştırır;
  uygulamanın kendi kodu alanı bir yanıta koyarsa Haldus engelleyemez.
- **Hassas alanlar.** `.Sensitive(...)` bağlayıcıda alanı `PropertyModel.IsInputOnly` yapar (içe aktarmanın içeriğiyle
  aynı bayrak): yanıt DTO'suna ve yanıt izdüşümüne (`ResourceEmitter.ResponseAssignments`) girmez, isteklerde kalır.
  İşaretleme arama, sıralama, doğal anahtar ve çeviri çözülmeden önce yapılır ve `FieldCandidates` böyle alanları
  aday saymaz (değerine göre aramak veya sıralamak onu sızdırırdı). Sürümleri saklanan kaynakta üretilen modül
  `HaldusRevisions.Hide<T>(...)` çağırır; alan anlık görüntüye girmez ve geri yükleme ona dokunmaz. Kopya ve yayın
  kopyası alanı sunucu tarafında taşır. Gömülü satırlarda da bildirilir (`DetailBuilder.Sensitive`): satırın alanı
  sahibin yanıtına (`DetailProjection`), anlık görüntüsüne ve geri yüklemesine girmez. **İsteği olduğu gibi saklayan
  veya geri gösteren bir özellik hassas alanla birleşemez:** `ChangeSets`, kaynağın veya gömülü satırlarının hassas
  alanı varsa `HALDUS007`'dir (güvenlik incelemesinde çıktı; küme sahnelenen gövdeyi saklayıp geri gösteriyordu).
  Yeni bir özellik istek gövdesini saklıyorsa aynı kontrol ona da eklenmelidir. Sınırlar: kullanıcının yazdığı filtre sınıfı (`IFilter<T>`) alanı süzmekte
  serbesttir; uygulamanın kendi kodu alanı bir yanıta koyarsa Haldus engelleyemez; `PUT` her alan gibi onu da ister.
  **Yanıt üreten yeni bir yol `ResponseAssignments` dışında alan saymamalıdır.**
- **Ölçüte göre silme.** `Traits.Purgeable`; `EmitPurgeMatching`. Ölçüt `ApplyFilter()` ile, liste ucundakiyle aynı
  koddan uygulanır ve kaynağın sorgu filtrelerinden geçer; silme `BatchDeleteItemAsync` çağrılarıdır (bu yüzden
  `Batch` ister ve sürüm kontrollü kaynakla birleşmez), hepsi tek bir `HaldusBatch` sınırındadır. İki koruma vardır
  (ikincisi ve boş metin kuralı güvenlik incelemesinden sonra eklendi): hiçbir ölçüt verilmediyse (boş veya yalnızca
  boşluk olan metin de verilmemiş sayılır) 400 döner; verilen ölçüt görünen bütün kayıtlara uyuyorsa (ör. `false`
  gönderilen bir bayrak, her kayda uyan bir değer) silme ancak `all=true` ile yapılır. Sayım modu ikisinden de önce
  neyin silineceğini gösterir. Yumuşak silinen kaynakta kayıtlar yumuşak silinir.
- **Yeniden çalıştırma.** `Traits.Rerunnable`; `RerunOfId` `ExtraMembers`'tadır. `EmitJobRerun` yeni kaydı
  `CreateInitializer(source: "source")` ile kurar (girdiler kopyalanır, çıktılar ve durum baştan başlar), referanslarını
  kayıttan okuyarak doğrular ve oluşturma ucundaki `Prepare` / kaydet / `DispatchAsync` sırasıyla teslim eder. İçe
  aktarma işiyle birleşmez (içeriği iş bitince boşaltılır). Yeni iş, isteği yapanın kimliği ve diliyle çalışır.
- **Değişiklik kümeleri.** `ResourceModel.ChangeSets` (`LifecycleReader`; durumlar `ReadChainStates` ile okunur: durum
  makinesi varsa sayılmaları zorunludur, çünkü küme kaydın düzenlenemediği durumlarda da çalışan bir yazmadır).
  Uçları `EndpointEmitter.ChangeSets.cs` üretir; saklama (`HaldusChangeSets`, tek bir `HaldusChangeSets` tablosu) ve
  JSON'un istek tipine çevrilmesi (`HaldusChangeSetRequests`) çalışma zamanındadır.
  - **Uygulama ve önizleme, kısmi güncelleme ucunun parçalarından kurulur** (`ChainGuard`, `WriteChecks`, `CycleCheck`,
    `ExpectVersion`, `PatchAssignments`, `SyncDetails`, `Compute`, `Save`); tek fark `StateGuard`'a `EditableIn` yerine
    kümenin durumlarının verilmesidir. `EmitWrite`'a eklenen bir kural buraya da eklenmelidir; paylaşılan parçaya
    konan kural kendiliğinden gelir.
  - Küme JSON olarak saklanır ve her kullanımda `Patch{Entity}Request`'e çevrilip yeniden doğrulanır; böylece
    "gönderilmedi" ile "null gönderildi" ayrımı korunur ve hazırlanırken geçerli olan içerik uygulanırken de
    doğrulanır. Sahneleme üst düzey alanları birleştirir (aynı alanın sonuncusu geçerlidir; gömülü satır listesi ve
    metadata çantası bütün olarak değişir).
  - Kümenin durumu eşzamanlılık belirtecidir ve küme, kayıtla aynı kaydetmede kapanır; birlikte gelen iki uygulama
    isteğinden ikincisi 409 (sürüm kontrollü kaynakta 412) alır. Bu yüzden uygulama `Save(always: true)` ile kaydeder.
  - Kümeyi bulan her uç önce kaydı kendi filtrelerinden geçirerek okur; küme tablosunun kiracısı yoktur, görünürlüğü
    kayıttan gelir. Başka bir kaydın kümesi bu kaydın adresinde bulunamaz.
  - Sınırlar: yalnızca `MapCrud` + `Patch` işlemiyle birleşir; kümenin toplu, ayrı uçlu detay ve komutla yazılan
    satır karşılığı yoktur; bir kaydın birden çok açık kümesi olabilir ve birbirlerinden habersizdirler; kümeler
    kendiliğinden silinmez ve kayıt silinince kalır; içerik en çok 1 milyon karakterdir; uygulama idempotency
    anahtarı almaz (kümenin durumu zaten ikinci uygulamayı durdurur).
- **Bakiye.** `Archetype.Balance` ana hattan geçer (ayrı bir üretici hattı değildir): anahtarı `MapStore` gibi istemci
  verir (`HasClientKey`), temel işlemleri liste ve tekil okumadır, kova alanları `ExtraMembers`'tadır (aranabilir,
  sıralanabilir) ve `Auditable` kendiliğinden eklenir. Komutları `EndpointEmitter.Balance.cs` üretir; kilit ve defter
  çalışma zamanındadır (`HaldusBalances`).
  - Her komut aynı sırayı izler: istek doğrulanır, bakiye kilitlenir (`HaldusBalances.BeginAsync`: kaynak + kiracı +
    anahtar; `HaldusOrdering` üzerinden, açık transaction varsa ona katılır), kayıt okunur veya izlenmeye başlanır
    (`FindOrStartAsync`), taban kontrolü yapılır, kova yazılır, defter kaydı eklenir (`Record`), tek kaydetme, commit.
    Reddedilen komut kaydetmeden döner; bu yüzden hiç yazılmamış anahtara gelen reddedilmiş istek kayıt oluşturmaz.
    **Bakiyeyi yazan yeni bir komut bu sırayı bozmamalı ve kovayı kilit dışında okumamalıdır.**
  - Taban kontrolü yalnızca bakiyeyi azaltan değişimde yapılır (eksideki bir bakiye artırılabilir); `AllowNegative`
    bildirildiyse kontrol hiç üretilmez. Taşıma tek kaydetmedir ve iki defter kaydı yazar.
  - Defterin kiracısı istekten değil bağlamdan okunur; kiracısı çözülemeyen okuma hiçbir kayıt göremez.
  - Zincirde yalnızca `MultiTenant`, `Idempotent`, `Sortable`, `Searchable`, `Filterable`, `Buckets`, `AllowNegative`,
    `Without` ve `Only` yazılabilir; gerisi `HALDUS007`'dir (bakiyeyi başka yoldan yazan veya gizleyen bildirimler
    onu bozardı). `Buckets` ve `AllowNegative` başka bir archetype'ta `HALDUS007`'dir.
  - **Tutma** (planlardan sonra; `BalanceModel.HoldFrom` / `HoldTo`, zincirde `.Holds(from, to)`). Kurallar çalışma
    zamanındadır (`HaldusBalanceHolds`: `PlaceAsync`, `CloseAsync`; tek bir `HaldusBalanceHolds` tablosu) ve bakiyenin
    kilidi altında, kova + defter + tutma kaydı tek kaydetmede yazılır. Üretilen kod kovaların okunup yazılmasını ve
    bakiyenin bulunmasını (`Read`, `Write`, `FindOrStartAsync`) delege olarak verir. Süresi dolan tutmaları bakım
    bırakır (`ReleaseExpiredHoldsAsync`; `RegisterBalanceHolds`), her birini kendi kiracısı adına (`HaldusAmbient`);
    kapatılamayanlar atlanır ki sırayı tutmasınlar. Süresi geçmiş tutma tahsil edilemez ama elle bırakılabilir.
    Ayrılan kova elle boşaltılmışsa süresi dolan tutma yine kapanır ve geri dönen miktar kovada kalanla sınırlanır.
    Sınırlar: bir bakiyenin en çok 1000 açık tutması olur; tutma kısmen tahsil edilemez; ömür en çok bir yıldır;
    kapanan tutmalar silinmez.
  - Sınırlar: bakiye silinemez; kullanıcının partial sınıfa eklediği alanları hiçbir uç yazmaz;
    defter kendiliğinden temizlenmez; miktarın ölçeği ve birimi yoktur (`decimal`); anahtar en çok 200 karakterdir;
    uygulamanın kendi kodu bakiyeyi doğrudan yazarsa deftere kayıt düşmez.
- **Durum eksenleri.** `ResourceModel.Axes` (`LifecycleReader`; `ReadWorkflow` ile okunur, alanı `ExtraMembers`'a
  durum alanı gibi girer). `ResourceLinker.AttachTransitions` geçişin eksenini durumlarının tipinden bulur
  (`TransitionModel.Axis`; asıl durumdaysa `null`); iki ayrı tipten durum taşıyan geçiş `HALDUS010`'dur. Üretilen
  geçiş yalnızca kendi eksenini denetler (`AxisGuard`) ve yazar; numara yalnızca asıl durumun geçişinde ayrılır.
  Eksen de adıyla saklanır ve, kaynak sürüm kontrollü değilse, eşzamanlılık belirtecidir (çakışma 409
  `StateChanged`); entegrasyon testi aynı geçişi birlikte isteyen iki istekten yalnızca birinin geçtiğini doğrular.
  Bütün belirteçler her güncellemenin koşuluna girdiği için, farklı eksenlerde tam aynı anda yapılan iki geçişten biri
  de 409 alabilir (yeniden denenir). Yeni kayıt, kopya dahil, her eksende başlangıç durumunda doğar. Asıl durum
  olmadan yalnızca eksen de bildirilebilir. Sınırlar: `MapCrud`, `MapReadOnly`, `MapAppendOnly` ile birleşir;
  `Publishable` ile birleşmez; eksenin `EditableIn` benzeri kuralı, bağlarda (`MapAssociation`) ekseni ve "bu durumda
  hangi geçişler yapılabilir" listesi (`supportedActions`) yoktur.
- **Çeviriler.** `Traits.Translatable` (`ITranslatable`; `Translations` trait alanıdır ve yanıtta yer alır), sayılan
  alanlar `ResourceModel.TranslatableFields` (bağlayıcıda çözülür: yazılabilir, gömülü olmayan `string` alanlar).
  Kurallar çalışma zamanındadır (`HaldusTranslations`: etiketin düzeltilmesi, daha genel dile düşme, 50 dil sınırı);
  çanta jsonb kolonunda metadata gibi metne çevrilerek saklanır (`HaldusDb.ConfigureTranslations`) ve her yazmada
  yeni bir çanta atanır.
  - Yazma uçları (`EmitTranslations`) güncelleme ucunun iskeletini kullanır: `ReadIfMatch`, `OwnerGuard`, `ChainGuard`,
    `StateGuard(EditableIn)`, `ExpectVersion`, `Save`. İstek tipi (`Translate{Entity}Request`) çevrilen alanların
    doğrulama attribute'larını `Required` dışında taşır; böylece çeviri de aynı filtreden geçer.
  - Okuma: `Localize(response, locale)` yanıt kurulduktan sonra uygulanır ve yalnızca liste, tekil okuma ve yayın
    okumalarında çağrılır. Yazma yanıtları, arama (`/search`), doğal anahtarla okuma ve sürümler kaynak dildedir.
    Geçersiz bir `?locale=` değeri hata değildir; dil istenmemiş sayılır.
  - Çeviri içeriktir: onaya bağlı kaynakta içerik alanı olarak bildirilir, anlık görüntüye girer ve geri yüklemede
    geri yazılır; aynı kaynağın başka bir kaydından doğan kayıt (kopya, yayın, düzeltme) çevirileri taşır
    (`CreateInitializer`).
  - Sınırlar: yalnızca `MapCrud` ile birleşir; çevirilere göre süzme, arama ve sıralama yoktur (hep kaynak dildeki
    metin üzerindendir); kaynak metin değişince çeviriler eskimiş sayılmaz; gömülü satırların ve gömülü değer
    nesnelerinin alanları çevrilemez; toplu ve içe aktarılan kayıtlar çevirisiz doğar.
- **Saklama süresi.** `ResourceModel.RetentionTicks` (`LifecycleReader`; süre derleme zamanında okunan bir sabit olmalı).
  Üretilen modül `HaldusMaintenanceRegistry.RegisterRetention<T>(süre)` çağırır; bakım, oluşturulalı süreden uzun
  olmuş kayıtları `ExecuteDelete` ile siler (sorgu filtreleri bilerek kaldırılır; silinmiş ve süresi dolmuş kayıtlar
  da gider). Başka bir kayıt referans veriyorsa veritabanı reddeder, uyarı günlüğe yazılır ve bir sonraki turda
  yeniden denenir. `MapCrud`, `MapReadOnly` ve `MapAppendOnly` ile birleşir; `MapFile` (içerik depoda sahipsiz kalırdı)
  ve `Publishable` ile birleşmez. Toplu silme değişiklik izleyicisini atladığı için sürümlere, değişiklik akışına ve
  toplanan alanlara yansımaz.
- **Okundu takibi.** `Traits.ReadTracking` (`IReadTracked`, `ReadAt`; trait alanıdır). `read` / `unread` uçları
  `EmitArchive` kalıbından geçer (`If-Match`, `Save`); `read-all` kaynağın filtrelerinden geçen tek bir
  `ExecuteUpdate`'tir, `unread-count` arşivlenmiş kayıtları saymaz. İkisinin de adı geçiş ve aksiyon adı olamaz
  (`ReservedRoute`). Okundu bilgisi içerik değildir: salt-okunur kaynakta da değişir, ama `Auditable` kaynakta
  `UpdatedAt` alanını ilerletir. `read-all` değişiklik izleyicisini atlar (akışa ve sürümlere girmez).
- **Okundu işaretleri.** `Traits.ReadReceipts`; entity'ye alan veya arayüz eklemez. Uçları
  `EndpointEmitter.ReadReceipts.cs` üretir, mantık çalışma zamanındadır (`HaldusReadReceipts`: tek tablo; kaynak tipi +
  kullanıcı + kayıt kimliği benzersizdir). Kayıt hiç yazılmaz, bu yüzden uçlar sürüm, onay ve düzenleme kurallarına
  bakmaz; ama kaydı her zaman kendi filtrelerinden geçirerek arar (göremeyen işaretleyemez ve durumunu okuyamaz) ve
  kullanıcısı çözülemeyen isteği reddeder. Liste ve sayım, kaydın anahtarının metin haliyle işaretlere bağlanır
  (`ReceiptKey`: `item.Id.ToString()`; sağlayıcı bunu çevirebilmelidir). Aynı anda gelen iki işaretleme benzersiz
  indekse takılır ve yutulur. Sınırlar: `read-all` istek başına 5000 kayıt işaretler; kimin okuduğunun listesi
  ("görenler") yoktur; kayıt silinince işaretleri kalır; `ReadTracking` ile birleşmez (adresleri aynıdır).
- **Toplanan alanlar.** Zincirdeki `Counts<T>()` / `Sums<T>(...)` `ResourceModel.Rollups` olur ve alanı
  `ExtraMembers`'a ekler (yalnızca yanıtta; aranabilir ve sıralanabilir). Alt kaynağı ve onun bu kaynağa işaret eden
  alanını `ResourceLinker.ResolveRollups` çözer (her tür referans olur: `References`, `Under`, `AttachedTo`,
  `Hierarchical`). Üretilen modül, alt kaynak başına bir `HaldusRollups.Register<TParent, TChild>` çağrısı yazar.
  - **Kural kaydetmeye bağlıdır, uçlara değil.** Interceptor kaydedilmek üzere olan alt kayıtların üst kayıtlarını
    not eder (`Collect`; eklenen, silinen ve yabancı anahtarı, toplanan alanı, `DeletedAt` veya `ExpiresAt` alanı
    değişen kayıtlar), açık bir transaction yoksa açar (`BeginAsync`) ve kaydetme bitince üst kaydı baştan sayar
    (`ApplyAsync`). Alt kayıt ile üst kaydın alanı böylece birlikte kalıcı olur. Üretilen uçlarda toplama kodu yoktur.
  - Sayım üst kayıt başına bir kilidin altındadır (`HaldusOrdering.BeginAsync`, `Rollup:{tip}` + anahtar); kilit
    olmadan aynı üst kayda birlikte yazan iki istek birbirinin kaydını göremezdi. Entegrasyon testi on iki eşzamanlı
    yazmayla doğrular. Kilitler tipe ve anahtara göre sıralı alınır.
  - Sayılan kayıtlar `HaldusRollups.Children<T>`'tır: kimliğe bağlı filtreler (kiracı, sahip, ebeveyn) kalkar, çünkü
    sayı alt kaydı yazanın neyi görebildiğine bağlı olamaz; silinmiş, süresi dolmuş ve yayın kopyası olan kayıtlar
    sayılmaz. **Bu yüzden kiracıya ait kayıtlar genel bir kayıtta toplanamaz** (`HALDUS007`): herkesin gördüğü alan
    her kiracının kaç kaydı olduğunu ötekilere gösterirdi. Kullanıcıya ait kayıtların (kendisi sahipli veya sahipli
    bir ebeveynin altında; `ResourceLinker.IsOwnerScoped`) o kullanıcıya ait olmayan bir kayıtta toplanması da aynı
    nedenle reddedilir; beğeni sayısı gibi bilerek istenen durumlar `acrossOwners: true` ile açıkça yazılır
    (güvenlik incelemesinde çıktı; ilk yazımda örtük olarak serbestti).
  - Alanlar `ExecuteUpdate` ile yazılır: üst kaydın `UpdatedAt` alanı, sürümleri, onayı ve değişiklik akışı
    etkilenmez (satır sürümü, xmin, ilerler). Üst kayıt `IComputed` ise ardından satırlarıyla yüklenir, `Compute()`
    çağrılır ve değiştiyse sıradan bir kaydetmeyle yazılır (o zaman bütün kurallar işler); eşzamanlılık çakışmasında
    kayıt yeniden okunup üç kez daha denenir (`FinishAsync`). Üst kayıt bağlamda izleniyorsa yeniden okunur.
  - Sınırlar: alt kayıt aynı projede eşlenmiş olmalıdır; toplanan alan null olamayan `decimal`, `int`, `long` veya
    `double` olmalıdır; `Publishable` ile birleşmez; değişiklik izleyicisini atlayan yazmalar (`ExecuteUpdate`,
    `ExecuteDelete`, bakımın temizliği, veritabanının zincirleme silmesi) toplamı yenilemez, bir sonraki izlenen
    yazma düzeltir; süresi dolan kayıt, başka bir yazma yeniden saydırana kadar toplamda kalır; her alt yazma üst
    kayıt başına iki-üç ek sorgudur ve aynı üst kaydın yazmaları sıraya girer.
- **Özel alanlar.** `Traits.CustomFields` (`IHasCustomFields`; çanta `Dictionary<string, JsonElement>`, jsonb).
  Tanımlar `HaldusCustomFieldDefinitions` tablosundadır (kaynağın tam tip adı + kiracı + anahtar; kiracıya bağlı
  olmayan kaynakta kiracı `Guid.Empty`'dir ki benzersiz indeks işlesin). Doğrulama çalışma zamanındadır
  (`HaldusCustomFields.ApplyAsync`): tanımsız anahtar, türüne uymayan değer ve eksik zorunlu alan
  `customFields.{anahtar}` adıyla alan düzeyinde 400 olur. Oluşturmada değerler kayıt kurulmadan önce doğrulanır
  (`CustomFieldCheck`, `WriteChecks` içinde) ve `CreateInitializer` onları oradan alır; güncellemede doğrulama
  atamanın içindedir (`CustomFieldAssignment`; `UpdateAssignments` ve `PatchAssignments` çağırır). Aynı kaynağın
  başka bir kaydından doğan kayıt (kopya, yayın, düzeltme, geri yükleme) değerleri doğrulamadan taşır. Özel alanlar
  içeriktir: onaya bağlı kaynakta değişmeleri onayı düşürür. Sınırlar: değerlere göre süzme, sıralama ve arama
  yoktur; tanım değişince var olan kayıtlar yeniden doğrulanmaz; `Under` ve `AttachedTo` ile birleşmez (tanımlar
  tek bir adreste yönetilir); kaynak başına en çok 100 alan.
- **Aktarma.** Zincirdeki `Exports<T>()` / `Imports<T>()` işe `ResourceModel.Transfer` verir; hedefi
  `ResourceLinker.AttachTransfers` çözer ve hedef kaynağı işaretler (`IsExported`, `IsImported`). Hedefin uç sınıfı
  işin çağıracağı herkese açık metotları kazanır (`EmitTransfers`): `ExportAsync` kayıtları kaynağın kendi
  filtrelerinden geçirip yanıt biçiminde verir, `ImportItemAsync` tek bir kaydı toplu oluşturmanın öğe yoluyla
  (`BatchCreateItemAsync`) oluşturur; bu yüzden içe aktarmanın hedefi `.Batch()` bildirmiş olmalıdır ve tekil
  oluşturmanın bütün kuralları içe aktarmada da geçerlidir. İşin mantığı üretilen bir handler sınıfıdır
  (`{İş}TransferHandler`, `EmitTransferHandler`) ve yazılmış handler'lar gibi kaydedilir. Biçimler, sınırlar ve
  hata raporu çalışma zamanındadır (`HaldusTransfers`).
  - İş, onu başlatan kiracı ve kullanıcının kimliğiyle çalışır (`HaldusAmbient`); dışa aktarma yalnızca onların
    görebildiği kayıtları içerir.
  - **İş, aktardığı kaynağın kapsamını taşımalıdır:** hedef kiracıya aitse (kendisi veya ebeveyni) iş de
    `MultiTenant`, hedef kullanıcıya aitse iş de `OwnedByCurrentUser` olmalıdır; değilse `HALDUS007`. İşin kaydı,
    hata raporu ve ürettiği dosya işi görebilen herkese açıktır; kapsamı olmayan bir iş bir kiracının verisini
    ötekilere gösterirdi (güvenlik incelemesinde çıktı).
  - İçe aktarma atomiktir: satırlar tek transaction içinde, her biri kendi kayıt noktasında işlenir; biri bile
    reddedilirse hepsi geri alınır. Bu, yarıda kalıp yeniden çalışan işin aynı kayıtları iki kez eklemesini de
    önler. İçerik (`Content`) yalnızca istekte yer alır (`PropertyModel.IsInputOnly`) ve iş bitince boşaltılır.
  - Dışa aktarılan CSV'de formül gibi başlayan metinlerin önüne kesme işareti konur (CSV enjeksiyonu). **Dışa
    aktarmaya yeni bir biçim eklenirse aynı koruma düşünülmelidir.**
  - Üretilen kodda uzantı metotları tam adıyla çağrılır (`TaskAsyncEnumerableExtensions.WithCancellation`):
    örtük `using`'ler kapalıyken `WithCancellation` `System.Linq`'teki yanlış uzantıya bağlanıyordu.
  - Sınırlar: dışa aktarma dosyayı bellekte kurar; üretilen dosyalar kendiliğinden silinmez; içe aktarma yalnızca
    kayıt oluşturur (güncellemez) ve ebeveyn altındaki kaynaklara yazamaz; ilerleme yalnızca içe aktarmada bildirilir.
- **Dosyalar.** `MapFile` ayrı bir archetype değil, `ResourceModel.File` taşıyan bir `Archetype.Crud`'dır
  (`MapCallParser.ArchetypeOf`); bu yüzden bütün trait'ler, ilişkiler ve toplu yollar onun için de geçerlidir.
  Alanları (`ContentType`, `Size`, `Checksum`, `Status`, `UploadedAt`, `StorageKey`) `LifecycleReader.FileMembers()`
  ile `ExtraMembers`'a girer, `FileName` yazılabilir bir alandır ve `Auditable` kendiliğinden eklenir. Sınırlar
  `MapFile` çağrısının sabit argümanlarından okunur (`LifecycleReader.ReadFile`).
  - Boyut ve tür sınırı, özetin hesaplanması ve depoyla konuşmak çalışma zamanındadır (`HaldusFiles`); uç kaydı
    bulur, yazma kurallarından geçirir (`If-Match`, `ChainGuard`) ve `Save()` ile kaydeder. Gövde okunurken sayılır
    ve sınır aşılınca okuma kesilir; `Content-Length` yalan söylese de sınır geçerlidir.
  - Depo `IHaldusFileStorage`'dır ve bağlamı parametre alır: veritabanı deposu içeriği dosya kaydıyla aynı
    kaydetmede yazar. İçerik, kayıt silindikten sonra kaldırılır (`SaveDelete` içinde `HaldusFiles.ReleaseAsync`).
    Dosya sistemi deposunda anahtarı Haldus üretir; yine de klasörün dışını gösteren anahtar reddedilir.
  - **İçerik her yüklemede yeni bir anahtara yazılır**; kayıt ancak içerik kabul edilince ona geçer ve eski içerik
    kayıt kaydedildikten sonra kaldırılır (`FileUpload.CompleteAsync`). Aynı anahtarın üzerine yazmak, reddedilen
    (boş veya çok büyük) bir yüklemenin var olan içeriği bozmasına yol açıyordu (güvenlik incelemesinde çıktı).
  - İndirilen içerik `attachment` olarak ve `X-Content-Type-Options: nosniff` ile gönderilir: tür yükleyenin
    beyanıdır. **İçeriği tarayıcıda açılacak biçimde (`inline`) sunan bir seçenek eklenirse tür beyaz listesi
    zorunlu olmalıdır.**
  - Yükleme ucuna `.Accepts(...)` eklenmez: o bildirim içerik türünü yönlendirmede süzer ve bizim 415 yanıtımız
    yerine boş bir 415 döner.
  - **Multipart.** `HaldusFiles.ReceiveAsync` gövdenin türü `multipart/form-data` ise formun ilk dosya parçasını
    içerik sayar (`MultipartReader`; akış halinde okunur): tür parçanın türüdür, boyut sınırı parçaya uygulanır, formun
    kendi yükü için 64 KB pay bırakılır. Yalnızca `PUT` ucundadır; `POST` eklenmedi, çünkü çerez tabanlı kimlik
    doğrulamada bir HTML formu başka bir siteden gönderilebilirdi (CSRF), `PUT` gönderilemez.
  - **İmzalı bağlantı** (`FileModel.SignedLinks`; `EmitFileLinks`). Bağlantı, kimliği ve üretildiği andaki içerik
    özetini taşıyan, Data Protection ile süreli korunmuş bir değerdir (`HaldusFileLinks`; amaç dizesi kaynağı
    ayırır). Üreten uç kaydı kendi filtrelerinden geçirerek arar; açan uç oturumsuzdur (`HaldusTokenAccess`
    metadatası, `AllowAnonymous`) ve yalnızca kiracı ile sahiplik filtrelerini adlarıyla kaldırır. İçerik değiştiyse
    (özet uymuyorsa) veya kayıt yoksa 404 döner. Sınırlar: bağlantı tek tek iptal edilemez (ancak içerik değişince
    veya süresi dolunca ölür); en çok bir gün yaşar; kaç kez açıldığı sayılmaz; `Under` ve `AttachedTo` ile birleşmez
    (bağlantının adresi kaynağın kendi koleksiyonundadır).
  - Sınırlar: dosya sistemi deposunda atomik toplu silme geri alınırsa içerik geri gelmez; veritabanı deposu
    içeriği belleğe alır; `Workflow`, `Document`, `SoftDeletable`, `Cloneable`, `Publishable`, zincir işlemleri ve
    `CreatedFrom` ile birleşmez; virüs taraması, görsel işleme ve devam ettirilebilir yükleme yoktur.
- **S3 deposu.** `Haldus.Storage.S3` (`AddHaldusS3Storage`; MinIO ve AWS S3, varsayılan yol biçimli adres). İstekler
  `HttpClient` ile gönderilir ve `S3Signer` ile imzalanır (AWS SigV4; AWS belgelerindeki örnek vektörlerle sınanır).
  Uzunluğu bilinen (aranabilir) içerik tek bir `PUT` ile akıtılır: HTTPS'te `UNSIGNED-PAYLOAD`, düz HTTP'de önce
  özeti hesaplanıp imzalanır. Uzunluğu bilinmeyen içerik (`HaldusFiles` her zaman böyle verir) `PartSize` kadar
  tamponlanır; tampona sığarsa tek `PUT`, sığmazsa çok parçalı yüklemedir ve bellekte en çok bir parça durur. Yarıda
  kalan yükleme iptal edilir ve özgün hata aynen yeniden fırlatılır (`HaldusFiles` boyut sınırını ondan 413'e
  çevirir). Ayarlar açılışta değil depo ilk kullanıldığında doğrulanır (build sırasındaki şema üretimi kimlik bilgisi
  olmadan çalışsın diye). Testler MinIO'yu genel Testcontainers kapsayıcısıyla başlatır; `minio/minio` imajı artık
  çekilemediği için `cgr.dev/chainguard/minio:latest` kullanılır (`HALDUS_TEST_MINIO_IMAGE` ile değiştirilir; sürüme
  sabit değildir). Sınırlar: gerçek AWS S3'e karşı denenmedi; geçici kimlik bilgisi (oturum token'ı, rol) yoktur;
  kovayı Haldus oluşturmaz; içerik kayıtla aynı transaction'da değildir (kayıt kaydedilemezse nesne sahipsiz kalabilir);
  indirmede `Range` desteklenmez; nesne deposuna doğrudan (ön imzalı) yükleme ve devam ettirilebilir yükleme yoktur.
- **Webhook yayıncısı.** `AddHaldusWebhookPublisher` (`HaldusWebhooks.cs`) olayları yapılandırılmış adreslere imzalı
  HTTP isteğiyle teslim eden bir `IHaldusEventPublisher`'dır. İmza Standard Webhooks düzenindedir: `Webhook-Id`
  (olayın kimliği), `Webhook-Timestamp`, `Webhook-Signature: v1,{base64(HMAC-SHA256(anahtar, "id.zaman.gövde"))}`;
  alıcı `HaldusWebhookSignature.Verify` ile doğrular (sabit süreli karşılaştırma, zaman toleransı, anahtar değişimi
  için birden çok imza). 2xx dışındaki yanıt hata sayılır ve olay kutudan yeniden denenir (en az bir kez; alıcı
  yineleneni `Webhook-Id`'den tanır). Yönlendirme izlenmez; adres `https` olmalıdır (`AllowInsecureHttp` yerel
  deneme içindir) ve anahtar en az 16 karakterdir (açılışta doğrulanır). **Adresler uygulamanın yapılandırmasından
  gelir; kullanıcıların tanımladığı adreslere gönderim (örnekteki `WebhookEndpoint` kaynağı gibi) SSRF koruması ister
  ve bu yayıncının işi değildir.** Sınırlar: birden çok adresten biri başarısız olursa yeniden denemede ötekilere de
  yeniden gönderilir; adres başına ayrı kuyruk ve devre kesici yoktur. Anahtar `whsec_…` biçimindeyse base64
  çözülerek kullanılır (belirtimin örnek vektörüyle sınanır). Yanıtın gövdesi okunmaz; hata metnine yalnızca
  sunucunun adı yazılır. **Yayıncının zaman aşımı bir `OperationCanceledException`'dır:** `HaldusEvents.DeliverAsync`
  ve dağıtıcı servis onu ancak kendi iptal belirteçleri iptal edilmişse dışarı bırakır. İlk yazımda her iptal
  istisnası dışarı çıkıyordu ve yanıt vermeyen bir alıcı arka plan servisini, onunla birlikte uygulamayı durduruyordu
  (incelemede çıktı; çekirdekteki bu kusur webhook'tan önce de vardı, `HttpClient` kullanan her yayıncıyı etkilerdi).
- **Takılabilirler.** `.AttachedTo<T>()` zincirde `Attached` işaretli bir `Parent` bildirimi olarak okunur ve
  `ReferenceKind.Attachment` olarak çözülür (isteğe bağlı, istekle yazılamayan yabancı anahtar). Hedef tarafında
  yazılan `Attachable` / `Commentable` bildirimleri (`ResourceModel.ReverseAttachments`) bağlayıcının ilk adımında
  çocuğun bildirimine çevrilir (`ResourceLinker.MergeReverseAttachments`); `Taggable` ise
  `HaldusGenerator.Emit` içinde bir `AssociationDeclaration`'a dönüşür.
  - **`Taggable` çalışma zamanında boş değildir:** bağın uçlarını o eşler (`ResourceBuilder.Taggable`), çünkü ayrı
    bir `MapAssociation` çağrısı yoktur. Bağın uçları kaynağın grubunun dışında, kökte durur; builder kendisine
    eklenen her kuralı (yetkilendirme, hız sınırı) saklar ve bu "yoldaş" gruplara da uygular, zincirde
    `Taggable`'dan önce de sonra da yazılsa. İlk yazımda uçlar üretilen `Map` metodunda köke eşleniyordu ve
    kaynağa eklenen yetkilendirme onlara ulaşmıyordu (güvenlik incelemesinde çıktı). **Bir kaynağın builder'ından
    eşlenen ama grubunun dışında kalan her yeni uç aynı yoldan (`_companions`) geçmelidir.**
  - `Attachable` / `Commentable` ise çalışma zamanında boştur: takılı kaynağın uçlarını kendi `Map` çağrısı
    (`app.MapFile<Attachment>()`) eşler ve kuralları da o çağrının builder'ı taşır; hedefin builder'ına eklenen
    kural takılı kaynağa uygulanmaz.
  - Uçlar hedef başına bir kez üretilir: `HaldusGenerator` kaynağın, o hedef tek ebeveyniymiş gibi bir kopyasını
    kurar ve `EndpointEmitter`'dan geçirir; sınıf adı (`{Kaynak}On{Hedef}Endpoints`) ve işlem adları (`WithName`)
    üretilen metinde hedefin adıyla değiştirilir. `EmitAttached` hepsini ortak bir grubun altında eşleyen giriş
    noktasını üretir. Entity, DTO'lar ve model bir kez üretilir.
  - Model: her hedef için yabancı anahtar, `HaldusFilters.Parent` filtresi (hedeflerden biri dolu ve görünür),
    `num_nonnulls(...) = 1` kısıtı. "Zincirleme silme kiracı sınırını aşmaz" kuralı burada da geçerlidir:
    hedeflerden biri kiracıya aitse kaynak kiracıya ait sayılır ve genel hedeflerin silmesi `Restrict` olur.
  - Takılabilir kaynağın tek bir adresi yoktur; bu yüzden `Under`, `Orderable`, `NaturalKey`, `ChangeFeed`,
    durum makinesi, `CreatedFrom`, `/me`, detay (`MapDetail`), builder'ında geçiş veya aksiyon, bir bağın ucu
    olmak ve başka bir kaynağın ebeveyni olmak `HALDUS007`'dir (`AttachmentProblem` ve ilgili bağlama adımları).
    `Hierarchical` birleşir: yanıtlanan kayıt aynı hedefe takılı olmalıdır.
- **Satırı değişen sahip.** Gömülü satır sahibinin parçasıdır. Üretilen uçlar satır değişince sahibi `Modified`
  işaretler; aynı kuralı, satırları başka bir kodun (aksiyonun veya geçişin mantığı, uygulamanın kendi kodu)
  değiştirdiği durum için interceptor uygular (`HaldusDetails.TouchOwners`; satır tiplerini üretilen modül
  `HaldusDetails.Register<TOwner, TDetail>(fk)` ile bildirir). Sahibe bağlı her kural (sürüm, `UpdatedAt`, anlık
  görüntü, onayın düşmesi, değişiklik günlüğü) buna dayanır; güvenlik incelemesinde, yalnızca satırları değiştiren
  bir aksiyonun onayı düşürmediği görülünce eklendi.
- **Sürümler ve değişiklik akışı.** İkisini de interceptor yazar (`HaldusRevisions.Capture`, `HaldusChanges.Capture`),
  trait alanları yazıldıktan sonra ve kaydı hangi kod yazarsa yazsın; üretilen uçlarda yakalama kodu yoktur. İkisi
  de anahtarın kaydetmeden önce bilinmesini ister (veritabanının ürettiği anahtarla birleşmezler) ve yayın
  kopyalarını atlar. Kayıtları tipin tam adı, anahtar ve (varsa) kiracı ile eşlenir.
  - **Anlık görüntü** EF modelinden okunur: gölge olmayan bütün skaler alanlar, complex property'ler ve kayıtlı
    satır koleksiyonları (`HaldusRevisions.Register<T>(koleksiyonlar)`), jsonb olarak. Okurken görüntüden izlenmeyen
    bir kayıt kurulur (`FindAsync`) ve yanıt her yanıt gibi `ToResponse` ile üretilir; bu yüzden yanıtta olmayan
    hiçbir alan dışarı çıkmaz. Görüntü yine de bütün alanları sakladığı için `SecretOnce` / `TokenAccess` ile
    birleşmez. Hiçbir şeyi değiştirmeyen yazma sürüm oluşturmaz (EF kaydı `Modified` saymaz).
  - **Geri yükleme** bir güncellemedir (`EmitRevisions`): güncellenebilir alanlar ve gömülü satırlar (kimlikleriyle:
    duran güncellenir, silinmiş olan aynı kimlikle eklenir, sürümde olmayan silinir) geri yazılır, geri gelen
    referanslar kayıttan okunarak yeniden doğrulanır, güncelleme korumaları (`ChainGuard`, `EditableIn`, `If-Match`)
    geçerlidir. Ağaçtaki yer (`ParentId`) geri yüklenmez. Sınırlar: kayıt silinince sürümleri kalır; satırın kendi
    referansı (varsa) yeniden doğrulanmaz; ayrı uçlu detay görüntüye girmez.
  - **Akış.** Günlük satırı kaydın kimin olduğunu da taşır (kiracı, sahip, ebeveyn): silinen kayıt sonradan
    sorulamaz, akışı okuyanın onu görüp göremeyeceği günlükten anlaşılır. Uç önce günlüğü isteği yapanın kapsamıyla
    okur (`HaldusChanges.ReadAsync`), sonra değişen kayıtları kaynağın kendi filtrelerinden geçirerek yükler;
    bulunamayan (silinmiş, yumuşak silinmiş, süresi dolmuş) kayıt `deleted` olarak döner. Ebeveyn altındaki
    kaynakta önce ebeveynin görünürlüğü doğrulanır (`ParentCheck`); kısa adresli (`shallow`) kaynakla birleşmez.
    İmleç günlükteki kimliktir; kimlik eklenirken, görünürlük transaction bitince belirlendiği için akış
    `HaldusChanges.SafetyLag` (varsayılan 2 sn) kadar geriden gelir ve yeterince eskimemiş ilk satırda durur.
    Bundan uzun süren bir transaction'ın satırları yine atlanabilir (bilinen sınır). `ExecuteUpdate` /
    `ExecuteDelete` gibi değişiklik izleyicisini atlayan yazmalar günlüğe ve sürümlere girmez.
- **Dönemli kayıtlar.** `Traits.EffectiveDated` (`IEffectiveDated`); iki alan `LifecycleReader.EffectivePeriod()` ile
  sıradan, yazılabilir alan olarak eklenir. Kural kaydetmenin kendisine bağlıdır: `EndpointEmitter.Effective(save)`
  `Save()`, `SaveCreated()` ve toplu oluşturmanın kaydetmesini sarar; kapsam kilitlenir (`OrderLock`, sıra ile aynı
  kilit), `HaldusEffectiveDating.ApplyAsync` dönemi kapsamdaki öteki kayıtlarla sınar ve hepsi birlikte kalıcı
  olur. **Dönemli bir kaynağı kaydeden yeni bir yol bu sarmalayıcıdan geçmelidir** (generator testi toplu
  oluşturmanın atlandığını yakalamıştı). Kapsam `SiblingsOf` + sayılan alanlardır. Veritabanı kısıtı yoktur
  (çakışmasız aralık kısıtı `btree_gist` eklentisi isterdi); koruma kilittir ve entegrasyon testi eşzamanlı
  yazmalarla doğrular. Silme serbesttir (araya boşluk bırakır).
- **Taslak ve yayın.** `Traits.Publishable`; `PublishedOfId` (yanıtta yok) ve `PublishedAt` `ExtraMembers`'tadır.
  Kopyaları gizleyen `HaldusFilters.Publication` filtresi modeldedir; kaynağa dokunan her sorgu yalnızca taslakları
  görür ve yalnızca yayın uçları (`EmitPublishing`) o filtreyi adıyla kaldırır. Kopya her yayında yeniden kurulur
  (önceki silinir, yenisi `CreateInitializer(source)` + `CopyRows` + `Compute` ile eklenir) ve dışarıya taslağın
  kimliğiyle gösterilir (`ToPublished`). Eşzamanlı iki yayından ikincisi benzersiz indekse
  (`UX_{Tablo}_PublishedOfId`) takılır ve 409 alır. Doğal anahtarın indeksi kısmidir (`PublishedOfId IS NULL`).
  Sınırlar: kopya yalnızca yazılabilir alanları ve gömülü satırları taşır (Haldus'un yönettiği alanları değil),
  bu yüzden `Workflow`, `Document`, `Numbered`, `Expiring`, sırlar, zincir işlemleri, `Approvable` ve
  `EffectiveDated` ile birleşmez; yayındaki kayıtlar `Filterable` filtresiyle süzülür ama aranamaz (`/search`) ve
  sıralanamaz; ayrı uçlu detaylar kopyaya girmez.
  - **Zamanlanmış yayın** (planlardan sonra; `ResourceModel.ScheduledPublishing`, zincirde `.ScheduledPublishing()`).
    `ScheduledAt` `ExtraMembers`'tadır; yalnızca `schedule` uçları ve yayının kendisi yazar. Kopyayı kuran parça
    (`EmitPublishing` içindeki `copy`) elle yayın ile bakımın çağırdığı `PublishDueAsync` arasında paylaşılır; **yayına
    yeni bir kural eklenirse o parçaya konmalıdır.** Üretilen modül kaynağı
    `HaldusMaintenanceRegistry.RegisterScheduledPublishing` ile kaydeder; bakım zamanı gelen taslakları bütün
    filtreleri kaldırarak bulur (`HaldusScheduledPublishing.DueAsync`; tur başına kaynak başına 100), sonra her
    taslağı kendi kiracısı ve sahibi adına (`HaldusAmbient`), filtrelerden geçirerek yeniden okur ve yayınlar.
    Zamanlamak taslağın bir yazmasıdır (`If-Match`, `Save`). Kendi filtrelerinden geçemeyen taslak (üst kaydı
    görünmeyen, silinmiş sayılan) hiç yayınlanamaz; zamanlaması düşürülür (`ClearAsync`). Tur, başarısız olanları
    atlayarak sıradakilere geçer (`skip: failed`; kaynak başına en çok 1000 deneme): yayınlanamayan birkaç taslak
    sıranın başını tutup başka kiracıların taslaklarını bekletemez (güvenlik incelemesinde çıktı). Sınırlar: yayın
    zamanından en çok bir bakım aralığı (`MaintenanceInterval`) sonra gerçekleşir; zamanlanmış yayından kaldırma
    yoktur; bakım sırasında çakışan taslak bir sonraki turda yeniden denenir.
- **Onay.** `Traits.Approvable` (`IApprovable`; alanları `TraitCatalog`'dadır), kuralları `ResourceModel.Approval`
  (`LifecycleReader.ReadApproval`). Kimin karar verebileceği ve kararın kayda nasıl yazıldığı çalışma zamanındadır
  (`HaldusApprovals.DecideAsync`); üretilen uç kaydı bulur, yazma kurallarından geçirir (`If-Match`, kapanmış kayıt
  koruması) ve `Save()` ile kaydeder. Kararlar `HaldusApprovalDecisions` tablosunda birikir (kaynak adı + anahtar
  ile; yabancı anahtar yoktur, kayıt silinince kararları kalır); tablo yalnızca onaya bağlı bir kaynak varsa
  modele girer.
  - **Onayı düşürmek uçların değil interceptor'ın işidir** (güvenlik incelemesinden sonra böyle oldu; uçlara
    yazılan sıfırlama aksiyonları ve uygulamanın kendi kodunu kaçırıyordu). Yeni kayıt karar bekleyerek doğar;
    değişen bir kaydın içerik alanlarından biri değiştiyse (`HaldusApprovals.ContentChanged`) onay düşer. İçerik
    alanlarını üretilen modül bildirir (`HaldusApprovals.Register<T>(...)`: kullanıcının alanları, üretilen
    yabancı anahtarlar ve `Metadata`); Haldus'un yönettiği alanlar (durum, numara, zaman damgaları) içerik değildir.
    Gömülü satır değişikliği sahibi `Modified` işaretlediği için içerik değişikliği sayılır. Önizleme
    kaydetmediği için kuralı kendisi uygular (`ResetIfChanged`). Onaylanmış kaydın güncelleme uçlarındaki kilidi
    `ChainGuard()` içindedir (`ApprovalLock`); geçişlerin onay koşulu `ApprovalGate`'tir ve geçen işlemi
    `HaldusApprovals.Exempt` ile işaretler ki o işlemin yazdığı alanlar onayı düşürmesin.
  - **Onay zinciri.** `ApprovalModel.Roles` (`LifecycleReader`; zincirdeki yeri önemli değildir). Üretilen uç rolleri
    ve `HaldusRoles.IsInRole(httpContext, role)` işlevini `DecideAsync`'e verir; karar (onay da ret de) sıradaki
    düzeyin rolüne sorulur ve rol sorulamıyorsa (servis yoksa) karar verilemez: bildirilmiş bir kural, denetlenemediği
    için atlanmaz. Rol, "içeriği yazan karar veremez" ve "aynı kişi iki düzeyi onaylayamaz" kurallarının yerine
    geçmez; onlardan sonra denetlenir. `IApprovalLevels.RequiredApprovalLevels` bildirilen düzey sayısına kırpılır ve
    her kararda kayıttan yeniden okunur (içerik değişince onay zaten düşer). Sınırlar: bir düzeyin birden çok rolü,
    paralel onay ("iki kişiden biri"), vekâlet ve düzeyin atlanması yoktur.
  - "Kendi yazdığını onaylayamaz" kuralı `Contributors` listesine bakar (içeriği yazmış herkes; kayıt boyunca
    birikir). Yalnızca son yazana bakmak, ikinci bir kişinin küçük bir düzenlemesinden sonra ilk yazanın kendi
    içeriğini onaylamasına izin veriyordu.
  - Karar kayıtları tipin tam adı, anahtar ve (varsa) kiracı ile eşlenir; kısa ad, aynı adlı iki kaynağın
    kararlarını karıştırırdı.
  - Karar, kararı verenin gördüğü içeriğe bağlanır: `ApprovalStatus`, `ApprovalLevel` ve her içerik yazmasında
    ilerleyen `RequestedAt` eşzamanlılık belirtecidir (sürüm kontrollü kaynakta satır sürümü yeter). Entegrasyon
    testi eşzamanlı onay ve reddin yalnızca birinin geçtiğini doğrular; durum belirteç olmadan ikisi de geçiyordu.
  - "Aynı kişi iki düzeyi onaylayamaz" kuralı, kaydın son `ApprovalLevel` onayına bakar; içerik değişince düzey
    sıfırlandığı için önceki turun onaylayanları yeniden onaylayabilir.
  - Sınırlar: onaya bağlı kaynağın ayrı uçlu detayı olamaz (`HALDUS007`; satır sahibinden bağımsız yazılırdı);
    `ExecuteUpdate` gibi değişiklik izleyicisini atlayan yazmalar onayı düşürmez; silme onaya bağlı değildir;
    onaylayanın yetkisi (rol) denetlenmez, yalnızca "içeriği yazmamış" ve "aynı kişi değil" kuralları vardır;
    onayın geri alınması yoktur. `MapCrud`, `MapAppendOnly` ve `MapReadOnly` ile birleşir.
- **Belge.** `Traits.Document`. `LifecycleReader` onu hazır bir `WorkflowModel`'e çevirir (durum tipi
  `Haldus.DocumentStatus`, `EditableIn` ve `DeletableIn` yalnızca `Draft`); `Workflow`, `EditableIn` ve `DeletableIn`
  ile birlikte yazılamaz. `submit` ve `cancel` geçişlerini, kullanıcı aynı adla yazmadıysa,
  `ResourceLinker.AttachTransitions` ekler (`TransitionModel.IsImplicit`); böyle bir geçiş çalışma zamanındaki
  kayda girmez, kaynağın `Map` metodunda standart uçlarla birlikte eşlenir. Düzeltme üçüncü zincir işlemidir
  (`ChainOperation.Amend`: `AmendedFromId` / `AmendedById`, yalnızca `Cancelled` durumunda) ve `EmitChain`'den
  geçer; zincir işlemlerinin bütün kuralları (anahtar veritabanında üretilemez, bir kayıt bir kez düzeltilir,
  çiftin parçası silinemez, sırlarla birleşmez) onun için de geçerlidir; bu yüzden düzeltme taslağı da silinemez
  (vazgeçilen düzeltme gönderilip iptal edilir). Yalnızca `MapCrud` ile birleşir.
- **Bağlar ve kapsam.**
  - **Bağın yükü ve durumu** `OperationParser.ParseAssociation` içinde okunur: bağ sınıfı derlemede varsa
    (`LinkDeclared`) skaler alanları yük olur, zincirdeki `Workflow` `LifecycleReader.ReadWorkflow` ile okunur.
    Bağın geçişleri `TransitionParser.ParseLink` ile okunur (`TransitionDeclaration.IsLink`; sahibinin adı iki ucun
    adından kurulur), kaynak geçişlerinden ayrılır ve `ResourceLinker.AttachLinkTransitions` ile bağlanır.
    `AssociationBuilder.MapTransition` çalışma zamanında boştur; uç, bağın üretilen `Map` metodunda eşlenir.
    Yükü veya durumu olan bağ (`AssociationModel.HasResponse`) kendi yanıt tipini döndürür ve topluca
    değiştirilemez; `Status` eşzamanlılık belirtecidir (çakışma 409 `StateChanged`). Sınırlar: bağın `EditableIn` /
    `DeletableIn` kuralı yoktur (yük her durumda yazılır, bağ her durumda kaldırılır), geçişi iş mantığı taşımaz,
    bağın uçları idempotency ve token ile erişim almaz. Sağ ucun anahtarı metinse (`MapLookup`) `links` kodlu kayıt
    `…/links` adresiyle çakışır.
  - **Topluca değiştirme** (`AssociationEmitter.EmitReplace`): listedeki her kayıt isteği yapana görünür olmalıdır
    (biri bile yoksa 400 ve hiçbir şey değişmez); isteği yapana görünmeyen bağlara dokunulmaz (bağın sorgu filtresi).
    En çok 1000 kimlik. Tek kaydetmedir ama kilitsizdir: aynı ucu aynı anda değiştiren iki isteğin sonucu ikisinin
    birleşimi olabilir.
  - **Ebeveyn altında tek kayıt.** `MapSingleton().Under<T>()`: anahtar ebeveynin yabancı anahtarıdır
    (`ModuleEmitter`), okuma ve yazma önce ebeveyni arar (yoksa 404), hiç yazılmadıysa varsayılanlar ebeveynin
    kimliğiyle döner. `MultiTenant` ile birleşmez (kiracı ebeveynden gelir); `shallow: true` alamaz.
  - **Kısa adres.** `ReferenceDeclaration.Shallow` → `ResolvedReference.IsShallowParent`, `ResourceModel.ShallowParent`.
    Bağlayıcı onu `ReferenceKind.Reference` olarak çözer (alan istekte yazılır, `IsCreateOnly`); ebeveynin anlamı
    modelden gelir: `HaldusFilters.Parent` filtresi, `Cascade` silme, doğal anahtarın ve kardeş sırasının
    (`Siblings`) kapsamı. Bir kaynağın ebeveynine bakan yeni kod `resource.Parent ?? resource.ShallowParent` yazmalıdır.
  - **Komutla yazılan detay.** `ResourceModel.IsCommandWritten`; `EndpointEmitter.EmitDetailCommands` üç ucu üretir.
    Her komut sahibi satırlarıyla yükler ve sahibin yazma kurallarından geçer (`If-Match`, `OwnerGuard`,
    `ChainGuard`, `EditableIn`, `Compute`, `Save`). Satırlar güncelleme isteklerine ve `Sync…` yardımcılarına
    girmez; oluşturma isteğinde ve kopyada yer alır. Komutun adresi (`{satırlar}`) geçiş ve aksiyon adı olamaz
    (`ReservedRoute`). Sınırlar: komutların toplu biçimi ve önizlemesi yoktur; satırın sırası eklenme sırasıdır,
    araya ekleme yoktur.
- **Yanıt izdüşümü tek yerdedir:** `ResourceEmitter.ResponseAssignments`. Bir kaynağın yanıtını döndüren her
  üretici (kendi uçları, bağ listeleri) onu kullanır; elle yazılan bir izdüşüm, yanıta sonradan eklenen zorunlu
  alanlarda (durum, numara, gömülü satırlar) derlenmez.
- **Modüller ve bağlamlar.** Hangi modülün hangi bağlamı kullandığını `HaldusContextOptions` tutar
  (`DefaultContext`, `ModuleContexts`, `Modules`). `HaldusDbContextProvider.For<TEntity>()` entity'nin
  assembly'sine bakar; `HaldusModelRegistry.Apply` her bağlama yalnızca ona bağlı modüllerin katkısını
  uygular (bağlam Haldus servisleri olmadan kurulduysa hepsini). Bir modüle ait olmayan tablolar (ör. iş
  kuyruğu) `RegisterGlobal` ile kaydedilir ve hangi bağlama gireceklerine kendileri karar verir.
  - Kayıt defterleri (`HaldusModelRegistry`, `HaldusServiceRegistry`, `HaldusMaintenanceRegistry`...) süreç
    genelinde statiktir, ama bir uygulama yalnızca kendi modüllerini görmelidir: ortak bağlam ve handler
    kaydı, `AddHaldus` sırasında bağlamın assembly'sinden başvurularla bulunan modüllerle (`Modules`)
    sınırlanır. Aynı süreçte iki uygulama çalıştıran testler buna dayanır; statik bir kayıt defterini
    gezen yeni kod da aynı süzmeyi yapmalıdır.
  - Modülün üretilen kayıt kodu bir module initializer'dır ve assembly ilk kullanıldığında çalışır. Ana
    proje `AddHaldus` çağırdığında modül henüz kullanılmamış olabilir; bu yüzden `LoadModules` onu
    `RunModuleConstructor` ile çalıştırır. Bunu yapmadan handler sınıfları kaydedilmezdi.
  - Generator başka bir assembly'deki entity'yi kaynak olarak değil derlenmiş tip olarak görür: anahtarını
    `Id` / `Code` / `Key` özelliğinden, kiracıya bağlı olup olmadığını `IMultiTenant`'tan okur
    (`ReferenceDeclaration.IsExternal`). Üretilen model, böyle bir referans için kısıtı
    `HaldusModel.Shares<T>(context)` doğruysa kurar; ebeveyn için `HaldusModel.RequireShared` çağırır.
  - Tablo adı üretilen modelde açıkça verilir (`ToTable`). EF Core adı bağlamdaki `DbSet` özelliğinden alır;
    başka modülden gelen entity için o özellik yoktur ve tablo tekil adla oluşurdu. DbSet özellikleri
    yalnızca bağlamla aynı projede üretilen entity'ler için eklenir; diğerlerine `Set<T>()` ile ulaşılır.
  - Fiziksel silme, veritabanının reddini her zaman 409 `InUse` olarak bildirir. Bir modül, kendisine başka
    bir modülden (veya elle yazılmış bir ilişkiden) referans verildiğini bilemez; bu yüzden "referans
    veriliyor mu" bilgisine dayanan eski koşul kaldırıldı.
  - Sınırlar: PostgreSQL iş kuyruğu tek bir bağlamdadır; işleri başka bağlamda duran bir modül onu kullanamaz
    (açık bir hata verir). Ayrı bağlamlar arasında ortak transaction yoktur.
- Haldus'un kendi tabloları (`HaldusCounters`, `HaldusIdempotencyRecords`) yalnızca onları kullanan bir
  kaynak varsa modele eklenir; ekleyen, üretilen modül kodudur.
- Tanılar `HALDUS001`+ kodlarıyla `Diagnostics/HaldusDiagnostics.cs` ve `AnalyzerReleases.Unshipped.md`
  dosyalarında birlikte tutulur.

## Kurallar

- Commit'ten önce `dotnet test` çalıştır ve commit'i sonucuna bağla; test çıktısına bakmadan zincirleme
  commit atma. Bekleyen `.received.cs` dosyası varken de commit atma.
- Üretilen kodda ve kütüphanelerde kod İngilizce, yorumlar Türkçe.
- Generator testleri üretilen çıktının hatasız derlendiğini de doğrulamalıdır.
- .NET 11 yayınlandı ama LTS değil; paketler 10.x hattında tutulur.
- Derleme uyarıları hatadır; buna NuGet'in güvenlik açığı uyarıları da dahildir. Dolaylı bir bağımlılık
  yüzünden derleme durursa uyarıyı kapatma, paketi yamalı sürüme sabitle.
