---
paths:
  - "src/**"
  - "samples/**"
  - "tests/**"
  - "docs/**"
  - "reports/**"
---

# Tasarım kararları

Bunlar kullanıcıyla kararlaştırıldı; yeniden sorma. Yeni bir kullanıcı kararı da buraya yazılır.

## Model

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
    sistemi (`AddHaldusFileSystemStorage`). Nesne depoları `IHaldusFileStorage` uygulanarak eklenir; çekirdek onlar
    için paket taşımaz, S3 uyumlu depo ayrı bir pakettir (`Haldus.Storage.S3`).
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
    sonra uygulamanın kaydettiği `IHaldusEventPublisher` servislerine teslim edilir. Çekirdek bir mesaj kuyruğu
    paketi taşımaz; kuyruğa, webhook'a ya da başka bir modüle taşımak yayıncının işidir. Hazır yayıncılar ayrı
    paketlerdir (SQS, RabbitMQ, Kafka) ve webhook yayıncısı çekirdektedir.
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
- Süresi dolmuş `Expiring` kayıtları varsayılan olarak silinmez (`HaldusOptions.PurgeExpiredAfter` boş).
  Bu varsayılanı kullanıcı ayrıca onaylamadı; kullanıcı verisini kendiliğinden silmemek için böyle seçildi.
- `MultiTenant` kiracıyı token'daki claim'den okur.

## Proje tipleri

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

## Teknoloji

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
