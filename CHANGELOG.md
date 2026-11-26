# Değişiklik günlüğü

Bu projedeki önemli değişiklikler bu dosyada yazılır. Biçim [Keep a Changelog](https://keepachangelog.com/tr-TR/1.1.0/)
düzenini izler ve sürümler [anlamsal sürümlemeye](https://semver.org/lang/tr/) uyar. 1.0 öncesinde küçük sürümler de
kıran değişiklik içerebilir; her biri "Kıran değişiklikler" altında yazılır.

## Yayınlanmadı

### Eklenenler

- Lisans: MIT (`LICENSE`); paketler lisansı `PackageLicenseExpression` ile taşır.
- Kullanıcı belgeleri (`docs/`): kurulum, adım adım eğitim, kavramlar, her özellik alanı için rehberler, başvuru
  sayfaları (bildirim dizini, yapılandırma, HTTP sözleşmesi, tanılar, paketler) ve sık sorulan sorular. Belgelerdeki
  C# örnekleri testle derlenir (`tests/Haldus.Docs.Tests`).
- Eğitimin örnek uygulaması: `samples/Haldus.Sample.Tracker` (EF Core migration'larıyla). Entegrasyon testi
  (`TrackerTests`) migration'ları boş bir veritabanına uygular ve eğitimdeki akışı baştan sona yürütür.
- `CONTRIBUTING.md`, `SECURITY.md`, issue ve pull request şablonları.
- GitHub Actions ile sürekli tümleştirme: derleme, bütün testler, paketleme, üretilen şema ve istemcilerin güncelliği,
  örnek arayüzlerin denetimleri.
- Context7 yapılandırması (`context7.json`): araştırma notları, raporlar, testler ve katkıcı belgeleri indeksin
  dışında; kodlama ajanlarına kılavuzun kuralları verilir.

### Düzeltilenler

- Silinen dosya kaydının içeriği, kayıt nasıl silinirse silinsin depodan kaldırılır: takıldığı veya altında durduğu
  kayıtla birlikte zincirleme silinen ve uygulamanın kendi koduyla silinen dosyaların içeriği artık depoda kalmaz.
  Dosya sistemi ve S3 depolarında içerik silme commit edilince kaldırılır; geri alınan silme (atomik toplu silme dahil)
  içeriğe dokunmaz.
- `EffectiveDated` kaynağında `effectiveFrom`, tekrar serisinde `startsAt` zorunludur; gönderilmeyen alan 0001-01-01
  sayılmak yerine alan düzeyinde 400 alır. API şemasında iki alan da artık zorunlu görünür.
- Serbest `MapAction` ve `MapQuery` uçlarında mantığın fırlattığı `TransitionRejectedException` 422 olur (önceden
  500'dü); uçların şemasına 422 yanıtı eklendi.
- Dışa aktarılan CSV'nin satırları her işletim sisteminde CRLF ile ayrılır (RFC 4180); Linux'ta LF yazılıyordu. CI
  kurulurken, testler Linux'ta denenirken çıktı.
- Onayda "kendi yazdığını onaylayamaz" mesajı (`Approval.OwnRequest`) kuralı doğru anlatır: içeriği yazmış herkesi
  kapsar, yalnızca son yazanı değil.
- Yanlış yere düşmüş belge yorumları (`HaldusProblems.KeyRequired` / `Duplicate`, `AddHaldusModule`) ve `RoleOf`'un
  silme davranışını eksik anlatan yorumu.

## 0.1.0-preview.1

İlk ön sürüm. Paketler henüz bir NuGet kaynağında yayınlanmadı; depodan üretilir (`dotnet pack`).

### Eklenenler

- **Kaynak üreticisi.** `Map*` çağrılarından entity'nin öteki yarısı, istek ve yanıt tipleri, Minimal API uçları,
  doğrulama ve EF Core yapılandırması; yanlış bildirimler için derleme tanıları (`HALDUS001`–`HALDUS010`).
- **Archetype'lar:** `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapStore`, `MapLookup`, `MapSingleton`, `MapDetail`,
  `MapAssociation`, `MapAction`, `MapQuery`, `MapJob`, `MapFile`, `MapBalance`.
- **İlişkiler:** `References`, `Under` (kısa adres dahil), `Hierarchical`, `Embeds`, yük ve durum taşıyan bağlar,
  kullanıcıya bağlı bağlar, `TypedBy`, `RoleOf`, `SubtypeOf`, `AttachedTo`.
- **Durum:** `Workflow`, `EditableIn`, `DeletableIn`, geçişler, ek durum eksenleri (`Axis`), `Document`.
- **Trait'ler:** kiracı ve kullanıcı kapsamı, denetim alanları, geri alınabilir silme, arşiv, eşzamanlılık denetimi,
  süre dolumu, saklama süresi, tekrara karşı koruma, toplu işlemler, metadata, doğal anahtar, anahtar biçimleri,
  numaralar, sıralama, sıralı kayıtlar, kopya, önizleme, ölçüte göre silme, değişiklik kümeleri, ters kayıt, yerine
  geçen kayıt, üretme ve türetme, onay ve onay zinciri, sürümler, taslak ve yayın, zamanlanmış yayın, değişiklik akışı,
  okundu bilgisi, toplanan alanlar, özel alanlar, çeviriler, hassas ve şifreli alanlar, sırlar ve token ile erişim,
  tekrar serisi, olay yayını, içe ve dışa aktarma.
- **Paketler:** `Haldus.AspNetCore` (generator dahil), `Haldus.EntityFrameworkCore`, `Haldus.Abstractions`; iş
  altyapıları (`Haldus.Jobs.InMemory`, `Haldus.Jobs.PostgreSql`, `Haldus.Jobs.Hangfire`); S3 uyumlu dosya deposu
  (`Haldus.Storage.S3`); olay yayıncıları (`Haldus.Events.Sqs`, `Haldus.Events.RabbitMq`, `Haldus.Events.Kafka`) ve
  çekirdekte webhook yayıncısı.
- Kodlama ajanları için İngilizce başvuru (`docs/guide.md`, paketle birlikte) ve `llms.txt`.
