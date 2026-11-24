# Haldus taksonomi sınaması

Araştırma raporu ([Haldus archetype ve trait taksonomisi](Haldus%20archetype%20ve%20trait%20taksonomisi.md)) 59 gerçek
varlığı önerdiği modele eşlemiş, ama bu eşlemelerin "henüz bir üreteçle sınanmadığını" not etmişti. Bu belge o
sınamanın sonucudur: her satır, yazılmış olan API ile ifade edilmeye çalışıldı.

## Sonuç

59 satırın 50'si bugünkü API ile yazılıp derleniyor; 9'u için anlamlı bir eşleme yok. (İlk sayım 19 temiz, 29
kısmi, 11 yok idi. Entity aksiyonu yazıldıktan sonra bir satır temize geçti; sahiplik yazıldıktan sonra iki satır
temize, bir satır yoktan kısmiye geçti.)

| Sonuç | Satır | Anlamı |
|---|---|---|
| Temiz | 22 | Raporun önerdiği eşleme, yazılmış özelliklerle eksiksiz ifade ediliyor |
| Kısmi | 28 | Çekirdek ifade ediliyor; önerilen eşlemedeki bir veya daha çok özellik henüz yok |
| Yok | 9 | Gereken archetype veya trait yazılmadı |

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
  ayarlar, stok, iş, toplu işlemler, kullanıcıya ait kaynaklar ve favoriler. Geri kalan satırlar yalnızca derleme
  düzeyinde doğrulandı.
- **Hüküm:** "Temiz" ile "kısmi" arasındaki ayrım benim yorumumdur: önerilen eşlemede adı geçen bir özellik
  yazılmadıysa, aynı sonuç elle (ör. bir handler içinde) elde edilebilse bile satır kısmi sayıldı.

## Satırlar

