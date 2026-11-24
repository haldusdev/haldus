# Haldus taksonomi sınaması

Araştırma raporu ([Haldus archetype ve trait taksonomisi](Haldus%20archetype%20ve%20trait%20taksonomisi.md)) 59 gerçek
varlığı önerdiği modele eşlemiş, ama bu eşlemelerin "henüz bir üreteçle sınanmadığını" not etmişti. Bu belge o
sınamanın sonucudur: her satır, yazılmış olan API ile ifade edilmeye çalışıldı. Sınama iki kez yapıldı: ilk sürümün
sonunda (10. aşama) ve ikinci planın sonunda (21. aşama); aşağıdaki tablo ikincisinin sonucudur.

## Sonuç

59 satırın tamamı bugünkü API ile yazılıp derleniyor ve önerilen eşlemeyi eksiksiz ifade ediyor.

| Sonuç | İlk sınama | Bugün | Anlamı |
|---|---|---|---|
| Temiz | 19 | 59 | Raporun önerdiği eşleme, yazılmış özelliklerle eksiksiz ifade ediliyor |
| Kısmi | 29 | 0 | Çekirdek ifade ediliyor; önerilen eşlemedeki bir özellik yok |
| Yok | 11 | 0 | Gereken archetype veya trait yazılmadı |

Araştırmanın başlangıçtaki düz katalog için kendi sayımı 10 oturuyor, 31 zorlanarak, 18 oturmuyor idi. Üç sayım
aynı ölçüyle yapılmadı (ilki araştırmacının hükmüydü; sonraki ikisi derlenen kod), bu yüzden fark kesin bir
iyileşme ölçüsü olarak değil, yön olarak okunmalıdır.

İkinci plan boyunca satırlar şöyle kapandı:

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
| 30. Kalan satırlar | `Recurring`; varyant üretimi ve sepet birleştirme entity aksiyonu olarak | 3 |

Dört katmanlı model (bir archetype, ilişkiler, en fazla bir durum modeli, trait'ler) iki sınamadan da sağlam çıktı:
hiçbir satır iki archetype istemedi ve hiçbir satır modelin yeniden açılmasını gerektirmedi. Kapanan bütün boşluklar
yeni bir trait, ilişki bildirimi veya zincir metoduyla kapandı; kalanlar da aynı türdendir.

## Nasıl sınandı

- **Derleme:** [TaxonomyTests.cs](../tests/Haldus.Generators.Tests/TaxonomyTests.cs) her satırı, raporun önerdiği
  eşlemeye en yakın biçimde ve yalnızca yazılmış özelliklerle bildirir, generator'dan geçirir ve üretilen kodun
  hatasız derlendiğini doğrular. Aşağıdaki tablo o dosyadaki verinin aynısıdır; sayılar bir testle bağlıdır.
- **Çalışma:** Derleme, uçların doğru davrandığını göstermez. Tablodaki özelliklerin her biri örnek uygulamalarda
  gerçek PostgreSQL'e karşı sınanıyor (bugün 300'e yakın entegrasyon testi); ama her satırın kendi birleşimi değil.
  Örnek mağazada bulunmayan birleşimler (ör. iade, abonelik, rezervasyon satırlarının tam hali) yalnızca derleme
  düzeyinde doğrulandı.
- **Hüküm:** "Temiz" ile "kısmi" arasındaki ayrım benim yorumumdur: önerilen eşlemede adı geçen bir özellik
  yazılmadıysa, aynı sonuç elle (ör. bir handler içinde) elde edilebilse bile satır kısmi sayıldı. Araştırmanın bir
  satır için ayrıca not ettiği ama eşlemeye koymadığı eksikler (ör. zamanlanmış yayın) satırı kısmi yapmadı.

## Satırlar

