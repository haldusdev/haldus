# Değişiklik günlüğü

Bu projedeki önemli değişiklikler bu dosyada yazılır. Biçim [Keep a Changelog](https://keepachangelog.com/tr-TR/1.1.0/)
düzenini izler ve sürümler [anlamsal sürümlemeye](https://semver.org/lang/tr/) uyar. 1.0 öncesinde küçük sürümler de
kıran değişiklik içerebilir; her biri "Kıran değişiklikler" altında yazılır.

## Yayınlanmadı

## 0.1.0-preview.1

İlk ön sürüm; paketler nuget.org'da yayınlanır. API 1.0'a kadar değişebilir.

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
- Kullanıcı belgeleri (`docs/`): kurulum, adım adım eğitim, kavramlar, her özellik alanı için rehberler, başvuru
  sayfaları (bildirim dizini, yapılandırma, HTTP sözleşmesi, tanılar, paketler) ve sık sorulan sorular. Belgelerdeki
  C# örnekleri testle derlenir.
- Örnek uygulamalar: mağaza (`samples/Haldus.Sample.Shop`; yönetim paneli, mobil uygulama ve orval istemcisiyle),
  eğitimin bitmiş hali (`samples/Haldus.Sample.Tracker`, EF Core migration'larıyla) ve modüler yapı
  (`samples/Haldus.Sample.Modular`).
- Lisans: MIT. Paketler lisansı `PackageLicenseExpression` ile taşır.
