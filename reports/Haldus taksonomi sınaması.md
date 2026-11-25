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
  gerçek PostgreSQL'e karşı sınanıyor (bugün 424 entegrasyon testi); ama her satırın kendi birleşimi değil.
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
  düzeyleri getirir (24). Araştırmanın önerdiği onay zinciri planlardan sonra, trait'in iki eklentisi olarak yazıldı:
  düzey başına rol (`ApprovalRoles`) ve kayda göre değişen düzey sayısı (`IApprovalLevels`; koşullu adım). Paralel
  onay ve vekâlet yoktur.
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
  Bakiyeye karşı süreli tutma (rezervasyonun kendiliğinden geri dönmesi) planlardan sonra yazıldı (`Holds`): tutma
  miktarı bir kovadan ötekine ayırır, tahsil edilir, bırakılır veya ömrü dolunca bakım tarafından bırakılır.
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
  RRULE değildir (sıklık, aralık, bitiş; haftanın günleri, ay içindeki sıra ve saat dilimi planlardan sonra eklendi,
  `RRULE`'un geri kalanı yoktur). Varyant üretimi ve sepet birleştirme
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
- **"Bilinen sınırlar"ın yeni bağımlılık gerektirmeyenleri planlardan sonra yazıldı:** tekrar serisinde haftanın
  günleri ve saat dilimi, şifreli alan (`Encrypted`), onay zinciri, paylaşılan kayıtta kullanıcı başına okundu bilgisi
  (`ReadReceipts`), zamanlanmış yayın (`ScheduledPublishing`), ULID ve istemcinin verdiği kimlik, bakiyeye karşı tutma
  (`Holds`), multipart yükleme ve süreli indirme bağlantısı (`SignedLinks`). Hiçbiri bir satırın hükmünü değiştirmedi;
  tablonun geri kalanı aşağıdadır.
- **Bu eklemelerin güvenlik incelemeleri altı bulgu verdi; hepsi düzeltildi.** `TokenAccess` sahibi olmayan kayıtta
  kimlikli uçları açık bırakıyordu (`TokenOnly`); şifreli alan tipten devralınabiliyordu; yayınlanamayan taslaklar
  zamanlanmış yayının sırasını başka kiracılar için tutabiliyordu; istemcinin verdiği kimliğin "alınmış" yanıtı,
  isteği yapanın göremediği bir kaydın varlığını söylüyordu (iki bulgu: önce kiracı ve sahip, sonra gizlenen öteki
  kayıtlar); bırakma nedeninin uzunluğu defter kolonunu aşıp tutmanın hiç bırakılamamasına yol açıyordu.
- **Gerçek kimlik doğrulama Haldus'ta hiçbir şeyi değiştirmedi, ama örnekte bir açık gösterdi.** Örnek mağaza parola
  ile girişe ve taşıyıcı token'a geçirildi; Haldus kullanıcıyı ve kiracıyı zaten claim'lerden okuyordu. Panel
  tarayıcıda denenirken görüldü: reddedilen bir token sunan istek anonim sayılıyor ve kaynağa göre 401, 403 veya 200
  alıyordu; istemci oturumunu yenilemesi gerektiğini anlayamıyordu. Örnek, reddedilen token'ı her kaynakta 401 ile
  yanıtlayacak biçimde düzeltildi. Bu, uygulamanın kimlik doğrulama katmanının işidir; Haldus'a taşınmadı.
- **Kimlik doğrulamanın eksikleri kapatılınca bağımsız bir inceleme istendi ve on bir bulgu verdi; hepsi düzeltildi.**
  Üçü ağırdı: (1) örnekte giriş yalnızca kimlik uçlarında zorunluydu, kiracıya ait olmayan kaynaklar token'sız
  yazılabiliyordu (artık her uç giriş ister, açık olanlar kendini işaretler); (2) e-postayla gönderilen altı haneli
  kodun deneme hakkı yeni kod istenince yenileniyordu, kod denenerek bulunabilirdi (sayımlar artık saatlik pencereye
  ait); (3) yanıt vermeyen bir webhook alıcısının zaman aşımı arka plan servisini ve uygulamayı durduruyordu. Sonuncusu
  çekirdekteki bir kusurdu ve `HttpClient` kullanan her yayıncıyı etkilerdi. Ötekiler: doğrulanmış hesapta doğrulama
  ucunun kodsuz token vermesi, yenileme token'ının yeniden kullanılabilmesi ve oturumun süresiz olması, hız sınırının
  IPv6'da ve vekil arkasında zayıflığı, sıfırlama isteğinin süresinin adresin kayıtlı olduğunu ele vermesi, parola
  değiştirme ucunun kilit sayacının dışında kalması, webhook hata metninin adresteki anahtarı saklayabilmesi, S3'te
  vazgeçilemeyen yüklemenin sessiz kalması, demo kimliğinin üretimde yanlışlıkla açılabilmesi. Buradan çıkan ders
  öncekilerle aynı değil: bunlar iki özelliğin kesişimi değil, yeni yazılmış kodun ilk halinin eksikleriydi ve hiçbirini
  testler yakalamamıştı.
- **Düzeltmeler yeniden incelendi ve ikisi kendi kusurunu getirmişti.** Kodun deneme sınırını pencereye bağlamak,
  başkasının yanlış kodlar göndererek bir hesabın parola sıfırlamasını engellemesine izin veriyordu; yenileme
  token'ının bir kez kullanılması ise yanıtı yolda kaybolan istemcinin veya aynı anda yenileyen ikinci sekmenin
  oturumunu kapatıyordu. İlki kodu uzatıp (sekiz karakter, 40 bit) deneme sınırını kaldırarak, ikincisi kısa bir
  toleransla kapatıldı. O tolerans da yeniden incelendi: pencere içinde eskimiş token'a yeni kimlik bilgisi veriyordu;
  artık oturumu kapatmıyor ama token da vermiyor. Aynı incelemenin gösterdiği son eksik de kapatıldı: örnekte rol
  yoktu ve kayıt olan her hesap ortak kaynakları yazabiliyordu. Bunun için üretilen uçlar artık kaynağın kapsamını
  metadata olarak taşıyor (`HaldusResourceScope`) ve örnek, ortak kaynakların yazılmasını personele bırakıyor; kural
  kaynakları tek tek saymadığı için sonradan eklenen kaynakları da kapsıyor.
- **O kuralın kendisi de iki incelemeden geçti ve ilk hali dört yerden açıktı.** Rol, e-postası doğrulanmamış hesaba da
  veriliyordu (personel adresiyle ilk kaydolan rolü alırdı); kapsamını bildirmeyen yazma uçları kuralın dışında
  kalıyordu (bütün ürünlerin fiyatını değiştiren bir aksiyon her hesaba açıktı); okuma, adresin sonuna bakılarak
  tanınıyordu; okundu işaretleri ise tersine, ortak kaynakta yanlışlıkla personele kalmıştı. Kural artık kapalı
  başlıyor, yazmayan `POST` uçlarını üretici işaretliyor (`HaldusReadOnly`) ve işaret uçları kullanıcıya ait
  sınıflanıyor. Kuralın gerçek kimlik doğrulamayla sınanmamış olması bu açıkların geçmesinin nedeniydi: entegrasyon
  testlerinin çoğu, yetki kuralının hiç çalışmadığı demo kimliğiyle koşuyor.
- **İstemcilerin oturum mantığı için yazılan testler iki hata buldu:** yenileme isteğindeki her HTTP hatası (geçici bir
  sunucu arızası da) kullanıcıyı oturumdan atıyordu ve yenilenen token'lar, o sırada değişmiş başka bir hesabın
  oturumuna yazılabiliyordu.
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
| Tutmanın kısmen tahsil edilmesi | 11, 21 | Bakiyeye karşı süreli tutma sonradan yazıldı (`Holds`); tutma bütün olarak tahsil edilir veya bırakılır |
| Kaydı kimlerin okuduğunun listesi ("görenler") | 45 | Kullanıcı başına okundu bilgisi sonradan yazıldı (`ReadReceipts`); her kullanıcı yalnızca kendi işaretini görür |
| Hassas alanın maskelenerek gösterilmesi (son dört karakter) | — | Şifreli saklama sonradan yazıldı (`Encrypted`); şifreli alan hiçbir yanıtta gösterilmez |
| Çok düzeyli kalıtım ve tek tablo dışındaki eşlemeler | — | `SubtypeOf` tek düzeydir ve tek tablo (TPH) kullanır |
| Devam ettirilebilir yükleme, nesne deposuna doğrudan (ön imzalı) yükleme | 32, 49 | Multipart yükleme, süreli indirme bağlantısı (`SignedLinks`) ve S3 uyumlu depo (`Haldus.Storage.S3`; `AWSSDK.S3` üzerine) sonradan yazıldı. Depo MinIO'ya karşı sınandı, gerçek AWS S3'e karşı sınanmadı |
| Mesaj kuyruklarının gerçek ortamda sınanması | — | Webhook yayıncısı (`AddHaldusWebhookPublisher`) ve üç kuyruk yayıncısı sonradan, resmi istemci paketleriyle yazıldı: Amazon SQS (`AWSSDK.SQS`), RabbitMQ (`RabbitMQ.Client`), Kafka (`Confluent.Kafka`). RabbitMQ ve Kafka kapsayıcıda çalışan gerçek aracılara, SQS bir öykünücüye (ElasticMQ) karşı sınandı; gerçek AWS'ye karşı ve TLS / SASL ile sınanmadılar |
| Zamanlanmış yayından kaldırma | 27 | Zamanlanmış yayın sonradan yazıldı (`ScheduledPublishing`); yayın, zamanından en çok bir bakım aralığı sonra gerçekleşir |
| "İş tanımı + çalıştırmalar" modeli (AIP-152) | — | İş kaydı zaten bir çalıştırmadır; yeniden çalıştırma yeni bir iş kaydıdır |
| İstemcinin verdiği metin kimlik (slug) | — | ULID ve istemcinin verdiği `Guid` kimlik sonradan yazıldı; anlamlı metin anahtar için `NaturalKey` veya `MapStore` vardır |
| "Uzatma kancaları" | — | Araştırmanın ilk sürüm listesinde adı geçiyor, tanımı yok |
| İki adımlı doğrulama; rol yönetimi; birden çok kimlik sağlayıcı | — | Gerçek kimlik doğrulama sonradan yazıldı: parola ile giriş, sunucuda oturum ve çıkış, kodla parola sıfırlama ve e-posta doğrulama, hesap kilidi, hız sınırı; ardından kimlik sağlayıcıyla giriş (OpenID Connect: backend'de `JwtBearer` ve `OpenIdConnect`, panelde `oidc-client-ts`, mobilde `expo-auth-session`). Backend ve panel kapsayıcıdaki Keycloak'a karşı denendi; mobildeki giriş cihazda denenmedi, başka sağlayıcılar denenmedi |
| Mobil ekranların cihazda otomatik testi | — | Arayüzlerin testleri paket eklenmeden yazıldı: oturum ve istek mantığı (Node'un yerleşik test çalıştırıcısı; panel 375, mobil 232 test), panelin bileşenlerinin sunucu tarafı çizimle denetimi ve gerçek bir tarayıcının DevTools protokolüyle sürüldüğü etkileşim testleri (`pnpm test:e2e`; panel 49, mobil 34 senaryo). Mobilde sürülen uygulamanın web hedefidir; cihaza özgü olanlar (güvenli depo, klavye, yerel bileşenler) yalnızca Android emülatöründe elle denendi. Etkileşim testleri API'nin bir taklidiyle konuşur; panelin ve uygulamanın gerçek backend'le birlikte çalışması elle doğrulandı |

## Yük altında ölçüm

Kilide dayanan özellikler [Haldus.LoadTest](../tools/Haldus.LoadTest) aracıyla, çalışan örnek mağazaya karşı ölçüldü
(2026-11-25). Her satır 2000 işlemdir (önünde sayılmayan 100 ısınma işlemi); hiçbir çalıştırmada başarısız yanıt
olmadı.

**Ortam:** tek makine (Intel i7-11700KF, 16 mantıksal çekirdek, 64 GB; Windows 11). İstemci, backend (.NET 10,
Release, Kestrel) ve PostgreSQL 17 (Docker Desktop, WSL2) aynı makinededir ve işlemciyi paylaşır. Veritabanı
varsayılan ayarlarıyla, boş başlatıldı. Sayılar bu makinenin göreli kıyasıdır; bir kapasite vaadi değildir.

| Senaryo | Ne ölçülüyor | Eşz. 1: işlem/sn (p50) | Eşz. 8: işlem/sn (p50 / p99) | Eşz. 32: işlem/sn (p50 / p99) |
|---|---|---:|---:|---:|
| Okuma | 20 kayıtlık sayfa + toplam | 366 (2,7 ms) | 2373 (3,2 / 5,8 ms) | 4839 (6,2 / 12,1 ms) |
| Yazma | Kilitsiz, sayaçsız kayıt | 222 (4,4 ms) | 1632 (4,8 / 7,6 ms) | 4542 (7,5 / 9,3 ms) |
| Boşluksuz numara | Tek sayaç | 97 (10,0 ms) | 156 (50 / 68 ms) | 157 (202 / 239 ms) |
| Sequence numarası | `gapless: false` (+ idempotency) | 70 (13,9 ms) | 538 (14,5 / 19,9 ms) | 1148 (27,1 / 48,8 ms) |
| Sıraya ekleme | `Orderable`, ağacın kilidi | 104 (9,5 ms) | 143 (55 / 67 ms) | 143 (223 / 250 ms) |
| Sırada taşıma | 100 kardeş yeniden numaralanır | 54 (18,3 ms) | 58 (136 / 163 ms) | 57 (558 / 592 ms) |
| Bakiye, tek anahtar | `MapBalance` `adjust` | 115 (8,5 ms) | 143 (55 / 70 ms) | 144 (222 / 241 ms) |
| Bakiye, işçi başına anahtar | aynı | 116 (8,5 ms) | 801 (9,8 / 14,3 ms) | 2019 (15,5 / 24,0 ms) |
| Tutma + tahsilat | `Holds`; iki istek | 50 (19,9 ms) | 59 (136 / 159 ms) | 58 (546 / 601 ms) |
| Toplanan alan, tek üst kayıt | `Counts` + `Sums` | 83 (11,8 ms) | 154 (52 / 64 ms) | 151 (210 / 287 ms) |
| Toplanan alan, işçi başına üst kayıt | aynı | 82 (12,0 ms) | 619 (12,3 / 17,2 ms) | 1118 (23,1 / 335 ms) |
| Türetme | `DerivedFrom`, kaynak başına kilit | 122 (8,1 ms) | 151 (53 / 60 ms) | 151 (210 / 234 ms) |
| Dönemli kayıt | `EffectiveDated`, tek kapsam | 102 (9,7 ms) | 141 (56 / 71 ms) | 141 (222 / 267 ms) |

Dışa aktarma: 20 000 ürünün CSV'si (1,74 MiB) 0,44 saniyede üretildi; dosya 30 ms'de indirildi.

Okunması:

- **Kilit doğruluğu sıraya sokarak sağlıyor ve bedeli ölçülebilir.** Aynı kilidi bekleyen yazmalar bu ortamda
  saniyede 140–150'de düzleşiyor; eşzamanlılık arttıkça verim değil yanıt süresi büyüyor (32 istemcide ortanca ~220 ms).
  Tavanı, kilidin tutulduğu süre belirliyor; o sürenin çoğu veritabanının commit'i (bu ortamda kilitsiz bir yazma
  da tek başına 4,4 ms sürüyor). Daha hızlı bir diskte tavan yükselir, biçimi değişmez.
- **Kilidin kapsamı beyan edildiği gibi.** Bakiye ve toplanan alan anahtar / üst kayıt başına kilitleniyor: aynı
  işlemler farklı anahtarlara dağıldığında verim eşzamanlılıkla birlikte büyüyor (2019 ve 1118 işlem/sn).
- **`DerivedFrom` ve `Orderable` en dar boğazlar.** Türetme kaynak tipinin tamamını, sıra bütün ağacı tek kilide
  bağlar; örnekte bütün ürünlerin stok hareketleri ve bütün kategori eklemeleri saniyede ~150 ile sınırlıdır. İkisi
  de `CLAUDE.md`'de bilinen sınır olarak yazılıydı; ölçüm büyüklüğünü verdi. Yazma hacmi yüksek bir kaynak için
  türetme yerine kendi kilidi dar olan `MapBalance` veya toplanan alan uygundur.
- **Ölçüm boşluksuz numarada bir kusur gösterdi ve düzeltildi.** İlk ölçümde verim ötekilerle aynıydı (~165–175
  işlem/sn), ama 32 istemcide yanıt süresinin dağılımı çok eşitsizdi: ortanca 13,6 ms iken yüzde biri 1,5 saniyeyi, en
  uzunu 2 saniyeyi geçiyordu. Sayaç yalnızca bir satır kilidiyle korunuyordu ve PostgreSQL o kilidi bekleyenleri
  geliş sırasıyla uyandırmıyor; bazı istekler defalarca öne geçiliyordu. Sayaç artışının önüne, öteki özelliklerin
  kullandığı danışma kilidi konuldu. Tablodaki satır düzeltmeden sonraki ölçümdür: verim aynı kaldı (157 işlem/sn),
  p99 1486 ms'den 239 ms'ye, en uzun yanıt 2064 ms'den 241 ms'ye indi. Ortanca yükseldi (13,6 → 202 ms), çünkü artık
  kimse sırayı atlamıyor; tek istemcide bedeli bir ek gidiş-dönüştür (9,5 → 10,0 ms). Numaranın boşluksuz olması
  gerekmiyorsa `gapless: false` kilidi tümüyle kaldırır ve eşzamanlılıkla ölçeklenir.
- **Sırada taşımanın maliyeti kapsamın büyüklüğüyle orantılı.** 100 kardeşte taşıma, eklemenin yaklaşık iki katı
  sürüyor (18 ms). Daha büyük kapsamlar ölçülmedi.

Ölçülmeyenler: olay dağıtımı (örnek mağazada kayıtlı yayıncı yok; yalnızca olayın kutuya yazılması ölçümün içinde),
veritabanı dosya deposuna büyük dosya yükleme, milyonlarca satırlık tablolar, uzun süreli çalışma ve birden çok
backend örneği.

## Bu sınamanın söylemediği
- Her satırın kendi birleşiminin uçlarının doğru davrandığı; özellikler tek tek sınandı, satırların çoğu yalnızca
  derleniyor.
- Tasarım kararlarının doğru olduğu. 15. aşamadan sonraki kararlar sorulmadan, önerilen seçenekle verildi ve
  kullanıcı tarafından ayrıca onaylanmadı; hepsi `CLAUDE.md` dosyasının "Tasarım kararları" bölümünde, bu notla
  birlikte yazılıdır.
- Üretim yükündeki davranış. Kilide dayanan özellikler tek makinede, küçük tablolarla ölçüldü
  ([Yük altında ölçüm](#yük-altında-ölçüm)); olay dağıtımı, büyük dosyalar, büyük tablolar ve birden çok sunucu
  ölçülmedi.
- Güvenliğin tam olduğu. Her aşamadan sonra yapılan incelemelerin bulguları düzeltildi; bu, bulunmamış bir
  birleşim hatası kalmadığını göstermez. Oran sınırlama ve onaylayanın yetkisi gibi konular uygulamaya bırakıldı.
- Mobil uygulamanın iOS'ta veya gerçek bir cihazda çalıştığı. Örnek mobil uygulama Expo'nun web hedefinde ve bir
  Android emülatöründe (Android 17, Expo Go), çalışan backend'e karşı denendi; iOS'ta çalıştırılmadı. Yönetim paneli
  tarayıcıda uçtan uca denendi. İkisinin de otomatik testi yoktur.
- PostgreSQL dışındaki veritabanları. Numaralama, kilitler ve benzersizlik çakışmasının tanınması PostgreSQL'e
  özgüdür.