| # | Varlık | Sonuç | Bugün nasıl yazılıyor | Eksik olan |
|---|---|---|---|---|
| 1 | Order | Temiz | `MapCrud` + `Workflow` + `Axis<PaymentState>` + `Axis<ShipmentState>` + `ChangeSets(Placed)` + `Numbered(assignOn)` + `ConcurrencyChecked` + `Auditable` + `MapDetail` + `CreatedFrom<Cart>` | |
| 2 | OrderItem / LineItem | Temiz | Gömülü `MapDetail` + `IComputed` toplamlar | — |
| 3 | Product + Variant + Option | Temiz | `MapCrud` + `Publishable` + gömülü `MapDetail` (sırası korunur) + `Batch`; varyant üretimi satırlara erişen bir entity aksiyonu | |
| 4 | Category / Collection | Temiz | `MapCrud` + `Hierarchical` + `Archivable` + `Orderable` | — |
| 5 | Cart | Temiz | `MapCrud` + `OwnedByCurrentUser` + `MapDetail` + `Expiring(sliding)` + `Cloneable`; misafir sepeti `TokenAccess`, birleştirme bir entity aksiyonu | |
| 6 | Checkout Session | Temiz | `MapCrud().Without(Delete)` + `Workflow` + `Expiring` + `Idempotent`; tamamlanınca doğan kayıt `CreatedFrom<CheckoutSession>(on: "complete")` | — |
| 7 | Coupon | Temiz | `MapCrud` + `Expiring()` + `NaturalKey` + `CreateOnly` + `IComputed` | — |
| 8 | Payment (auth/capture/void) | Temiz | `MapCrud().Without(Delete)` + `Workflow` + `Idempotent`; hareketler `MapAppendOnly().Under<Payment>()` | — |
| 9 | Refund | Temiz | `MapAppendOnly` + `Workflow` + `Idempotent` + `Metadata`; iptal bir geçiş | — |
| 10 | FulfillmentOrder | Temiz | `MapReadOnly` + `Workflow` + `Axis<FulfillmentRequest>` + geçişler | |
| 11 | Inventory level | Temiz | `MapBalance` + `Buckets(...)` (kovalar arası `move`) + `Idempotent` | |
| 12 | Invoice | Temiz | `MapCrud` + `Workflow` + `EditableIn` + `Numbered(assignOn)` + `Idempotent` + `MapDetail` + `Previewable`; ödeme durumu `Sums<InvoicePayment>()` + `IComputed` ile türetilir | |
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
| 25 | Time-off balance | Temiz | `MapBalance` + `Buckets("available", "pending", "taken")` | |
| 26 | Timesheet / attendance | Temiz | `MapCrud` + `Batch(atomic: false)`; giriş/çıkış birer entity aksiyonu | — |
| 27 | Post / Entry | Temiz | `MapCrud` + `Publishable` + `SoftDeletable` + `ConcurrencyChecked` + `Revisioned` | — |
| 28 | Localized fields | Temiz | `Translatable(...)`; yayındaki hal çevirileri taşır (`Publishable`) | |
| 29 | Revision / Snapshot | Temiz | `Revisioned` (liste, tek sürüm, geri yükleme) | — |
| 30 | Comment (threaded) | Temiz | `MapCrud` + `Hierarchical` + `SoftDeletable`; hedeflerde `Commentable<Comment>()` | — |
| 31 | Tag / Label | Temiz | `MapLookup` + `MapAssociation` (bağlar `PUT /articles/{id}/tags` ile topluca değişir) | — |
| 32 | MediaFile / Asset | Temiz | `MapFile` (boyut ve tür sınırı) + `MultiTenant` + `Metadata` | — |
| 33 | EventType / rezerve edilebilir kaynak | Temiz | `MapCrud().Without(Delete)` + `Archivable` | — |
| 34 | Availability / free-busy | Temiz | `MapQuery` | — |
| 35 | Appointment / Booking | Temiz | `MapCrud` + `Workflow` + `Idempotent` + `ConcurrencyChecked` + `Supersedes` (yeniden planlama) | — |
| 36 | Recurring event / series | Temiz | `MapCrud` + `Recurring` (kural + hesaplanan oluşlar + oluş başına istisna) | |
| 37 | Tenant / Organization | Temiz | `MapCrud` + `Auditable` | — |
| 38 | User | Temiz | `MapCrud` + `SoftDeletable` + `Metadata` + `Searchable` + entity aksiyonu | — |
| 39 | Role & permission | Temiz | `MapLookup` + `MapCrud` + `MapAssociation` | — |
| 40 | Membership | Temiz | `MapAssociation<Organization, User>()` + bağ sınıfında rol + `Workflow` ve geçişler | — |
| 41 | Invitation | Temiz | `MapCrud().Without(Update, Patch, Delete)` + `Workflow` + `Expiring` + `TokenAccess`; kabul edilince doğan üyelik `CreatedFrom<Invitation>(on: "accept")` | — |
| 42 | ApiKey / secret | Temiz | `MapCrud().Without(Update, Patch)` + `Expiring()` + `SecretOnce` + `Rotatable` | — |
| 43 | Settings / Preferences | Temiz | `MapSingleton().MultiTenant()`, `MapStore().MultiTenant()` | — |
| 44 | AuditLog / Event | Temiz | `MapReadOnly` + `Searchable` + `Retention`; dışa aktarma `MapJob().Exports<AuditLog>()` | |
| 45 | Notification | Temiz | `MapReadOnly` + `OwnedByCurrentUser` + `ReadTracking` + `Retention` | |
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
| 57 | Counter | Temiz | `Counts<Track>()` + `Sums<Track>(...)` | |
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
- **Güvenlik incelemeleri modeli değil birleşimleri düzeltti.** İkinci plan boyunca her aşamadan sonra yapılan
  incelemeler on üç bulgu verdi (biri geçerli değildi); hiçbiri bir archetype veya trait'in tanımını değiştirmedi, hepsi iki
  özelliğin kesiştiği yerdeydi (kopyanın devraldığı referans, zincir işleminin durum kuralını atlaması, aksiyonun
  onaylı içeriği değiştirmesi, etiket uçlarının yetkilendirmeyi almaması, kiracıya ait veriyi aktaran işin kiracıya
  ait olmaması). Bu, araştırmanın "kalan riskin ağırlık merkezi API yüzeyinde değil üretimin iç tarafındadır"
  tespitini doğrular.

