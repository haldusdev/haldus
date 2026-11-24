# Haldus taksonomi sınaması

Araştırma raporu ([Haldus archetype ve trait taksonomisi](Haldus%20archetype%20ve%20trait%20taksonomisi.md)) 59 gerçek
varlığı önerdiği modele eşlemiş, ama bu eşlemelerin "henüz bir üreteçle sınanmadığını" not etmişti. Bu belge o
sınamanın sonucudur: her satır, yazılmış olan API ile ifade edilmeye çalışıldı.

## Sonuç

59 satırın 57'si bugünkü API ile yazılıp derleniyor (45'i eksiksiz); 2'si için anlamlı bir eşleme yok. (İlk sayım 19 temiz, 29
kısmi, 11 yok idi. Entity aksiyonu yazıldıktan sonra bir satır temize geçti; sahiplik yazıldıktan sonra iki satır
temize, bir satır yoktan kısmiye geçti; alan eklentileri yazıldıktan sonra üç satır temize, bir satır yoktan
kısmiye geçti; kayıt işlemleri yazıldıktan sonra üç satır daha temize geçti; kayıtlar arası zincir yazıldıktan
sonra beş satır temize, bir satır yoktan kısmiye geçti; sırlar yazıldıktan sonra iki satır daha temize geçti;
bağlar ve kapsam yazıldıktan sonra üç satır daha, onay yazıldıktan sonra bir satır daha temize geçti; zaman ve
sürüm yazıldıktan sonra bir satır kısmiden, bir satır yoktan temize geçti; dosyalar ve takılabilirler yazıldıktan
sonra bir satır kısmiden, üç satır yoktan temize geçti.)

| Sonuç | Satır | Anlamı |
|---|---|---|
| Temiz | 45 | Raporun önerdiği eşleme, yazılmış özelliklerle eksiksiz ifade ediliyor |
| Kısmi | 12 | Çekirdek ifade ediliyor; önerilen eşlemedeki bir veya daha çok özellik henüz yok |
| Yok | 2 | Gereken archetype veya trait yazılmadı |

Araştırmanın başlangıçtaki düz katalog için kendi sayımı 10 oturuyor, 31 zorlanarak, 18 oturmuyor idi. İki sayım
aynı ölçüyle yapılmadı (o, araştırmacının hükmüydü; bu, derlenen kod), bu yüzden fark kesin bir iyileşme ölçüsü
olarak değil, yön olarak okunmalıdır.

