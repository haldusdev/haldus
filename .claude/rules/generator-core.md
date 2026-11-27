---
paths:
  - "src/**"
  - "tests/Haldus.Generators.Tests/**"
  - "tests/Haldus.IntegrationTests/**"
---

# Generator'ın işleyişi: çekirdek

Generator'ın iç işleyişi; dört dosyaya (`generator-*.md`) bölünmüştür. Kalın yazılmış "…yeni bir … eklenirse" cümleleri kuraldır.

## Ayrıştırma, bağlama, görünürlük

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
  ilk istekte değil uygulama açılırken verilir. İkisinde de mantığın fırlattığı `TransitionRejectedException` 422 olur
  (`HaldusRejectionFilter`; geçişlerdekiyle aynı yanıt). Belge yazılırken çevrilmediği görülmüş, sonra eklenmiştir.
- Eşzamanlılık kontrollü kaynakta durum değiştiren her uç (`restore`, `purge`, `archive` dahil) `If-Match`
  ister; yeni bir uç eklerken `ReadIfMatch` / `ExpectVersion` / `Save` üçlüsünü kullan.
- Üretilen kod çalışma zamanına bir module initializer ile bağlanır: `HaldusEndpointRegistry` (entity →
  endpoint eşleyici) ve `HaldusModelRegistry` (assembly → model katkısı). `MapCrud<T>()` çalışma
  zamanında yalnızca bu kayda bakar.
- Kullanıcı `Id` yazmadıysa `Guid` (v7) üretilir; yazdıysa `Guid`, `int` veya `long` olmalıdır.
- DTO'lara yalnızca skaler alanlar girer; herkese açık setter'ı olmayan alanlar yalnızca yanıtta yer alır.
- `AddHaldus<TContext>()` aynı projedeki partial bir bağlamı gösteriyorsa DbSet özellikleri eklenir.

## Yaşam döngüsü, geçişler, numaralama, idempotency

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

## Toplu uçlar, bakım, kapsam, sahiplik

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
- **Kapsam metadatası.** Üretilen her uç grubu, kaynağın kayıtlarının kime ait olduğunu metadata olarak taşır
  (`HaldusResourceScope(tenantScoped, ownerScoped)`; `EndpointEmitter`, `AssociationEmitter`, `UserLinkEmitter`).
  Uygulama yetki kurallarını buna göre yazar; örnek mağazanın "ortak kaynakları yalnızca personel yazar" kuralı
  buna dayanır. Kapsam kaynağın kendisinden (`MultiTenant`, `Owned`) veya ebeveyninden gelir: bağlayıcı ebeveynin
  kapsamını referansla birlikte taşır (`ResolvedReference.TargetTenantScoped` / `TargetOwnerScoped`; `Under`, kısa
  adresli ebeveyn ve `AttachedTo` hedefleri için), `ResourceModel.TenantScoped` / `OwnerScoped` ondan hesaplanır.
  Takılabilir kaynağın uçları hedef başına üretildiği için kapsamı da hedef başınadır. Bağın kapsamı uçlarından
  birininkidir; kullanıcıya bağlı bağ her zaman kullanıcıya aittir. **Kayıtların kime ait olduğunu değiştiren yeni bir
  bildirim bu iki özelliğe yansıtılmalıdır;** yoksa uygulamanın yetki kuralı o kaynağı yanlış sınıflar. Sınırlar:
  token ile erişilen (`TokenOnly`) kaynak "ortak" görünür (uçları zaten oturumsuzdur); `MapAction` / `MapQuery` gibi
  entity'ye bağlı olmayan uçlar metadata taşımaz (bir yetki kuralı onları kapsamı bilinmeyen, yani en kısıtlı sınıfta
  saymalıdır). Gövde taşıdığı için `POST` olan ama yazmayan uçlar ayrıca `HaldusReadOnly` ile işaretlenir (`/search`,
  `/batch-get`, `/preview`, `/{id}/preview`). **Yazmayan yeni bir `POST` ucu üretilirse o da işaretlenmelidir; yazan bir
  uç asla işaretlenmemelidir.** İki istisna grubun kapsamını uç düzeyinde değiştirir: okundu işaretini yazan uçlar
  (`/read`, `/unread`, `/read-all`) her zaman kullanıcıya aittir (kayıt ortak olsa da işaret işaretleyenindir; yoksa
  ortak bir duyuruyu yalnızca personel "okundu" işaretleyebilirdi), ve içe aktarma işi kayıt eklediği kaynaktan daha
  dar bir kapsam bildiremez (kiracıya ait bir iş ortak kaynağa yazıyorsa ortak sınıflanır). Bilinen sınır:
  `CreatedFrom(on:)` ile bir geçişin başka bir kaynakta doğurduğu kayıt, geçişin kaynağının kapsamıyla yazılır.
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

## Bağlar, yanıt izdüşümü, modüller, tanılar

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
