---
paths:
  - "src/**"
  - "tests/Haldus.Generators.Tests/**"
  - "tests/Haldus.IntegrationTests/**"
---

# Generator'ın işleyişi: alanlar, kayıt işlemleri, sırlar

Generator'ın iç işleyişi; dört dosyaya (`generator-*.md`) bölünmüştür. Kalın yazılmış "…yeni bir … eklenirse" cümleleri kuraldır.

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