Dört katmanlı model (bir archetype, ilişkiler, en fazla bir durum modeli, trait'ler) sınamadan sağlam çıktı:
hiçbir satır iki archetype istemedi ve hiçbir satır modelin yeniden açılmasını gerektirmedi. Kalan boşlukların
tamamı eksik trait veya archetype'tır.

## Nasıl sınandı

- **Derleme:** [TaxonomyTests.cs](../tests/Haldus.Generators.Tests/TaxonomyTests.cs) her satırı, raporun önerdiği
  eşlemeye en yakın biçimde ve yalnızca yazılmış özelliklerle bildirir, generator'dan geçirir ve üretilen kodun
  hatasız derlendiğini doğrular. Aşağıdaki tablo o dosyadaki verinin aynısıdır; sayılar bir testle bağlıdır.
- **Çalışma:** Derleme, uçların doğru davrandığını göstermez. Tablodaki birleşimlerin bir kısmı örnek uygulamalarda
  gerçek PostgreSQL'e karşı sınanıyor: sipariş, satırlar, rezervasyon, kupon, iade, sepet, ödeme, yorumlar,
  ayarlar, stok, iş, toplu işlemler, kullanıcıya ait kaynaklar, favoriler, doğal anahtar, sıralama, metadata,
  kardeşler arası sıra, kopyalama, önizleme, ters kayıt, yerine geçme, başka bir kayıttan üretme, türetme, bir kez
  gösterilen sır, döndürme, token ile erişim, bağların topluca değiştirilmesi, yük ve durum taşıyan bağ, ebeveyn
  altında tek kayıt, kısa adres, komutla yazılan satırlar, onay (tek ve çok düzeyli), belge, saklanan sürümler ve
  geri yükleme, değişiklik akışı, dönemli kayıtlar, taslak ve yayın, dosya yükleme ve indirme, birkaç kaynağa
  takılabilen ekler ve yorumlar, etiketler.
  Geri kalan satırlar yalnızca derleme düzeyinde doğrulandı.
- **Hüküm:** "Temiz" ile "kısmi" arasındaki ayrım benim yorumumdur: önerilen eşlemede adı geçen bir özellik
  yazılmadıysa, aynı sonuç elle (ör. bir handler içinde) elde edilebilse bile satır kısmi sayıldı.

## Satırlar

| # | Varlık | Sonuç | Bugün nasıl yazılıyor | Eksik olan |
|---|---|---|---|---|
| 1 | Order | Kısmi | `MapCrud` + `Workflow` + `Numbered(assignOn)` + `ConcurrencyChecked` + `Auditable` + `MapDetail` + `CreatedFrom<Cart>` | `MapChangeSet`, birden çok durum ekseni |
| 2 | OrderItem / LineItem | Temiz | Gömülü `MapDetail` + `IComputed` toplamlar | — |
| 3 | Product + Variant + Option | Kısmi | `MapCrud` + `Publishable` + gömülü `MapDetail` (sırası korunur) + `Batch` | Varyant üretimi |
| 4 | Category / Collection | Temiz | `MapCrud` + `Hierarchical` + `Archivable` + `Orderable` | — |
| 5 | Cart | Kısmi | `MapCrud` + `OwnedByCurrentUser` + `MapDetail` + `Expiring(sliding)` + `Cloneable` | Sepet birleştirme, anonim oturumun sahipliği |
| 6 | Checkout Session | Temiz | `MapCrud().Without(Delete)` + `Workflow` + `Expiring` + `Idempotent`; tamamlanınca doğan kayıt `CreatedFrom<CheckoutSession>(on: "complete")` | — |
| 7 | Coupon | Temiz | `MapCrud` + `Expiring()` + `NaturalKey` + `CreateOnly` + `IComputed` | — |
| 8 | Payment (auth/capture/void) | Temiz | `MapCrud().Without(Delete)` + `Workflow` + `Idempotent`; hareketler `MapAppendOnly().Under<Payment>()` | — |
| 9 | Refund | Temiz | `MapAppendOnly` + `Workflow` + `Idempotent` + `Metadata`; iptal bir geçiş | — |
| 10 | FulfillmentOrder | Kısmi | `MapReadOnly` + `Workflow` + geçişler | İkinci durum ekseni, böl/birleştir/taşı |
| 11 | Inventory level | Kısmi | `MapAppendOnly` hareket + `MapReadOnly().DerivedFrom<StockMovement>()` seviye | `MapBalance` (durum kovaları, kovalar arası taşıma) |
| 12 | Invoice | Kısmi | `MapCrud` + `Workflow` + `EditableIn` + `Numbered(assignOn)` + `Idempotent` + `MapDetail` + `Previewable` | Türetilmiş `paid` durumu |
| 13 | InvoiceLine | Temiz | Gömülü `MapDetail` | — |
| 14 | Credit note | Temiz | `MapAppendOnly` + `CreatedFrom<Invoice>` + `Workflow` (void) + `Numbered` | — |
| 15 | JournalEntry | Temiz | `MapCrud` + `Workflow` + `MapDetail` + `Reversible`; "satırlar dengeli" kuralı geçişte | — |
| 16 | Journals (genel defter) | Temiz | `MapReadOnly` + `Searchable` | — |
| 17 | Chart of accounts | Temiz | `MapCrud` + `Hierarchical` + `Archivable`; referanslar `Restrict` | — |
| 18 | Document numbering | Temiz | `Numbered` (boşluksuz, kiracı ve dönem başına) | — |
| 19 | Payment (muhasebe) + allocation | Temiz | `MapAppendOnly` + `References<Invoice>` + `Idempotent` + `Reversible` | — |
| 20 | StockMovement / defter kaydı | Temiz | `MapAppendOnly` + `Auditable` | — |
| 21 | Reservation | Temiz | `MapCrud` + `Workflow` + `Expiring` | — |
| 22 | Transfer | Temiz | `MapCrud` + `Workflow` + `MapDetail` + `Idempotent` + `IComputed` + `Cloneable` | — |
| 23 | Employee | Kısmi | `MapCrud` + `Searchable` + `ChangeFeed` + entity aksiyonu; geçmiş `MapCrud().Under<Employee>().EffectiveDated()` | `CustomFields` |
| 24 | LeaveRequest | Temiz | `MapCrud` + `Under<Employee>` + `Workflow` + `Approvable(levels: 2, "take")` | — |
| 25 | Time-off balance | Kısmi | `MapAppendOnly` hareket + `MapReadOnly().DerivedFrom<LeaveEntry>()` bakiye | `MapBalance` (bakiyeye karşı atomik kontrol, bekleyen talepler) |
| 26 | Timesheet / attendance | Temiz | `MapCrud` + `Batch(atomic: false)`; giriş/çıkış birer entity aksiyonu | — |
| 27 | Post / Entry | Temiz | `MapCrud` + `Publishable` + `SoftDeletable` + `ConcurrencyChecked` + `Revisioned` | — |
| 28 | Localized fields | Yok | — | `Translatable` |
| 29 | Revision / Snapshot | Temiz | `Revisioned` (liste, tek sürüm, geri yükleme) | — |
| 30 | Comment (threaded) | Temiz | `MapCrud` + `Hierarchical` + `SoftDeletable`; hedeflerde `Commentable<Comment>()` | — |
| 31 | Tag / Label | Temiz | `MapLookup` + `MapAssociation` (bağlar `PUT /articles/{id}/tags` ile topluca değişir) | — |
| 32 | MediaFile / Asset | Temiz | `MapFile` (boyut ve tür sınırı) + `MultiTenant` + `Metadata` | — |
| 33 | EventType / rezerve edilebilir kaynak | Temiz | `MapCrud().Without(Delete)` + `Archivable` | — |
| 34 | Availability / free-busy | Temiz | `MapQuery` | — |
| 35 | Appointment / Booking | Temiz | `MapCrud` + `Workflow` + `Idempotent` + `ConcurrencyChecked` + `Supersedes` (yeniden planlama) | — |
| 36 | Recurring event / series | Yok | — | Araştırmada bilinçli olarak kapsam dışı |
| 37 | Tenant / Organization | Temiz | `MapCrud` + `Auditable` | — |
| 38 | User | Temiz | `MapCrud` + `SoftDeletable` + `Metadata` + `Searchable` + entity aksiyonu | — |
| 39 | Role & permission | Temiz | `MapLookup` + `MapCrud` + `MapAssociation` | — |
| 40 | Membership | Temiz | `MapAssociation<Organization, User>()` + bağ sınıfında rol + `Workflow` ve geçişler | — |
| 41 | Invitation | Temiz | `MapCrud().Without(Update, Patch, Delete)` + `Workflow` + `Expiring` + `TokenAccess`; kabul edilince doğan üyelik `CreatedFrom<Invitation>(on: "accept")` | — |
| 42 | ApiKey / secret | Temiz | `MapCrud().Without(Update, Patch)` + `Expiring()` + `SecretOnce` + `Rotatable` | — |
| 43 | Settings / Preferences | Temiz | `MapSingleton().MultiTenant()`, `MapStore().MultiTenant()` | — |
| 44 | AuditLog / Event | Kısmi | `MapReadOnly` + `Searchable`; dışa aktarma ayrı bir `MapJob` | `Retention` |
| 45 | Notification | Kısmi | `MapReadOnly` + `OwnedByCurrentUser`; okundu işareti bir entity aksiyonu | `ReadTracking` (hepsini okundu işaretleme, okunmamış sayısı) |
| 46 | Webhook endpoint | Temiz | `MapCrud` + `Archivable` + `SecretOnce(recoverable)` + `Rotatable`; ping bir entity aksiyonu; yapılandırma `MapSingleton().Under<Webhook>()` | — |
| 47 | Webhook delivery | Temiz | `MapReadOnly` + `Under<Webhook>` + `Workflow`; yeniden gönderme bir geçiş | — |
| 48 | Subscription / Plan | Temiz | `MapCrud` + `Workflow` + `MapDetail` | — |
| 49 | Polimorfik attachment | Temiz | `MapFile`; hedeflerde `Attachable<Attachment>()` | — |
| 50 | Comment / activity feed | Temiz | `MapAppendOnly`; hedeflerde `Commentable<Activity>()` | — |
| 51 | Custom fields / metadata | Kısmi | `Metadata` (serbest anahtar-değer çantası) | `CustomFields` (kiracının tanımladığı, doğrulanan alanlar) |
| 52 | `/me` ve kullanıcıya ait kaynaklar | Temiz | `OwnedByCurrentUser()` (`/me` altında) veya `OwnedByCurrentUser(underMe: false)` | — |
| 53 | Report / aggregation | Temiz | `MapQuery` veya `MapJob` | — |
| 54 | Search | Temiz | `Searchable` | — |
| 55 | Favorite / star / vote | Temiz | `MapAssociation<Favorite, Product>().ForCurrentUser()` | — |
| 56 | Ordering / position | Temiz | `Orderable` (`Position` alanı, `POST /{id}/move`) | — |
| 57 | Counter | Kısmi | Gömülü satırlardan `IComputed` | Başka kaynaklardan toplama (`Rollup`) |
| 58 | Bulk operations | Temiz | `Batch` + `MapJob` | — |
| 59 | Büyük veri export'u | Temiz | `MapJob` + `Idempotent` | — |

## Bulgular

- **Durum makinesi, ayrı trait olarak önerilen birkaç kalıbı karşıladı.** Araştırma `Document`, tek adımlı onay ve
  "iptal et" / "yeniden gönder" gibi aksiyonlar için ayrı bildirimler öneriyordu. Bunların durumu değiştiren
  hepsi, yazılmış olan `Workflow` ve `MapTransition` ile ifade edildi (satır 9, 12, 15, 24, 47).
- **"Yalnızca eklenen detay" ayrı bir detay modu gerektirmedi.** Ödeme hareketleri `MapAppendOnly().Under<Payment>()`
  ile yazıldı (satır 8).
- **Sınamanın ilk hali, durum değiştirmeyen entity aksiyonunun eksik olduğunu gösterdi; sonradan yazıldı.** Dört
  satırda (23, 26, 38, 46) bir kayıt üzerinde çalışan ama durumunu değiştirmeyen bir uç gerekiyordu: giriş/çıkış,
  parola sıfırlama, ping. Bu, bir kaynağın builder'ında çağrılan `MapAction` ile kapatıldı; 26. satır böylece
  temize geçti, diğer üçünde başka özellikler eksik kalıyor.
- **Geçerli kullanıcıya ait kaynak dört satırı etkiliyordu** (5, 45, 52, 55); sonradan yazıldı. `OwnedByCurrentUser`
  ve `MapAssociation().ForCurrentUser()` ile 52 ve 55 temize geçti; 5 ve 45 artık sahipliği ifade ediyor, başka
  özellikleri eksik. Değer taşıyan oy (artı/eksi) yük taşıyan bağ gerektirir; o ayrı bir kalemdir.
- **Tersine çevirme üç muhasebe satırında eksikti** (14, 15, 19): bir kaydı silmek yerine onu geri alan karşı kaydı
  üretmek. `Reversible` ile yazıldı. Araştırmanın ayrı önerdiği `Reverses<T>` (başka tipten telafi kaydı) ayrı bir
  bildirim olarak eklenmedi: `CreatedFrom<T>` aynı şeyi (kaynağa işaret eden, ondan üretilen kayıt) ifade ediyor;
  14. satır bu yüzden temiz sayıldı ve bu benim yorumumdur.
- **Gizli değerler üç satırda eksikti** (41, 42, 46): yalnızca bir kez gösterilen anahtar, döndürme, token ile erişim.
  `SecretOnce`, `Rotatable` ve `TokenAccess` ile yazıldı. Araştırma sırrın yalnızca özetinin saklanmasını
  öneriyordu; webhook imza sırrı gibi sunucunun kendisinin kullandığı sırlar özetten geri okunamadığı için
  şifreli saklama (`recoverable: true`) ayrıca eklendi.
- **Onay ayrı bir trait olarak yazıldı; belge ise durum makinesinin hazır bir biçimi oldu.** `Approvable` karar
  kaydını, "kendi yazdığını onaylayamaz" kuralını ve sıralı düzeyleri getirir; 24. satır bununla temize geçti.
  Raporun önerdiği onay zinciri (düzey başına farklı roller, koşullu adımlar) yazılmadı: düzeyler yalnızca
  "birbirinden farklı N kişi" demektir. `.Document()` (taslak → gönder → iptal → düzelt) yeni bir satır kapatmadı,
  çünkü o satırlar (12, 15) zaten `Workflow` ile yazılabiliyordu; aynı şeyin kısa yazımıdır.
- **Zaman dört ayrı biçimde ortaya çıktı** ve dördü de ayrı yazıldı: kaydın geçmiş halleri (`Revisioned`, 27 ve 29),
  taslağın yanında yayındaki hal (`Publishable`, 3 ve 27), iş tarafının beyan ettiği geçerlilik dönemi
  (`EffectiveDated`, 23) ve "şundan beri ne değişti" akışı (`ChangeFeed`, 23). Araştırmanın 27. satır için ayrıca
  not ettiği zamanlanmış yayın yazılmadı; önerilen eşlemede adı geçmediği için satır temiz sayıldı, bu benim
  yorumumdur.
- **Polimorfik referans, tip + kimlik çifti olarak değil hedef başına yabancı anahtar olarak yazıldı.** Araştırma
  `.AttachedTo()` için Rails / Laravel tarzı "tip + id" alanlarını örnek gösteriyordu. Hedefler derleme zamanında
  bilindiği için her hedefe isteğe bağlı gerçek bir yabancı anahtar üretildi ve "tam olarak biri dolu" kuralı
  veritabanı kısıtı oldu; böylece takılı kaydın görünürlüğü ve zincirleme silinmesi, tek ebeveynli kaynaklarla aynı
  yoldan gelir. `Attachable`, `Commentable` ve `Taggable` ayrı altyapı değil, aynı bildirimin hedef tarafında
  yazılan kısa biçimleridir (30, 32, 49 ve 50. satırlar bunlarla kapandı).
- **Bağın kendisi üç satırda eksikti** (31, 40, 46): bağların topluca değiştirilmesi, üyelik gibi kendi alanı ve
  durumu olan bağ, bir kaydın altındaki tek yapılandırma kaydı. Üçü de yazıldı. Durumu olan bağın geçişleri
  gövdesizdir (iş mantığı taşımaz); mantık gerektiren bir üyelik hâlâ `MapCrud` + iki `References` ile yazılır.

## Eksikler, etkiledikleri satır sayısına göre

| Eksik | Satırlar |
|---|---|
| `CustomFields` | 23, 51 |

| `MapBalance` | 11, 25 |

| Birden çok durum ekseni | 1, 10 |
| Tek satırı etkileyenler | `MapChangeSet` (1), türetilmiş durum (12), sepet birleştirme (5), `Translatable` (28), `Retention` (44), `ReadTracking` (45), `Rollup` (57), varyant üretimi (3) |

Araştırma bunların çoğunu zaten ikinci ve üçüncü sürüme bırakmıştı. Bu sınamanın o sıralamaya eklediği tek şey,
ilk sürümün kapsamındaki satırlarda da gereken entity aksiyonuydu; o artık yazıldı. Dört satırı etkileyen sahiplik
(`OwnedByCurrentUser`, geçerli kullanıcıya bağlı bağ) da yazıldı ve bu tablodan çıktı. Ardından alan eklentileri
(`Metadata`, `CreateOnly`, `NaturalKey`) yazıldı; `Metadata`'nın kapattığı satırlar tablodan çıktı, kiracının
tanımladığı alanlar (`CustomFields`) ayrı bir kalem olarak kaldı.

## Bu sınamanın söylemediği

- Sınanmayan satırların uçlarının doğru davrandığı; onlar yalnızca derleniyor.
- Eksik özelliklerin elle yazılmasının ne kadar zahmetli olduğu.
- Frontend ve mobil tarafında üretilen şemanın (orval) kullanışlılığı; o kapsam henüz kararlaştırılmadı.