| # | Varlık | Sonuç | Bugün nasıl yazılıyor | Eksik olan |
|---|---|---|---|---|
| 1 | Order | Kısmi | `MapCrud` + `Workflow` + `Numbered(assignOn)` + `ConcurrencyChecked` + `Auditable` + `MapDetail` | `CreatedFrom<Cart>`, `MapChangeSet`, birden çok durum ekseni |
| 2 | OrderItem / LineItem | Temiz | Gömülü `MapDetail` + `IComputed` toplamlar | — |
| 3 | Product + Variant + Option | Kısmi | `MapCrud` + gömülü `MapDetail` (sırası korunur) + `Batch` | `Publishable`, varyant üretimi |
| 4 | Category / Collection | Kısmi | `MapCrud` + `Hierarchical` + `Archivable` | Kardeşler arası sıra (`Orderable`) |
| 5 | Cart | Kısmi | `MapCrud` + `OwnedByCurrentUser` + `MapDetail` + `Expiring(sliding)` | `Cloneable`, sepet birleştirme, anonim oturumun sahipliği |
| 6 | Checkout Session | Kısmi | `MapCrud().Without(Delete)` + `Workflow` + `Expiring` + `Idempotent` | `Creates<T>` (handler içinde elle) |
| 7 | Coupon | Kısmi | `MapCrud` + `Expiring()` + `IComputed` | Yalnızca oluştururken yazılan alan, doğal anahtar |
| 8 | Payment (auth/capture/void) | Temiz | `MapCrud().Without(Delete)` + `Workflow` + `Idempotent`; hareketler `MapAppendOnly().Under<Payment>()` | — |
| 9 | Refund | Kısmi | `MapAppendOnly` + `Workflow` + `Idempotent`; iptal bir geçiş | `Metadata` |
| 10 | FulfillmentOrder | Kısmi | `MapReadOnly` + `Workflow` + geçişler | İkinci durum ekseni, böl/birleştir/taşı |
| 11 | Inventory level | Kısmi | `MapAppendOnly` hareket + `MapReadOnly` seviye | `MapBalance`, türetme bildirimi |
| 12 | Invoice | Kısmi | `MapCrud` + `Workflow` + `EditableIn` + `Numbered(assignOn)` + `Idempotent` + `MapDetail` | `Previewable`, türetilmiş `paid` durumu |
| 13 | InvoiceLine | Temiz | Gömülü `MapDetail` | — |
| 14 | Credit note | Kısmi | `MapAppendOnly` + `References<Invoice>` + `Workflow` (void) + `Numbered` | `Reverses<Invoice>` |
| 15 | JournalEntry | Kısmi | `MapCrud` + `Workflow` + `MapDetail`; "satırlar dengeli" kuralı geçişte | `Reversible` |
| 16 | Journals (genel defter) | Temiz | `MapReadOnly` + `Searchable` | — |
| 17 | Chart of accounts | Temiz | `MapCrud` + `Hierarchical` + `Archivable`; referanslar `Restrict` | — |
| 18 | Document numbering | Temiz | `Numbered` (boşluksuz, kiracı ve dönem başına) | — |
| 19 | Payment (muhasebe) + allocation | Kısmi | `MapAppendOnly` + `References<Invoice>` + `Idempotent` | `Reversible` |
| 20 | StockMovement / defter kaydı | Temiz | `MapAppendOnly` + `Auditable` | — |
| 21 | Reservation | Temiz | `MapCrud` + `Workflow` + `Expiring` | — |
| 22 | Transfer | Kısmi | `MapCrud` + `Workflow` + `MapDetail` + `Idempotent` + `IComputed` | `Cloneable` |
| 23 | Employee | Kısmi | `MapCrud` + `Searchable` + entity aksiyonu; geçmiş `MapDetail().AsSubresource()` | `CustomFields`, `ChangeFeed`, `EffectiveDated` |
| 24 | LeaveRequest | Kısmi | `MapCrud` + `Under<Employee>` + `Workflow` (onayla/reddet) | `Approvable` (çok adımlı onay) |
| 25 | Time-off balance | Yok | — | `MapBalance` |
| 26 | Timesheet / attendance | Temiz | `MapCrud` + `Batch(atomic: false)`; giriş/çıkış birer entity aksiyonu | — |
| 27 | Post / Entry | Kısmi | `MapCrud` + `SoftDeletable` + `ConcurrencyChecked` + `Workflow` (yayınla) | `Publishable`, `Revisioned` |
| 28 | Localized fields | Yok | — | `Translatable` |
| 29 | Revision / Snapshot | Yok | — | `Revisioned` |
| 30 | Comment (threaded) | Kısmi | `MapCrud` + `Under<T>` + `Hierarchical` + `SoftDeletable` | Her kaynağa takılabilen yorum (`Commentable`) |
| 31 | Tag / Label | Kısmi | `MapLookup` + `MapAssociation` | Bağların topluca değiştirilmesi |
| 32 | MediaFile / Asset | Yok | — | `MapFile` |
| 33 | EventType / rezerve edilebilir kaynak | Temiz | `MapCrud().Without(Delete)` + `Archivable` | — |
| 34 | Availability / free-busy | Temiz | `MapQuery` | — |
| 35 | Appointment / Booking | Kısmi | `MapCrud` + `Workflow` + `Idempotent` + `ConcurrencyChecked` | `Supersedes` (yeniden planlama) |
| 36 | Recurring event / series | Yok | — | Araştırmada bilinçli olarak kapsam dışı |
| 37 | Tenant / Organization | Temiz | `MapCrud` + `Auditable` | — |
| 38 | User | Kısmi | `MapCrud` + `SoftDeletable` + `Searchable` + entity aksiyonu | `Metadata` |
| 39 | Role & permission | Temiz | `MapLookup` + `MapCrud` + `MapAssociation` | — |
| 40 | Membership | Kısmi | `MapCrud` + iki `References` + `Workflow` | Yük taşıyan ve durumu olan `MapAssociation` |
| 41 | Invitation | Kısmi | `MapCrud().Without(Update, Patch, Delete)` + `Workflow` + `Expiring` | `TokenAccess`, `Creates<Membership>` |
| 42 | ApiKey / secret | Kısmi | `MapCrud().Without(Update, Patch)` + `Expiring()` | `SecretOnce`, `Rotatable` |
| 43 | Settings / Preferences | Temiz | `MapSingleton().MultiTenant()`, `MapStore().MultiTenant()` | — |
| 44 | AuditLog / Event | Kısmi | `MapReadOnly` + `Searchable`; dışa aktarma ayrı bir `MapJob` | `Retention` |
| 45 | Notification | Kısmi | `MapReadOnly` + `OwnedByCurrentUser`; okundu işareti bir entity aksiyonu | `ReadTracking` (hepsini okundu işaretleme, okunmamış sayısı) |
| 46 | Webhook endpoint | Kısmi | `MapCrud` + `Archivable`; ping bir entity aksiyonu | `SecretOnce`, ebeveyn altında tek kayıt |
| 47 | Webhook delivery | Temiz | `MapReadOnly` + `Under<Webhook>` + `Workflow`; yeniden gönderme bir geçiş | — |
| 48 | Subscription / Plan | Temiz | `MapCrud` + `Workflow` + `MapDetail` | — |
| 49 | Polimorfik attachment | Yok | — | `Attachable`, `MapFile` |
| 50 | Comment / activity feed | Yok | — | `Commentable` |
| 51 | Custom fields / metadata | Yok | — | `Metadata`, `CustomFields` |
| 52 | `/me` ve kullanıcıya ait kaynaklar | Temiz | `OwnedByCurrentUser()` (`/me` altında) veya `OwnedByCurrentUser(underMe: false)` | — |
| 53 | Report / aggregation | Temiz | `MapQuery` veya `MapJob` | — |
| 54 | Search | Temiz | `Searchable` | — |
| 55 | Favorite / star / vote | Temiz | `MapAssociation<Favorite, Product>().ForCurrentUser()` | — |
| 56 | Ordering / position | Yok | — | `Orderable` (yalnızca gömülü satırlar sıra tutar) |
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
- **Tersine çevirme üç muhasebe satırında eksik** (14, 15, 19): bir kaydı silmek yerine onu geri alan karşı kaydı
  üretmek.
