---
paths:
  - "src/**"
  - "samples/**"
  - "tests/**"
  - "docs/**"
  - "reports/**"
---

# Aşamalar ve planlardan sonra yapılanlar

Tasarım kararlarındaki "N. aşama" atıfları bu listeye göredir. Üç plan bitti; yeni iş yeni bir plandır.

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

## İkinci plan

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


## Üçüncü plan

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

## Planlardan sonra

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
  (`AddHaldusWebhookPublisher`; aşağıda, "Generator'ın işleyişi").
- **İstemci paketleri (kullanıcı kararı, 2026-11-25).** S3 deposu ve SQS yayıncısı önce, onay sormamak için, AWS
  imzası elle yazılarak kurulmuştu; RabbitMQ ve Kafka için de tel protokolü elle yazılmaya başlanmıştı. Kullanıcı bunu
  reddetti ("saçmalama") ve dördünün de resmi istemci paketleriyle yazılmasını istedi: `AWSSDK.S3`, `AWSSDK.SQS`,
  `RabbitMQ.Client`, `Confluent.Kafka`. Elle yazılmış imzalayıcı (`S3Signer`) ve protokol kodu kaldırıldı.
  **Olgun bir kütüphanenin yerine protokol istemcisi veya güvenlik kodu elle yazılmaz; paket gerekiyorsa kullanıcıya
  sorulur.**
- **OIDC / JWT (kullanıcı kararı, 2026-11-25; soruldu ve yanıtlandı).** Backend'e
  `Microsoft.AspNetCore.Authentication.JwtBearer` ve `Microsoft.AspNetCore.Authentication.OpenIdConnect` eklenir;
  OIDC girişi panele (`oidc-client-ts`) ve mobil uygulamaya (`expo-auth-session`, `expo-web-browser`, `expo-crypto`)
  de eklenir; e-posta + parola girişi kalır ve OIDC yapılandırma verilince açılır (varsayılan kapalı). Kullanıcı
  ayrıca şunu söyledi: "isteyen JWT, isteyen OIDC kullanacak"; ikisi bu yüzden ayrı ayar bölümleridir ve biri
  ötekini gerektirmez (aşağıda, "Açık kararlar": "Kimlik sağlayıcı").
- Mobil uygulama Android'de denendi (emülatör, Android 17, Expo Go). Önceki "bu makinede emülatör yok" tespiti
  yanlıştı: SDK ve sanal cihazlar kuruluydu, yalnızca `PATH`'te değildiler (`%LOCALAPPDATA%\Android\Sdk`).

Yapılmayanlar ve nedenleri:

- Arayüz bileşenlerinin testi: test çerçevesinin kurulumu (`pnpm add -D vitest …`) izin sistemi tarafından
  reddedildi. Bunun yerine istemcilerin oturum ve istek mantığı, paket eklemeden, Node'un yerleşik test
  çalıştırıcısıyla sınandı. Bu testler iki gerçek hata buldu ve ikisi de düzeltildi: yenileme isteğindeki her HTTP
  hatası (5xx dahil) oturumu kapatıyordu; yenilenen token'lar o sırada değişmiş başka bir hesabın oturumuna
  yazılabiliyordu. Panelin bileşenleri de DOM kütüphanesi olmadan, sunucu tarafı çizimle sınandı; o testler iç içe iki `<main>` ve çeviriden geçmeyen birkaç erişilebilirlik etiketi buldu.
  Etkileşim testleri sonradan, yine paket eklenmeden yazıldı (`pnpm test:e2e`; `.claude/rules/sample-shop.md`). Onlar da gerçek hatalar buldu ve düzeltildi: mobilde sepete art arda eklemek her dokunuşta
  yeni bir sepet oluşturuyordu (değişiklikler artık sıraya girer ve sepetin güncel halini okur, `lib/basket.ts`);
  mobilde dil ve tema seçenekleri web'de seçili olduklarını bildirmiyordu (`accessibilityState` yerine
  `aria-checked`); panelde silme reddedilince neden yalnızca açık pencerenin arkasındaki bildirimde yazıyordu
  (artık pencerenin içinde, `role="alert"`). Mobil ekranlar yalnızca web hedefinde sürülür; cihazda otomatik ekran
  testi yoktur (emülatörde elle denendi).

- iOS: Windows'ta iOS simülatörü çalışmaz; uygulama iOS'ta denenmedi. iOS üretim paketi derlenir
  (`pnpm bundle:native`); çalıştığını göstermez.
- Raporun "Bilinen sınırlar" tablosunda kalan satırlar bilerek dar bırakılanlardır.
