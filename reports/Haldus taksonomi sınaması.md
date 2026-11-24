# Haldus taksonomi sınaması

Araştırma raporu ([Haldus archetype ve trait taksonomisi](Haldus%20archetype%20ve%20trait%20taksonomisi.md)) 59 gerçek
varlığı önerdiği modele eşlemiş, ama bu eşlemelerin "henüz bir üreteçle sınanmadığını" not etmişti. Bu belge o
sınamanın sonucudur: her satır, yazılmış olan API ile ifade edilmeye çalışıldı. Sınama üç kez yapıldı: ilk sürümün
sonunda (10. aşama), ikinci planın sonunda (21. aşama) ve üçüncü planın sonunda (32. aşama); aşağıdaki tablo
sonuncusunun sonucudur.

## Sonuç

59 satırın tamamı bugünkü API ile yazılıp derleniyor ve önerilen eşlemeyi eksiksiz ifade ediyor.

| Sonuç | İlk sınama | İkinci sınama | Bugün | Anlamı |
|---|---|---|---|---|
| Temiz | 19 | 47 | 59 | Raporun önerdiği eşleme, yazılmış özelliklerle eksiksiz ifade ediliyor |
| Kısmi | 29 | 10 | 0 | Çekirdek ifade ediliyor; önerilen eşlemedeki bir özellik yok |
| Yok | 11 | 2 | 0 | Gereken archetype veya trait yazılmadı |

Araştırmanın başlangıçtaki düz katalog için kendi sayımı 10 oturuyor, 31 zorlanarak, 18 oturmuyor idi. Bu sayım
ötekilerle aynı ölçüyle yapılmadı (araştırmacının hükmüydü; sonraki üçü derlenen kod), bu yüzden fark kesin bir
iyileşme ölçüsü olarak değil, yön olarak okunmalıdır.