## Kalan eksikler

| Eksik | Satırlar | Neden yazılmadı |
|---|---|---|




| Sepet birleştirme, anonim oturumun sahipliği | 5 | Alana özgü; bir handler işi |
| Kombinatoryal varyant üretimi | 3 | Alana özgü; bir aksiyon işi |
| Tekrar serisi | 36 | Araştırmada bilinçli olarak kapsam dışı |

Kalan on iki satırın hiçbiri ikinci planın kapsamında değildi: plan araştırmanın ikinci sürüm listesini, ilk
sürümden kalan küçük kalemleri ve ilk sınamada çıkan eksikleri kapsıyordu ve hepsi yazıldı.

## Bu sınamanın söylemediği

- Her satırın kendi birleşiminin uçlarının doğru davrandığı; özellikler tek tek sınandı, satırların çoğu yalnızca
  derleniyor.
- Eksik özelliklerin elle yazılmasının ne kadar zahmetli olduğu.
- Büyük veri altındaki davranış: kilide dayanan özellikler (`Orderable`, `EffectiveDated`, `DerivedFrom`, boşluksuz
  `Numbered`) ve içeriği bellekte kuran yollar (veritabanı dosya deposu, dışa aktarma) yük altında ölçülmedi.
- Frontend ve mobil tarafında üretilen şemanın (orval) kullanışlılığı; o kapsam henüz kararlaştırılmadı.