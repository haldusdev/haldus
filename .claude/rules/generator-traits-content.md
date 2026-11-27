---
paths:
  - "src/**"
  - "tests/Haldus.Generators.Tests/**"
  - "tests/Haldus.IntegrationTests/**"
---

# Generator'ın işleyişi: içerik, durum ve zaman

Generator'ın iç işleyişi; dört dosyaya (`generator-*.md`) bölünmüştür. Kalın yazılmış "…yeni bir … eklenirse" cümleleri kuraldır.

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
  yazmalarla doğrular. Silme serbesttir (araya boşluk bırakır). `EffectiveFrom` zorunludur: istek tiplerinde
  `Required` ve `HasValue` taşır (`LifecycleReader.RequiredValue`), çünkü gönderilmeyen değer tipli alan `default`
  olur ve `Required` onu yakalamaz; ilk yazımda gönderilmeyen başlangıç 0001-01-01 sayılıyordu (belge yazılırken
  çıktı). Tekrar serisinin `StartsAt` alanı da aynı kuralla zorunludur. **Üretilen, zorunlu, değer tipli yeni bir
  alan aynı ikiliyi taşımalıdır.**
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