"59 / 59" modelin bittiği anlamına gelmez; yalnızca bu tablodaki eşlemelerin artık yazılabildiğini söyler. Son üç
satır (3, 5, 36) aşağıda anlatıldığı gibi dar biçimleriyle kapandı ve her özelliğin bilinen sınırları vardır
([Bilinen sınırlar](#bilinen-sınırlar)).

İkinci ve üçüncü plan boyunca satırlar şöyle kapandı:

| Aşama | Yazılan | Temize geçen |
|---|---|---|
| Entity aksiyonu (ilk sınamadan hemen sonra) | Builder'da `MapAction` | 1 |
| 11. Sahiplik | `OwnedByCurrentUser`, `ForCurrentUser` | 2 |
| 12. Alan eklentileri | `Metadata`, `CreateOnly`, `NaturalKey`, `KeyStrategy`, sıralama | 3 |
| 13. Kayıt işlemleri | `Cloneable`, `Orderable`, `Previewable` | 3 |
| 14. Kayıtlar arası zincir | `Reversible`, `Supersedes`, `CreatedFrom`, `DerivedFrom` | 5 |
| 15. Sırlar | `SecretOnce`, `Rotatable`, `TokenAccess` | 2 |
| 16. Bağlar ve kapsam | Yüklü ve durumlu bağ, topluca değiştirme, ebeveyn altında tek kayıt, kısa adres, satır komutları | 3 |
| 17. Onay ve belge | `Approvable`, `Document` | 1 |
| 18. Zaman ve sürüm | `Revisioned`, `Publishable`, `EffectiveDated`, `ChangeFeed` | 2 |
| 19. Dosyalar ve takılabilirler | `MapFile`, `AttachedTo`, `Attachable`, `Commentable`, `Taggable` | 4 |
| 20. Özel alanlar ve aktarma | `CustomFields`, `Exports`, `Imports` | 2 |
| 22. Saklama, okundu takibi, toplama | `Retention`, `ReadTracking`, `Counts`, `Sums` | 4 |
| 23. Yerelleştirilmiş alanlar | `Translatable` | 1 |
| 24. Durum eksenleri | `Axis` | 1 |
| 25. Bakiye | `MapBalance`, `Buckets`, `AllowNegative` | 2 |
| 26. Değişiklik kümesi | `ChangeSets` | 1 |
| 27. Hassas alan, ölçüte göre silme, yeniden çalıştırma | `Sensitive`, `Purgeable`, `Rerunnable` | 0 |
| 28. Olay yayını | `PublishesEvents` | 0 |
| 29. Tip, rol, genelleme | `TypedBy`, `RoleOf`, `SubtypeOf` | 0 |
| 30. Kalan satırlar | `Recurring`; varyant üretimi ve sepet birleştirme entity aksiyonu olarak | 3 |
| 31. Frontend ve mobil | `openapi.json` sözleşmesi ve ondan üretilen istemci | 0 |

27–29. aşamalar tabloda satır kapatmadı: araştırmanın üçüncü sürüm listesinde yer alıyorlardı, ama 59 varlığın
önerilen eşlemelerinde adları geçmiyordu. Örnek uygulamada ve kendi testlerinde sınandılar.

Dört katmanlı model (bir archetype, ilişkiler, en fazla bir durum modeli, trait'ler) üç sınamadan da sağlam çıktı:
hiçbir satır iki archetype istemedi ve hiçbir satır modelin yeniden açılmasını gerektirmedi. Kapanan bütün boşluklar
yeni bir archetype (`MapFile`, `MapBalance`), trait, ilişki bildirimi veya zincir metoduyla kapandı. "En fazla bir
durum modeli" kuralı da açılmadı: ek durum eksenleri asıl durumun yanında, ayrı bir bildirim (`Axis`) olarak yazıldı.

## Nasıl sınandı

- **Derleme:** [TaxonomyTests.cs](../tests/Haldus.Generators.Tests/TaxonomyTests.cs) her satırı, raporun önerdiği
  eşlemeye en yakın biçimde ve yalnızca yazılmış özelliklerle bildirir, generator'dan geçirir ve üretilen kodun
  hatasız derlendiğini doğrular. Aşağıdaki tablo o dosyadaki verinin aynısıdır; sayılar bir testle bağlıdır.
- **Çalışma:** Derleme, uçların doğru davrandığını göstermez. Tablodaki özelliklerin her biri örnek uygulamalarda
  gerçek PostgreSQL'e karşı sınanıyor (bugün 339 entegrasyon testi); ama her satırın kendi birleşimi değil.
  Örnek mağazada bulunmayan birleşimler (ör. iade, abonelik, rezervasyon satırlarının tam hali) yalnızca derleme
  düzeyinde doğrulandı.
- **Hüküm:** "Temiz" ile "kısmi" arasındaki ayrım benim yorumumdur: önerilen eşlemede adı geçen bir özellik
  yazılmadıysa, aynı sonuç elle (ör. bir handler içinde) elde edilebilse bile satır kısmi sayıldı. Araştırmanın bir
  satır için ayrıca not ettiği ama eşlemeye koymadığı eksikler (ör. zamanlanmış yayın) satırı kısmi yapmadı.
  Bu ölçünün bir istisnası vardır: araştırmanın kendisinin "alana özgü, elle yazılır" saydığı iki iş (varyant
  üretimi, sepet birleştirme) bir entity aksiyonu olarak yazılabildiği gösterilince temiz sayıldı (3, 5). Varyant
  üretimi de sepet birleştirme ve misafir sepeti de örnek mağazada gerçek veritabanına karşı sınanıyor. Bu hüküm de
  benim yorumumdur.

## Satırlar

| # | Varlık | Sonuç | Bugün nasıl yazılıyor | Eksik olan |
|---|---|---|---|---|
| 1 | Order | Temiz | `MapCrud` + `Workflow` + `Axis<PaymentState>` + `Axis<ShipmentState>` + `ChangeSets(Placed)` + `Numbered(assignOn)` + `ConcurrencyChecked` + `Auditable` + `MapDetail` + `CreatedFrom<Cart>` | — |
| 2 | OrderItem / LineItem | Temiz | Gömülü `MapDetail` + `IComputed` toplamlar | — |
| 3 | Product + Variant + Option | Temiz | `MapCrud` + `Publishable` + gömülü `MapDetail` (sırası korunur) + `Batch`; varyant üretimi satırlara erişen bir entity aksiyonu | — |
| 4 | Category / Collection | Temiz | `MapCrud` + `Hierarchical` + `Archivable` + `Orderable` | — |
| 5 | Cart | Temiz | `MapCrud` + `OwnedByCurrentUser` + `MapDetail` + `Expiring(sliding)` + `Cloneable`; misafir sepeti `TokenAccess` + `TokenOnly`, birleştirme bir entity aksiyonu | — |
| 6 | Checkout Session | Temiz | `MapCrud().Without(Delete)` + `Workflow` + `Expiring` + `Idempotent`; tamamlanınca doğan kayıt `CreatedFrom<CheckoutSession>(on: "complete")` | — |
| 7 | Coupon | Temiz | `MapCrud` + `Expiring()` + `NaturalKey` + `CreateOnly` + `IComputed` | — |
| 8 | Payment (auth/capture/void) | Temiz | `MapCrud().Without(Delete)` + `Workflow` + `Idempotent`; hareketler `MapAppendOnly().Under<Payment>()` | — |
| 9 | Refund | Temiz | `MapAppendOnly` + `Workflow` + `Idempotent` + `Metadata`; iptal bir geçiş | — |
| 10 | FulfillmentOrder | Temiz | `MapReadOnly` + `Workflow` + `Axis<FulfillmentRequest>` + geçişler | — |
| 11 | Inventory level | Temiz | `MapBalance` + `Buckets(...)` (kovalar arası `move`) + `Idempotent` | — |
| 12 | Invoice | Temiz | `MapCrud` + `Workflow` + `EditableIn` + `Numbered(assignOn)` + `Idempotent` + `MapDetail` + `Previewable`; ödeme durumu `Sums<InvoicePayment>()` + `IComputed` ile türetilir | — |
| 13 | InvoiceLine | Temiz | Gömülü `MapDetail` | — |
| 14 | Credit note | Temiz | `MapAppendOnly` + `CreatedFrom<Invoice>` + `Workflow` (void) + `Numbered` | — |
| 15 | JournalEntry | Temiz | `MapCrud` + `Document` + `Numbered(assignOn)` + `MapDetail` + `Reversible`; "satırlar dengeli" kuralı gönderme geçişinde | — |
| 16 | Journals (genel defter) | Temiz | `MapReadOnly` + `Searchable` | — |
| 17 | Chart of accounts | Temiz | `MapCrud` + `Hierarchical` + `Archivable`; referanslar `Restrict` | — |
| 18 | Document numbering | Temiz | `Numbered` (boşluksuz, kiracı ve dönem başına) | — |
| 19 | Payment (muhasebe) + allocation | Temiz | `MapAppendOnly` + `References<Invoice>` + `Idempotent` + `Reversible` | — |
| 20 | StockMovement / defter kaydı | Temiz | `MapAppendOnly` + `Auditable` | — |
| 21 | Reservation | Temiz | `MapCrud` + `Workflow` + `Expiring` | — |
| 22 | Transfer | Temiz | `MapCrud` + `Workflow` + `MapDetail` + `Idempotent` + `IComputed` + `Cloneable` | — |
| 23 | Employee | Temiz | `MapCrud` + `Searchable` + `ChangeFeed` + `CustomFields` + entity aksiyonu; geçmiş `MapCrud().Under<Employee>().EffectiveDated()` | — |
| 24 | LeaveRequest | Temiz | `MapCrud` + `Under<Employee>` + `Workflow` + `Approvable(levels: 2, "take")` | — |
| 25 | Time-off balance | Temiz | `MapBalance` + `Buckets("available", "pending", "taken")` | — |
| 26 | Timesheet / attendance | Temiz | `MapCrud` + `Batch(atomic: false)`; giriş/çıkış birer entity aksiyonu | — |
| 27 | Post / Entry | Temiz | `MapCrud` + `Publishable` + `SoftDeletable` + `ConcurrencyChecked` + `Revisioned` | — |
| 28 | Localized fields | Temiz | `Translatable(...)`; yayındaki hal çevirileri taşır (`Publishable`) | — |
| 29 | Revision / Snapshot | Temiz | `Revisioned` (liste, tek sürüm, geri yükleme) | — |
| 30 | Comment (threaded) | Temiz | `MapCrud` + `Hierarchical` + `SoftDeletable`; hedeflerde `Commentable<Comment>()` | — |
| 31 | Tag / Label | Temiz | `MapLookup` + `MapAssociation` (bağlar `PUT /articles/{id}/tags` ile topluca değişir) | — |
| 32 | MediaFile / Asset | Temiz | `MapFile` (boyut ve tür sınırı) + `MultiTenant` + `Metadata` | — |
| 33 | EventType / rezerve edilebilir kaynak | Temiz | `MapCrud().Without(Delete)` + `Archivable` | — |
| 34 | Availability / free-busy | Temiz | `MapQuery` | — |
| 35 | Appointment / Booking | Temiz | `MapCrud` + `Workflow` + `Idempotent` + `ConcurrencyChecked` + `Supersedes` (yeniden planlama) | — |
| 36 | Recurring event / series | Temiz | `MapCrud` + `Recurring` (kural + hesaplanan oluşlar + oluş başına istisna) | — |
| 37 | Tenant / Organization | Temiz | `MapCrud` + `Auditable` | — |
| 38 | User | Temiz | `MapCrud` + `SoftDeletable` + `Metadata` + `Searchable` + entity aksiyonu | — |
| 39 | Role & permission | Temiz | `MapLookup` + `MapCrud` + `MapAssociation` | — |
| 40 | Membership | Temiz | `MapAssociation<Organization, User>()` + bağ sınıfında rol + `Workflow` ve geçişler | — |
| 41 | Invitation | Temiz | `MapCrud().Without(Update, Patch, Delete)` + `Workflow` + `Expiring` + `TokenAccess`; kabul edilince doğan üyelik `CreatedFrom<Invitation>(on: "accept")` | — |
| 42 | ApiKey / secret | Temiz | `MapCrud().Without(Update, Patch)` + `Expiring()` + `SecretOnce` + `Rotatable` | — |
| 43 | Settings / Preferences | Temiz | `MapSingleton().MultiTenant()`, `MapStore().MultiTenant()` | — |
| 44 | AuditLog / Event | Temiz | `MapReadOnly` + `Searchable` + `Retention`; dışa aktarma `MapJob().Exports<AuditLog>()` | — |
| 45 | Notification | Temiz | `MapReadOnly` + `OwnedByCurrentUser` + `ReadTracking` + `Retention` | — |
| 46 | Webhook endpoint | Temiz | `MapCrud` + `Archivable` + `SecretOnce(recoverable)` + `Rotatable`; ping bir entity aksiyonu; yapılandırma `MapSingleton().Under<Webhook>()` | — |
| 47 | Webhook delivery | Temiz | `MapReadOnly` + `Under<Webhook>` + `Workflow`; yeniden gönderme bir geçiş | — |
| 48 | Subscription / Plan | Temiz | `MapCrud` + `Workflow` + `MapDetail` | — |
| 49 | Polimorfik attachment | Temiz | `MapFile`; hedeflerde `Attachable<Attachment>()` | — |
| 50 | Comment / activity feed | Temiz | `MapAppendOnly`; hedeflerde `Commentable<Activity>()` | — |
| 51 | Custom fields / metadata | Temiz | `Metadata` (serbest çanta) + `CustomFields` (kiracının tanımladığı, türüne göre doğrulanan alanlar) | — |
| 52 | `/me` ve kullanıcıya ait kaynaklar | Temiz | `OwnedByCurrentUser()` (`/me` altında) veya `OwnedByCurrentUser(underMe: false)` | — |
| 53 | Report / aggregation | Temiz | `MapQuery` veya `MapJob` | — |
| 54 | Search | Temiz | `Searchable` | — |
| 55 | Favorite / star / vote | Temiz | `MapAssociation<Favorite, Product>().ForCurrentUser()` | — |
| 56 | Ordering / position | Temiz | `Orderable` (`Position` alanı, `POST /{id}/move`) | — |
| 57 | Counter | Temiz | `Counts<Track>()` + `Sums<Track>(...)` | — |
| 58 | Bulk operations | Temiz | `Batch` + `MapJob` + `MapJob().Imports<T>()` | — |
| 59 | Büyük veri export'u | Temiz | `MapJob().Exports<T>()` + `Idempotent` (mantığı üretilir; dosya işten indirilir) | — |

## Bulgular

- **Durum makinesi, ayrı trait olarak önerilen birkaç kalıbı karşıladı.** "İptal et" / "yeniden gönder" gibi durumu
  değiştiren aksiyonların hepsi `Workflow` ve `MapTransition` ile ifade edildi (9, 12, 47). `Document` sonradan
  yazıldı, ama yeni bir satır kapatmadı: aynı şeyin (taslak → gönder → iptal → düzelt) hazır bir biçimidir (15).
- **"Yalnızca eklenen detay" ayrı bir detay modu gerektirmedi.** Ödeme hareketleri `MapAppendOnly().Under<Payment>()`
  ile yazıldı (8).
- **İlk sınamanın tek beklenmedik bulgusu entity aksiyonuydu:** bir kayıt üzerinde çalışan ama durumunu
  değiştirmeyen uç (giriş/çıkış, parola sıfırlama, ping). Araştırmanın ilk sürüm listesinde yoktu; dört satırı
  etkiliyordu (23, 26, 38, 46) ve builder'da çağrılan `MapAction` ile kapandı.
- **Tersine çevirme üç muhasebe satırında eksikti** (14, 15, 19). `Reversible` ile yazıldı. Araştırmanın ayrı
  önerdiği `Reverses<T>` (başka tipten telafi kaydı) ayrı bir bildirim olarak eklenmedi: `CreatedFrom<T>` aynı şeyi
  ifade ediyor; 14. satır bu yüzden temiz sayıldı ve bu benim yorumumdur.
- **Gizli değerler üç satırda eksikti** (41, 42, 46). Araştırma sırrın yalnızca özetinin saklanmasını öneriyordu;
  webhook imza sırrı gibi sunucunun kendisinin kullandığı sırlar özetten geri okunamadığı için şifreli saklama
  (`recoverable: true`) ayrıca eklendi.
- **Bağın kendisi üç satırda eksikti** (31, 40, 46): bağların topluca değiştirilmesi, kendi alanı ve durumu olan
  bağ, bir kaydın altındaki tek yapılandırma kaydı. Durumu olan bağın geçişleri gövdesizdir; mantık gerektiren bir
  üyelik hâlâ `MapCrud` + iki `References` ile yazılır.
- **Onay bir trait oldu; zincir olmadı.** `Approvable` karar kaydını, "içeriği yazan onaylayamaz" kuralını ve sıralı
  düzeyleri getirir (24). Araştırmanın önerdiği onay zinciri (düzey başına farklı roller, koşullu adımlar)
  yazılmadı: düzeyler yalnızca "birbirinden farklı N kişi" demektir.
- **Zaman dört ayrı biçimde ortaya çıktı** ve dördü de ayrı yazıldı: kaydın geçmiş halleri (`Revisioned`; 27, 29),
  taslağın yanında yayındaki hal (`Publishable`; 3, 27), iş tarafının beyan ettiği geçerlilik dönemi
  (`EffectiveDated`; 23) ve "şundan beri ne değişti" akışı (`ChangeFeed`; 23).
- **Polimorfik referans, tip + kimlik çifti olarak değil hedef başına yabancı anahtar olarak yazıldı.** Hedefler
  derleme zamanında bilindiği için her hedefe gerçek bir yabancı anahtar üretildi ve "tam olarak biri dolu" kuralı
  veritabanı kısıtı oldu; takılı kaydın görünürlüğü ve silinmesi tek ebeveynli kaynaklarla aynı yoldan gelir.
  `Attachable`, `Commentable` ve `Taggable` ayrı altyapı değil, aynı bildirimin hedef tarafındaki yazımıdır
  (30, 32, 49, 50).
- **Bakiye, defterden türetilen bir izdüşüm olmaktan çıkıp kendi archetype'ı oldu** (11, 25). İkinci sınamada
  `MapAppendOnly` + `DerivedFrom` ile yaklaşık ifade ediliyordu; eksik olan, "bakiye yetiyor mu" kontrolünün yazmayla
  aynı kilidin altında yapılmasıydı. `MapBalance` bunu ve araştırmanın eksik saydığı adlandırılmış kovaları getirir.
  Bakiyeye karşı süreli tutma (rezervasyonun kendiliğinden geri dönmesi) üretilmez; kovalar arası `move` ile yazılır.
- **Birden çok durum ekseni "en fazla bir durum modeli" kuralını açmadı** (1, 10). Asıl durum tek kaldı; ödeme ve
  sevkiyat gibi eksenler `Axis` ile, kendi geçişleriyle yazıldı. Düzenleme ve silme kuralları yalnızca asıl duruma
  bakar; eksene bağlı bir `EditableIn` yoktur.
- **Türetilmiş durum ayrı bir bildirim gerektirmedi** (12, 57). Araştırmada tanımı olmayan iki eksik (faturanın
  `paid` durumu, sayaç) aynı parçayla kapandı: alt kayıtlardan toplanan alan ve ardından çağrılan `Compute()`.
- **Değişiklik kümesi bir archetype değil, hedefin bir yazma yolu oldu** (1). Araştırma `MapChangeSet<T>()`
  öneriyordu; küme hedefin birikmiş `PATCH` gövdesi olarak yazıldı ve kurallarını kısmi güncellemeden aldı. Bu,
  araştırmanın önerisinden bilerek ayrılan bir karardır.
- **Araştırmanın açık bıraktığı çeviri deposu sorusu alan-dil haritasıyla kapandı** (28): çeviriler kayıttaki bir
  çantadadır, yan tablo yoktur. Karşılığında çevirilere göre süzme, arama ve sıralama yapılamaz.
- **Son üç satır dar biçimleriyle kapandı** (3, 5, 36). Tekrar serisi araştırmada bilerek kapsam dışıydı; yazılan
  RRULE değildir (sıklık, aralık, bitiş; haftanın günleri ve saat dilimi yok). Varyant üretimi ve sepet birleştirme
  için yeni bir bildirim yazılmadı; ikisi de entity aksiyonudur. Bunları sınarken bir hata çıktı: mantığın eklediği
  gömülü satırlar kimliksiz kalıyor ve kaydetme 500 veriyordu.
- **Üçüncü sürüm listesinin tabloda karşılığı olmayan kalemleri de yazıldı:** hassas alan, ölçüte göre silme, işin
  yeniden çalıştırılması, olay yayını (outbox), tip–örnek, rol ve genelleme (tek tablo kalıtımı). Hiçbiri bir satırın
  hükmünü değiştirmedi.
- **Frontend ve mobil için kod üretilmedi; verilen şey sözleşmedir.** Örnek mağazanın `openapi.json` dosyasından
  orval ile üretilen istemci (579 dosya; tipler ve TanStack Query hook'ları) tip denetiminden hatasız geçiyor. Bunu
  sınarken şemadaki bir kusur görüldü: ASP.NET Core'un varsayılan sayı ayarı her tam sayıyı `integer | string`
  yazıyordu; örnek mağaza sayıları katı okuyacak biçimde ayarlandı.
- **Sözleşme iki gerçek arayüzde tüketildi** (planlardan sonra): bir yönetim paneli (Vite + React + shadcn/ui) ve bir
  mobil uygulama (Expo + React Native Reusables). İkisi de istemcisini aynı `openapi.json` dosyasından, yalnızca
  kullandığı etiketler için üretir. Yazarken görülenler: orval'ın varsayılan `fetch` istemcisi hata yanıtında
  fırlatmaz, bu yüzden bir sarmalayıcı (mutator) ve `forceSuccessResponse` gerekti; sürüm kontrollü kaynakta `ETag`
  yanıt başlığından okunup `If-Match` ile geri gönderildi; doğrulama bütünüyle backend'de kaldı ve alan hataları
  ile reddedilen geçişin nedeni, arayüzün dilinde, yanıttan okundu.
- **Misafir sepeti bir açık gösterdi** (planlardan sonra, 5. satır örnek mağazada çalıştırılırken). `TokenAccess`
  kimlikle çalışan adresleri de bırakıyordu; sahibi bir kullanıcı olmayan bir kayıtta bu, kimliği bilen herkese yazma
  izni demekti. `TokenOnly` eklendi: kimlikli hiçbir uç üretilmez.
- **Güvenlik incelemeleri üçüncü planda da aynı türden bulgular verdi.** Dört aşamadan sonra düzeltme gerekti:
  kullanıcılara ait kayıtların ortak bir kayıtta örtük toplanması (22), hassas alanın değişiklik kümesinde saklanıp
  geri gösterilmesi ve ölçütsüz sayılabilecek toplu silme (27), rolün zincirleme silmesinin kiracı sınırını aşması
  ve hassas alanın tipten devralınması (29), tek bir oluşu değiştiren uçların güncelleme korumalarını atlaması (30).
  Hepsi yine iki özelliğin kesiştiği yerdeydi.
- **Güvenlik incelemeleri modeli değil birleşimleri düzeltti.** İkinci plan boyunca her aşamadan sonra yapılan
  incelemeler on üç bulgu verdi (biri geçerli değildi); hiçbiri bir archetype veya trait'in tanımını değiştirmedi, hepsi iki
  özelliğin kesiştiği yerdeydi (kopyanın devraldığı referans, zincir işleminin durum kuralını atlaması, aksiyonun
  onaylı içeriği değiştirmesi, etiket uçlarının yetkilendirmeyi almaması, kiracıya ait veriyi aktaran işin kiracıya
  ait olmaması). Bu, araştırmanın "kalan riskin ağırlık merkezi API yüzeyinde değil üretimin iç tarafındadır"
  tespitini doğrular.

## Bilinen sınırlar

Tabloda eksik satır kalmadı. Aşağıdakiler bir satırı kısmi yapmıyor, ama bilerek yazılmadı; her özelliğin kendi
sınırları ayrıca [README](../README.md) ve `CLAUDE.md` dosyalarında, özelliğin anlatıldığı yerdedir.

| Yazılmayan | İlgili satırlar | Neden |
|---|---|---|
| Onay zincirinin gelişmiş biçimleri (paralel onay, vekâlet, düzeyin birden çok rolü) | 24 | Düzey başına rol (`ApprovalRoles`) ve kayda göre değişen düzey sayısı (`IApprovalLevels`) sonradan yazıldı |
| `RRULE`'un tamamı (`BYMONTHDAY`, `BYSETPOS`, `BYMONTH`, `WKST`, `EXDATE` / `RDATE`) | 36 | Haftanın günleri, ay içindeki sıra (`2TU`, `-1FR`) ve saat dilimi sonradan yazıldı; gerisi araştırmada da kapsam dışıydı |
| Bakiyeye karşı süreli tutma | 11, 21 | Kovalar arası `move` ile elle yazılır; süresi dolan tutma kendiliğinden geri dönmez |
| Paylaşılan kaydın kullanıcı başına okundu bilgisi | 45 | `ReadTracking` bilgiyi kayıtta tutar ve kullanıcıya ait kaynak ister |
| Hassas alanın maskelenerek gösterilmesi (son dört karakter) | — | Şifreli saklama sonradan yazıldı (`Encrypted`); şifreli alan hiçbir yanıtta gösterilmez |
| Çok düzeyli kalıtım ve tek tablo dışındaki eşlemeler | — | `SubtypeOf` tek düzeydir ve tek tablo (TPH) kullanır |
| İmzalı adres, çok parçalı ve devam ettirilebilir yükleme; nesne deposu paketleri | 32, 49 | Yeni bağımlılık gerektirirdi; `IHaldusFileStorage` uygulanarak eklenir |
| Mesaj kuyruğu paketi | — | `PublishesEvents` olayı uygulamanın yayıncısına teslim eder; taşımak onun işidir |
| Zamanlanmış yayın | 27 | Araştırma not etmiş, eşlemeye koymamıştı |
| "İş tanımı + çalıştırmalar" modeli (AIP-152) | — | İş kaydı zaten bir çalıştırmadır; yeniden çalıştırma yeni bir iş kaydıdır |
| ULID ve istemcinin verdiği kimlik | — | `KeyStrategy` için tercih belirtilmedi; önekli kimlik ve GUID yazıldı |
| "Uzatma kancaları" | — | Araştırmanın ilk sürüm listesinde adı geçiyor, tanımı yok |
| Gerçek kimlik doğrulamalı bir örnek arayüz | — | Örnek panel ve mobil uygulama demo kimliğiyle (başlıklar) çalışır; kimlik sağlayıcı seçimi sorulması gereken bir karardır |
| Arayüzler için otomatik test | — | Frontend test framework'ü sorulması gereken bir seçimdi; arayüzler tip denetimi, derleme ve elle doğrulandı |

## Bu sınamanın söylemediği

- Her satırın kendi birleşiminin uçlarının doğru davrandığı; özellikler tek tek sınandı, satırların çoğu yalnızca
  derleniyor.
- Tasarım kararlarının doğru olduğu. 15. aşamadan sonraki kararlar sorulmadan, önerilen seçenekle verildi ve
  kullanıcı tarafından ayrıca onaylanmadı; hepsi `CLAUDE.md` dosyasının "Tasarım kararları" bölümünde, bu notla
  birlikte yazılıdır.
- Büyük veri altındaki davranış: kilide dayanan özellikler (`Orderable`, `EffectiveDated`, `DerivedFrom`, boşluksuz
  `Numbered`, `MapBalance`, toplanan alanlar, olay dağıtımı) ve içeriği bellekte kuran yollar (veritabanı dosya
  deposu, dışa aktarma) yük altında ölçülmedi.
- Güvenliğin tam olduğu. Her aşamadan sonra yapılan incelemelerin bulguları düzeltildi; bu, bulunmamış bir
  birleşim hatası kalmadığını göstermez. Oran sınırlama ve onaylayanın yetkisi gibi konular uygulamaya bırakıldı.
- Mobil uygulamanın cihazda çalıştığı. Örnek mobil uygulama yalnızca Expo'nun web hedefinde, çalışan backend'e karşı
  denendi; Android ve iOS'ta çalıştırılmadı. Yönetim paneli tarayıcıda uçtan uca denendi, ama otomatik testi yoktur.
- PostgreSQL dışındaki veritabanları. Numaralama, kilitler ve benzersizlik çakışmasının tanınması PostgreSQL'e
  özgüdür.