- **Gizli değerler üç satırda eksik** (41, 42, 46): yalnızca bir kez gösterilen anahtar, döndürme, token ile erişim.
  Bunlar olmadan API anahtarı ve webhook sırrı güvenli biçimde üretilemiyor; elle yazılmaları gerekir.

## Eksikler, etkiledikleri satır sayısına göre

| Eksik | Satırlar |
|---|---|
| `Metadata` / `CustomFields` | 9, 23, 38, 51 |
| `Reversible` / `Reverses` | 14, 15, 19 |
| `SecretOnce` / `Rotatable` / `TokenAccess` | 41, 42, 46 |
| `Publishable` | 3, 27 |
| `Revisioned` | 27, 29 |
| `Orderable` | 4, 56 |
| `Cloneable` | 5, 22 |
| `Creates<T>` / `CreatedFrom<T>` | 1, 6, 41 |
| `MapBalance` | 11, 25 |
| `MapFile` / `Attachable` | 32, 49 |
| `Commentable` | 30, 50 |
| Birden çok durum ekseni | 1, 10 |
| Tek satırı etkileyenler | `MapChangeSet` (1), `Previewable` (12), `Approvable` (24), `Translatable` (28), `Supersedes` (35), yüklü ve durumlu bağ (40), bağların topluca değiştirilmesi (31), `Retention` (44), `ReadTracking` (45), `Rollup` (57), `EffectiveDated` ve `ChangeFeed` (23) |

Araştırma bunların çoğunu zaten ikinci ve üçüncü sürüme bırakmıştı. Bu sınamanın o sıralamaya eklediği tek şey,
ilk sürümün kapsamındaki satırlarda da gereken entity aksiyonuydu; o artık yazıldı. Dört satırı etkileyen sahiplik
(`OwnedByCurrentUser`, geçerli kullanıcıya bağlı bağ) da yazıldı ve bu tablodan çıktı.

## Bu sınamanın söylemediği

- Sınanmayan satırların uçlarının doğru davrandığı; onlar yalnızca derleniyor.
- Eksik özelliklerin elle yazılmasının ne kadar zahmetli olduğu.
- Frontend ve mobil tarafında üretilen şemanın (orval) kullanışlılığı; o kapsam henüz kararlaştırılmadı.
