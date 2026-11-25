# Bildirim dizini

Haldus'un herkese açık bütün bildirimleri: kaynak eşleyen `Map*` çağrıları, zincir metotları ve servis kayıtları.
Her satır, bildirimi ayrıntısıyla anlatan sayfaya gider. Bu liste testle denetlenir: herkese açık bir bildirim buraya
yazılmadan eklenemez.

Sütunlar:

- **Nereye:** bildirimin yazıldığı yer.
  - `app`: `WebApplication` / `IEndpointRouteBuilder`.
  - `zincir`: bir `Map*` çağrısının devamı.
  - `builder`: `Map*` çağrısının döndürdüğü ve bir değişkende tutulan builder (`orders.MapTransition(...)`).
  - `detay`: `MapDetail` çağrısının devamı.
  - `bağ`: `MapAssociation` çağrısının devamı.
  - `services`: `builder.Services`.

## İçindekiler

- [Kaynak eşleme](#kaynak-eşleme)
- [Kaynaklar arası ilişkiler](#kaynaklar-arası-ilişkiler)
- [Durum ve iş mantığı](#durum-ve-iş-mantığı)
- [Okuma](#okuma)
- [Kapsam ve sahiplik](#kapsam-ve-sahiplik)
- [Alanlar ve anahtarlar](#alanlar-ve-anahtarlar)
- [Yaşam döngüsü](#yaşam-döngüsü)
- [Kayıt işlemleri](#kayıt-işlemleri)
- [Kayıtlar arası zincir](#kayıtlar-arası-zincir)
- [Onay](#onay)
- [Sürüm, yayın ve okundu bilgisi](#sürüm-yayın-ve-okundu-bilgisi)
- [Toplanan alanlar](#toplanan-alanlar)
- [Dosyalar ve takılabilirler](#dosyalar-ve-takılabilirler)
- [Arka plan işleri](#arka-plan-işleri)
- [Bakiyeler](#bakiyeler)
- [Sırlar](#sırlar)
- [Olaylar](#olaylar)
- [Servis kayıtları](#servis-kayıtları)

## Kaynak eşleme

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `MapCrud<T>()` | `app` | Liste, tekil, oluşturma, `PUT`, `PATCH`, silme. | [Archetype'lar](../guides/archetypes.md#mapcrud) |
| `MapReadOnly<T>()` | `app` | Liste ve tekil okuma. | [Archetype'lar](../guides/archetypes.md#mapreadonly) |
| `MapAppendOnly<T>()` | `app` | Liste, tekil, oluşturma; güncelleme ve silme yok. | [Archetype'lar](../guides/archetypes.md#mapappendonly) |
| `MapStore<T>()` | `app` | Anahtarı istemcinin verdiği kayıt: `PUT /{key}` ekler veya değiştirir. | [Archetype'lar](../guides/archetypes.md#mapstore) |
| `MapLookup<T>()` | `app` | Kodlu küçük liste (`Code`, `Name`); sayfalamasız. | [Archetype'lar](../guides/archetypes.md#maplookup) |
| `MapSingleton<T>()` | `app` | Tek kayıt: `GET`, `PUT`, `PATCH`. | [Archetype'lar](../guides/archetypes.md#mapsingleton) |
| `MapFile<T>(maxBytes, contentTypes)` | `app` | İçeriği ayrıca yüklenen kaynak. | [Dosyalar](../guides/files.md) |
| `MapJob<T>()` | `app` | Uzun süren iş; oluşturma 202 döner. | [Arka plan işleri](../guides/jobs.md) |
| `MapBalance<T>()` | `app` | Yalnızca komutlarla değişen nicelik ve defteri. | [Bakiyeler](../guides/balances.md) |
| `MapAssociation<TLeft, TRight>()` | `app` | Çoktan-çoğa bağ. | [İlişkiler](../guides/relationships.md#çoktan-çoğa-bağ) |
| `MapAction<TRequest, TResponse>()` | `app` | Entity'ye bağlı olmayan `POST` komutu. | [İş mantığı](../guides/business-logic.md#serbest-aksiyonlar-ve-sorgular) |
| `MapQuery<TRequest, TResponse>()` | `app` | Entity'ye bağlı olmayan `GET` sorgusu. | [İş mantığı](../guides/business-logic.md#serbest-aksiyonlar-ve-sorgular) |
| `MapDetail<T>()` | `builder` | Sahibine ait satırlar (sipariş satırları). | [Detaylar](../guides/details-and-computed-fields.md) |
| `AsSubresource()` | `detay` | Satırları kendi uçlarına taşır. | [Detaylar](../guides/details-and-computed-fields.md) |
| `AsCommands()` | `detay` | Satırları tek tek komutlarla yazdırır. | [Detaylar](../guides/details-and-computed-fields.md) |
| `Without(...)`, `Only(...)` | `zincir` | Üretilecek standart işlemleri seçer. | [Kayıt işlemleri](../guides/record-operations.md#standart-işlemleri-seçmek) |

## Kaynaklar arası ilişkiler

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `References<T>()` | `zincir` | Yabancı anahtar; hedefin varlığı her yazmada doğrulanır. | [İlişkiler](../guides/relationships.md#referans) |
| `Under<T>()` | `zincir` | Kaynak ebeveyninin adresi altında yaşar. | [İlişkiler](../guides/relationships.md#ebeveyn-altında) |
| `Hierarchical()` | `zincir` | `ParentId` ile ağaç. | [İlişkiler](../guides/relationships.md#ağaç) |
| `Embeds<T>()` | `zincir` | Gömülü değer nesnesi. | [İlişkiler](../guides/relationships.md#gömülü-değer-nesnesi) |
| `ForCurrentUser()` | `bağ` | Bağı geçerli kullanıcıya bağlar (`/me/favorites`). | [İlişkiler](../guides/relationships.md#kullanıcıya-bağlı-bağ) |
| `Workflow<TStatus>()` | `bağ` | Bağa durum verir. | [İlişkiler](../guides/relationships.md#durumu-olan-bağ) |
| `MapTransition(...)` | `bağ` | Bağın durum geçişi. | [İlişkiler](../guides/relationships.md#durumu-olan-bağ) |
| `TypedBy<TType>()` | `zincir` | Tip kaydına bağlar; varsayılanları ondan alır. | [İlişkiler](../guides/relationships.md#tip-ve-örnek) |
| `RoleOf<TPlayer>()` | `zincir` | Kaydı bir oyuncunun rolü yapar. | [İlişkiler](../guides/relationships.md#rol) |
| `SubtypeOf<TBase>()` | `zincir` | Alt tip (tek tablo kalıtımı). | [İlişkiler](../guides/relationships.md#alt-tip) |

## Durum ve iş mantığı

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `Workflow<TStatus>(initial)` | `zincir` | Durum makinesi; `Status` alanı. | [Durum makineleri](../guides/state-machines.md) |
| `EditableIn(...)` | `zincir` | `PUT` / `PATCH` yalnızca sayılan durumlarda. | [Durum makineleri](../guides/state-machines.md) |
| `DeletableIn(...)` | `zincir` | Silme yalnızca sayılan durumlarda. | [Durum makineleri](../guides/state-machines.md) |
| `MapTransition(...)` | `builder` | Bir durumdan ötekine geçiş ucu. | [Durum makineleri](../guides/state-machines.md) |
| `Axis<TStatus>()` | `zincir` | Asıl durumdan bağımsız ek durum ekseni. | [Durum makineleri](../guides/state-machines.md#ek-durum-eksenleri) |
| `Document()` | `zincir` | Hazır belge durumu: taslak, gönderilmiş, iptal. | [Onay ve belge](../guides/approvals.md#belge) |
| `MapAction(...)` | `builder` | Kayıt üzerinde çalışan, durumunu değiştirmeyen aksiyon. | [İş mantığı](../guides/business-logic.md#entity-aksiyonları) |

## Okuma

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `Filterable<TFilter>()` | `zincir` | Kendi filtre sınıfınızı liste ucuna bağlar. | [Listeleme](../guides/querying.md#süzme) |
| `Sortable(...)` | `zincir` | `?sort=-createdAt,name`. | [Listeleme](../guides/querying.md#sıralama) |
| `Searchable(...)` | `zincir` | `POST /search`: tipli koşullar, `and` / `or` / `not`. | [Listeleme](../guides/querying.md#arama) |
| `NaturalKey(...)` | `zincir` | Benzersiz alan ve `GET /by-{alan}/{değer}`. | [Listeleme](../guides/querying.md#doğal-anahtar) |

## Kapsam ve sahiplik

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `MultiTenant()` | `zincir` | `TenantId`; kayıtlar kiracıya göre süzülür. | [Kiracılar](../guides/tenancy-and-ownership.md#çok-kiracılı-kaynaklar) |
| `OwnedByCurrentUser()` | `zincir` | `OwnerId`; kayıt onu oluşturana aittir (`/me`). | [Kiracılar](../guides/tenancy-and-ownership.md#kullanıcıya-ait-kaynaklar) |

## Alanlar ve anahtarlar

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `Auditable()` | `zincir` | Oluşturma ve değiştirme zamanı ve kullanıcısı. | [Alanlar](../guides/fields.md#denetim-alanları) |
| `CreateOnly(...)` | `zincir` | Alan yalnızca oluştururken yazılır. | [Alanlar](../guides/fields.md#yalnızca-oluştururken-yazılan-alanlar) |
| `KeyStrategy(...)` | `zincir` | Anahtarın biçimi: GUID, önekli, ULID, istemcinin verdiği. | [Alanlar](../guides/fields.md#anahtar-biçimi) |
| `Numbered(pattern)` | `zincir` | Boşluksuz numara (`INV-2026-000042`). | [Alanlar](../guides/fields.md#numaralar) |
| `Metadata()` | `zincir` | Serbest anahtar-değer çantası. | [Alanlar](../guides/fields.md#metadata) |
| `CustomFields()` | `zincir` | Kiracının tanımladığı özel alanlar. | [Alanlar](../guides/fields.md#özel-alanlar) |
| `Translatable(...)` | `zincir` | Alanların başka dillere çevirileri. | [Alanlar](../guides/fields.md#çeviriler) |
| `Sensitive(...)` | `zincir`, `detay` | Yalnızca yazılan alan; hiçbir yanıtta görünmez. | [Hassas alanlar](../guides/sensitive-data.md) |
| `Encrypted(...)` | `zincir`, `detay` | Veritabanında şifreli saklanan, yalnızca yazılan alan. | [Hassas alanlar](../guides/sensitive-data.md) |

## Yaşam döngüsü

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `SoftDeletable()` | `zincir` | Geri alınabilir silme; `restore`, `purge`. | [Yaşam döngüsü](../guides/lifecycle.md#geri-alınabilir-silme) |
| `Archivable()` | `zincir` | `archive`, `unarchive`. | [Yaşam döngüsü](../guides/lifecycle.md#arşivleme) |
| `ConcurrencyChecked()` | `zincir` | `ETag` / `If-Match` ile iyimser eşzamanlılık. | [Yaşam döngüsü](../guides/lifecycle.md#eşzamanlılık-denetimi) |
| `Expiring(...)` | `zincir` | Süresi dolan kayıt gizlenir ve yazılamaz. | [Yaşam döngüsü](../guides/lifecycle.md#süresi-dolan-kayıtlar) |
| `Retention(period)` | `zincir` | Eski kayıtlar bakımda silinir. | [Yaşam döngüsü](../guides/lifecycle.md#saklama-süresi) |
| `Idempotent()` | `zincir` | `Idempotency-Key` ile tekrara karşı koruma. | [Yaşam döngüsü](../guides/lifecycle.md#tekrara-karşı-koruma) |
| `EffectiveDated(...)` | `zincir` | Geçerlilik dönemleri; çakışma 409. | [Yaşam döngüsü](../guides/lifecycle.md#geçerlilik-dönemleri) |
| `Recurring()` | `zincir` | Tekrar serisi ve oluşları. | [Yaşam döngüsü](../guides/lifecycle.md#tekrar-serisi) |

## Kayıt işlemleri

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `Batch()` | `zincir` | Toplu okuma, oluşturma, güncelleme, silme. | [Kayıt işlemleri](../guides/record-operations.md#toplu-işlemler) |
| `Cloneable()` | `zincir` | `POST /{id}/clone`. | [Kayıt işlemleri](../guides/record-operations.md#kopyalama) |
| `Orderable()` | `zincir` | `Position` ve `POST /{id}/move`. | [Kayıt işlemleri](../guides/record-operations.md#sıralı-kayıtlar) |
| `Previewable()` | `zincir` | Kaydetmeden sonucu gösteren önizleme. | [Kayıt işlemleri](../guides/record-operations.md#önizleme) |
| `Purgeable()` | `zincir` | Ölçüte uyan kayıtları silme. | [Kayıt işlemleri](../guides/record-operations.md#ölçüte-göre-silme) |
| `ChangeSets(...)` | `zincir` | Düzenlenemeyen kaydı bir değişiklik kümesiyle düzeltme. | [Kayıt işlemleri](../guides/record-operations.md#değişiklik-kümeleri) |

## Kayıtlar arası zincir

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `Reversible(...)` | `zincir` | Kaydı nötrleyen ters kayıt. | [Kayıtlar arası zincir](../guides/record-chains.md#ters-kayıt) |
| `Supersedes(...)` | `zincir` | Eskisinin yerine geçen yeni kayıt. | [Kayıtlar arası zincir](../guides/record-chains.md#yerine-geçen-kayıt) |
| `CreatedFrom<TSource>()` | `zincir` | Kaydı başka bir kayıttan üretir. | [Kayıtlar arası zincir](../guides/record-chains.md#başka-bir-kayıttan-üretmek) |
| `DerivedFrom<TSource>()` | `zincir` | Salt-okunur izdüşüm. | [Kayıtlar arası zincir](../guides/record-chains.md#türetilmiş-kaynak) |

## Onay

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `Approvable(levels)` | `zincir` | Onay ve ret uçları; içerik değişince onay düşer. | [Onay ve belge](../guides/approvals.md#onay) |
| `ApprovalRoles(...)` | `zincir` | Onay düzeylerini rollere bağlar. | [Onay ve belge](../guides/approvals.md#onay-zinciri) |

## Sürüm, yayın ve okundu bilgisi

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `Revisioned()` | `zincir` | Her yazmanın anlık görüntüsü ve geri yükleme. | [Sürümler](../guides/history-and-publishing.md#sürümler) |
| `Publishable()` | `zincir` | Taslak ve yayındaki hal. | [Sürümler](../guides/history-and-publishing.md#taslak-ve-yayın) |
| `ScheduledPublishing()` | `zincir` | İleri tarihli yayın. | [Sürümler](../guides/history-and-publishing.md#zamanlanmış-yayın) |
| `ChangeFeed()` | `zincir` | `GET /changes?since=`. | [Sürümler](../guides/history-and-publishing.md#değişiklik-akışı) |
| `ReadTracking()` | `zincir` | Kullanıcıya ait kayıtta okundu bilgisi. | [Sürümler](../guides/history-and-publishing.md#okundu-takibi) |
| `ReadReceipts()` | `zincir` | Paylaşılan kayıtta kullanıcı başına okundu bilgisi. | [Sürümler](../guides/history-and-publishing.md#okundu-işaretleri) |

## Toplanan alanlar

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `Counts<TChild>()` | `zincir` | Alt kayıtların sayısı. | [Toplanan alanlar](../guides/rollups.md) |
| `Sums<TChild>(...)` | `zincir` | Alt kayıtlardaki bir alanın toplamı. | [Toplanan alanlar](../guides/rollups.md) |

## Dosyalar ve takılabilirler

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `SignedLinks()` | `zincir` | İçeriğe süreli, oturumsuz bağlantılar. | [Dosyalar](../guides/files.md#süreli-bağlantılar) |
| `AttachedTo<T>()` | `zincir` | Kaynak birden çok hedefe takılabilir. | [Dosyalar](../guides/files.md#takılabilir-kaynaklar) |
| `Attachable<TFile>()` | `zincir` | Hedef tarafından: dosyalar bu kaynağa takılabilir. | [Dosyalar](../guides/files.md#takılabilir-kaynaklar) |
| `Commentable<TComment>()` | `zincir` | Hedef tarafından: yorumlar bu kaynağa takılabilir. | [Dosyalar](../guides/files.md#takılabilir-kaynaklar) |
| `Taggable<TTag>()` | `zincir` | Etiket bağı. | [Dosyalar](../guides/files.md#etiketler) |

## Arka plan işleri

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `Rerunnable()` | `zincir` | Bitmiş işi aynı girdilerle yeniden çalıştırır. | [Arka plan işleri](../guides/jobs.md#yeniden-çalıştırma) |
| `Exports<TResource>()` | `zincir` | İş, bir kaynağı CSV veya JSON'a döker. | [Arka plan işleri](../guides/jobs.md#dışa-aktarma) |
| `Imports<TResource>()` | `zincir` | İş, satırlardan kayıt oluşturur. | [Arka plan işleri](../guides/jobs.md#satırları-içe-aktarma) |

## Bakiyeler

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `Buckets(...)` | `zincir` | Bakiyeyi adlandırılmış kovalara böler. | [Bakiyeler](../guides/balances.md#kovalar) |
| `AllowNegative()` | `zincir` | Bakiyenin sıfırın altına düşmesine izin verir. | [Bakiyeler](../guides/balances.md#negatif-bakiye) |
| `Holds(from, to)` | `zincir` | Tutma (rezervasyon) uçları. | [Bakiyeler](../guides/balances.md#tutmalar) |

## Sırlar

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `SecretOnce(prefix)` | `zincir` | Bir kez gösterilen sunucu sırrı (API anahtarı). | [Sırlar](../guides/secrets.md#bir-kez-gösterilen-sır) |
| `Rotatable()` | `zincir` | Sırrı döndürme. | [Sırlar](../guides/secrets.md#döndürme) |
| `TokenAccess(prefix, ...)` | `zincir` | Oturumsuz, token ile erişim. | [Sırlar](../guides/secrets.md#token-ile-erişim) |
| `TokenOnly()` | `zincir` | Kayda yalnızca token ile ulaşılır. | [Sırlar](../guides/secrets.md#yalnızca-token) |

## Olaylar

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `PublishesEvents()` | `zincir` | Kayıt değiştikçe olay yazılır ve teslim edilir. | [Olay yayını](../guides/events.md) |

## Servis kayıtları

| Bildirim | Nereye | Ne yapar | Sayfa |
|---|---|---|---|
| `AddHaldus<TContext>()` | `services` | Haldus'u ortak bağlamla kaydeder. | [Yapılandırma](configuration.md) |
| `AddHaldusModule<TContext>()` | `services` | Bir modülün kendi bağlamını bildirir. | [Modüler yapı](../guides/modules.md) |
| `UseHaldus()` | `app` | İsteğin dilini çözer. | [Yapılandırma](configuration.md) |
| `AddHaldusFileSystemStorage(root)` | `services` | Dosya içeriklerini bir klasörde saklar. | [Dosyalar](../guides/files.md#depolama) |
| `AddHaldusS3Storage(...)` | `services` | Dosya içeriklerini S3 uyumlu depoda saklar. | [Dosyalar](../guides/files.md#depolama) |
| `AddHaldusInMemoryJobs()` | `services` | Bellek içi iş kuyruğu. | [Arka plan işleri](../guides/jobs.md#altyapı) |
| `AddHaldusPostgreSqlJobs<TContext>()` | `services` | PostgreSQL iş kuyruğu. | [Arka plan işleri](../guides/jobs.md#altyapı) |
| `AddHaldusHangfireJobs()` | `services` | İşleri Hangfire ile yürütür. | [Arka plan işleri](../guides/jobs.md#altyapı) |
| `AddHaldusWebhookPublisher(...)` | `services` | Olayları imzalı HTTP istekleriyle teslim eder. | [Olay yayını](../guides/events.md#webhook) |
| `AddHaldusSqsPublisher(...)` | `services` | Olayları Amazon SQS'e taşır. | [Olay yayını](../guides/events.md#amazon-sqs) |
| `AddHaldusRabbitMqPublisher(...)` | `services` | Olayları RabbitMQ'ya taşır. | [Olay yayını](../guides/events.md#rabbitmq) |
| `AddHaldusKafkaPublisher(...)` | `services` | Olayları Kafka'ya taşır. | [Olay yayını](../guides/events.md#kafka) |
