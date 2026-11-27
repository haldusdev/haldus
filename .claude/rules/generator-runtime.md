---
paths:
  - "src/**"
  - "tests/Haldus.Generators.Tests/**"
  - "tests/Haldus.IntegrationTests/**"
---

# Generator'ın işleyişi: çalışma zamanı altyapısı

Generator'ın iç işleyişi; dört dosyaya (`generator-*.md`) bölünmüştür. Kalın yazılmış "…yeni bir … eklenirse" cümleleri kuraldır.

## İşler

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

## Olay yayını

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

## Aktarma, dosyalar, depolar, yayıncılar, takılabilirler

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
    kaydetmede yazar.
  - **Silinen dosyanın içeriğini kaldırmak uçların değil kaydetmenin işidir** (`HaldusFileCleanup`; interceptor ve bir
    transaction interceptor'ı). Kaydetmeden önce silinen `IHaldusFile` kayıtları toplanır; veritabanının zincirleme
    sileceği, bağlamda izlenmeyen dosya kayıtları da EF modelindeki `Cascade` yabancı anahtarlar izlenerek (en çok sekiz
    ilişki, tek kolonlu anahtarlar) ham SQL ile bulunur; izlenen ve silinmeyen dosyaların anahtarları çıkarılır.
    Veritabanı deposunda içerik satırları aynı kaydetmeye silinmek üzere eklenir (`RemoveWithAsync`); başka depoda
    anahtarlar silme kalıcı olunca kaldırılır: açık transaction yoksa kaydetmeden hemen sonra, varsa commit'te.
    Geri alma ve kayıt noktasına dönme (EF Core'un kendi kayıt noktaları ve `HaldusBatch` dahil) bekleyenleri düşürür;
    kayıt noktası adları olay verisinde olmadığı için bir yığınla izlenir ve dönülen kayıt noktası orada biter.
    **Bekleyen anahtarlar ait oldukları transaction'a bağlıdır** (`TransactionId`) ve yalnızca onun commit'i içeriği
    kaldırır; başka bir transaction'ın olayı veya transaction'sız bir kaydetme onları geri alınmış sayıp atar. Commit
    edilmeden atılan transaction olay vermez; ilk yazımda onun bekleyenleri, sonraki tek komutluk (transaction açmayan)
    kaydetmede siliniyor ve geri gelen kayıtların içeriği kayboluyordu (otomatik güvenlik incelemesinde çıktı).
    Depodan kaldırma commit'ten sonra başarısız olursa günlüğe yazılır, istek başarısız sayılmaz. İlk yazımda içerik
    yalnızca dosyanın kendi silme ucunda kaldırılıyordu: zincirleme ve uygulamanın kendi koduyla silinen dosyaların
    içeriği depoda sahipsiz kalıyor, dosya sistemi deposunda geri alınan toplu silme içeriği geri getirmiyordu (belge
    yazılırken çıktı). `ExecuteDelete` bunu atlar; `HaldusFiles.ReleaseAsync` o durumda elle kullanılır.
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
  - Sınırlar: veritabanı deposu içeriği belleğe alır; `Workflow`, `Document`, `SoftDeletable`, `Cloneable`, `Publishable`, zincir işlemleri ve
    `CreatedFrom` ile birleşmez; virüs taraması, görsel işleme ve devam ettirilebilir yükleme yoktur.
- **S3 deposu.** `Haldus.Storage.S3` (`AddHaldusS3Storage`; MinIO ve AWS S3, varsayılan yol biçimli adres). Servisle
  AWS SDK konuşur (`AmazonS3Client`); `ServiceUrl` verilmezse AWS'nin bölgedeki adresi kullanılır. Erişim anahtarı
  verilmezse kimlik bilgisi SDK'nın varsayılan zincirinden okunur (ortam, profil, rol); `SessionToken` geçici kimlik
  bilgisi içindir. Uzunluğu bilinen (aranabilir) içerik tek bir `PutObject` ile akıtılır (HTTPS'te gövde imzaya
  girmez). Uzunluğu bilinmeyen içerik (`HaldusFiles` her zaman böyle verir) `PartSize` kadar tamponlanır; tampona
  sığarsa tek `PutObject`, sığmazsa çok parçalı yüklemedir ve bellekte en çok bir parça durur. Çok parçalı yükleme
  SDK'nın `TransferUtility`'siyle değil kendi döngümüzle yapılır: yarıda kalan yükleme iptal edilir ve içeriği
  okurken çıkan özgün hata aynen yeniden fırlatılır (`HaldusFiles` boyut sınırını ondan 413'e çevirir). Servisin
  reddi `HaldusS3StorageException` olur (durum ve hata kodu; SDK'nın istisnası iç istisnadır). Özel bir adrese
  (`ServiceUrl`) karşı SDK'nın yeni varsayılan bütünlük özetleri kapatılır (`WHEN_REQUIRED`): S3 uyumlu servislerin
  hepsi onları tanımaz. İstemcinin öteki ayarları `ConfigureClient` ile değiştirilir. Ayarlar açılışta değil depo ilk
  kullanıldığında doğrulanır (build sırasındaki şema üretimi kimlik bilgisi olmadan çalışsın diye). Testler MinIO'yu
  genel Testcontainers kapsayıcısıyla başlatır; `minio/minio` imajı artık çekilemediği için
  `cgr.dev/chainguard/minio` kullanılır, özetiyle (`@sha256:…`) sabitlenmiş olarak (`HALDUS_TEST_MINIO_IMAGE` ile
  değiştirilir; kayıt defteri o özeti kaldırırsa testler imajı çekemez). Sınırlar: gerçek AWS S3'e karşı denenmedi;
  kovayı Haldus oluşturmaz; içerik kayıtla aynı transaction'da değildir (kayıt kaydedilemezse nesne sahipsiz
  kalabilir); indirmede `Range` desteklenmez; nesne deposuna doğrudan (ön imzalı) yükleme ve devam ettirilebilir
  yükleme yoktur.
- **Kuyruk yayıncıları.** Üçü de aynı biçimdedir: ayarlar yayıncının kurucusunda doğrulanır (geçersiz ayar arka plan
  servisini düşürür ve uygulama açılmaz), bağlantı ilk olayda kurulur, `EventTypes` gönderilecek türleri süzer
  (süzülen olay teslim edilmiş sayılır), gövde webhook yayıncısının gönderdiği olayla aynı JSON'dur ve zaman aşımı
  bir `TimeoutException`'a çevrilir (dağıtıcı onu sürecin kapanması sanmasın diye). Teslimat en az bir kezdir;
  tüketici yineleneni olayın kimliğinden tanır. Uygulama başına tek bir hedef kaydedilir ve gönderim, dağıtıcının
  kilidi altında, olay başına bir istektir.
  - **SQS** (`Haldus.Events.Sqs`, `AddHaldusSqsPublisher`; `AmazonSQSClient.SendMessageAsync`). Öznitelikler
    `EventId`, `EventType`, `Resource`. FIFO kuyrukta grup kimliği `{kaynak}:{kayıt}`, yineleme kimliği
    `{kaynak}:{kayıt}:{olay}`'dır (olay kimliği yalnızca veritabanı başına benzersizdir; ayrı bağlamlı iki modül aynı
    kuyruğa yazarsa çakışırdı); 128 karakteri veya yazdırılabilir ASCII'yi aşan kimliklerin SHA-256'sı kullanılır.
    Servisin reddi `HaldusSqsException` olur: kuyruğun adını, durumu ve hata kodunu taşır, servisin açıklamasını
    taşımaz; `MessageId` taşımayan yanıt da hatadır. Kimlik bilgisi S3 deposundaki gibi verilir. Testler ElasticMQ'yu
    özetiyle sabitlenmiş imajdan başlatır (`HALDUS_TEST_ELASTICMQ_IMAGE`). **Öykünücü imzayı doğrulamaz; gerçek AWS
    SQS'e karşı sınanmadı.** Sınırlar: mesaj boyutu denetlenmez (sınırı aşan olay on denemeden sonra bırakılır ve bu
    sırada arkasındakileri bekletir); FIFO yineleme penceresi beş dakikadır, kutunun yeniden deneme aralığı da beş
    dakikaya çıkabildiği için geç bir yeniden deneme yinelenebilir.
  - **RabbitMQ** (`Haldus.Events.RabbitMq`, `AddHaldusRabbitMqPublisher`; `RabbitMQ.Client` 7). Mesaj kalıcıdır ve
    kanal onay izlemeyle açılır: `BasicPublishAsync` aracının onayını bekler, ret ve hiçbir kuyruğa yönlendirilemeyen
    mesaj (`mandatory`) hata olur. Yönlendirme anahtarı verilmezse olayın türüdür (topic exchange'de tüketiciler türe
    göre bağlanır); varsayılan exchange'de anahtar hedef kuyruğun adı olmalıdır. Özellikler: `message-id` (olayın
    kimliği), `type`, başlıklarda `resource` ve `record-id`. Bağlantının kendiliğinden kurtarılması kapalıdır: hatadan
    sonra kanal ve bağlantı atılır, bir sonraki olay yenisini kurar. Exchange, kuyruk ve bağları Haldus oluşturmaz.
    Testler gerçek bir aracıya karşıdır (`HALDUS_TEST_RABBITMQ_IMAGE`), TLS dinleyicisi dahil: güvenilen sertifikayla
    gönderim geçer, güvenilmeyen sertifika varsayılan olarak reddedilir. Sınır: aynı kaydın olaylarının sırası tek
    tüketicili bir kuyrukta korunur, tüketiciler arasında korunmaz.
  - **Kafka** (`Haldus.Events.Kafka`, `AddHaldusKafkaPublisher`; `Confluent.Kafka`). Anahtar `{kaynak}:{kayıt}`'tır:
    bir kaydın olayları aynı bölüme düşer ve sıralı kalır (bölümleme Java istemcisiyle aynıdır, `Murmur2Random`).
    `acks=all`, idempotent üretici, olay başına bekleme (`LingerMs = 0`). Başlıklar `event-id`, `event-type`,
    `resource`. Güvenlik ayarları (SASL, TLS) `ConfigureProducer` ile istemcinin kendi ayarlarından verilir.
    İstemcinin günlüğü uygulamanın günlüğüne yönlendirilir. Konuyu Haldus oluşturmaz. Testler tek düğümlü gerçek bir
    kümeye karşıdır (`HALDUS_TEST_KAFKA_IMAGE`; düğüm kendi adresini bildirdiği için bağlantı noktası kapsayıcıdan
    önce seçilir). SASL/PLAIN ve TLS dinleyicileri de sınanır (yanlış parola ve güvenilmeyen sertifika reddedilir).
    Sınırlar: SCRAM ve karşılıklı TLS sınanmadı; paket yerel bir kütüphane (`librdkafka`) taşır.
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
  çözülerek kullanılır (belirtimin örnek vektörüyle sınanır); anahtar çözüldükten sonra en az 16 bayt olmalıdır. Yanıtın gövdesi okunmaz; hata metnine yalnızca
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
