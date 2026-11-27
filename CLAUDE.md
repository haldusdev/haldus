# Haldus

Archetype ve trait'lerle tekrarlayan backend kodunu derleme zamanında üreten .NET 10 framework'ü.
`app.MapCrud<Product>().MultiTenant()` gibi bir çağrıdan entity, DTO, Minimal API endpoint'leri ve
EF Core yapılandırması üretilir.

Bu dosya her oturumda yüklenir. Alanlara özgü ayrıntı `.claude/rules/` altındadır ve yalnızca eşleşen bir dosya
okunduğunda ya da düzenlendiğinde yüklenir (aşağıda, "Kural dosyaları").

## Kurallar

- Commit'ten önce `dotnet test` çalıştır ve commit'i sonucuna bağla; test çıktısına bakmadan zincirleme
  commit atma. Bekleyen `.received.cs` dosyası varken de commit atma.
- Üretilen kodda ve kütüphanelerde kod İngilizce, yorumlar Türkçe.
- Generator testleri üretilen çıktının hatasız derlendiğini de doğrulamalıdır.
- .NET 11 yayınlandı ama LTS değil; paketler 10.x hattında tutulur.
- Derleme uyarıları hatadır; buna NuGet'in güvenlik açığı uyarıları da dahildir. Dolaylı bir bağımlılık
  yüzünden derleme durursa uyarıyı kapatma, paketi yamalı sürüme sabitle.
- **Etiketi (`v*`) itmek bir yayındır ve geri alınamaz; kullanıcı istemeden etiket oluşturulmaz.**
- Olgun bir kütüphanenin yerine protokol istemcisi veya güvenlik kodu elle yazılmaz; paket gerekiyorsa kullanıcıya
  sorulur (kullanıcı kararı, 2026-11-25; `.claude/rules/plans-history.md`, "İstemci paketleri").
- **Herkese açık yeni bir `Map*` metodu, zincir metodu, `AddHaldus*` / `UseHaldus` metodu veya tanı `docs/guide.md`
  kılavuzuna, herkese açık yeni bir bildirim `docs/reference/declarations.md` dizinine yazılmadan eklenemez**
  (`GuideTests`, `DeclarationIndexTests`). Bir bildirimin davranışı değişince ilgili rehber sayfası, `docs/guide.md`
  ve gerekiyorsa eğitim aynı değişiklikte güncellenir.
- Haldus'un istek ve yanıtlarda görünen tiplerinin belge yorumları tüketen uygulamanın `openapi.json` dosyasına
  `description` olarak girer; böyle bir yorumu değiştirmek şemayı değiştirir, istemciler yeniden üretilir.
- `src` altına eklenen yeni bir kütüphane `Description` taşır; paket olmayacaksa `IsPackable=false` yazar.
- Yeni bir tasarım kararı ilgili kural dosyasına yazılır (çoğu zaman `.claude/rules/design-decisions.md`) ve
  yeniden sorulmaz. Kural dosyası yalnızca eşleşen bir dosyaya dokunulunca yüklendiği için, henüz koda bakmadan
  verilecek bir tasarım yanıtından önce ilgili kural dosyası okunur.

## Komutlar

- Derleme: `dotnet build`
- Testler: `dotnet test` (entegrasyon testleri Docker ister; Testcontainers PostgreSQL başlatır)
- Tek test projesi: `dotnet test --project tests/Haldus.Generators.Tests`
- Snapshot'ları onaylamak: `HALDUS_ACCEPT_SNAPSHOTS=1` ortam değişkeniyle generator testlerini çalıştır.
  Fark olduğunda test, `Snapshots/` altına `.received.cs` yazar; önce onu incele.
- Test çalıştırıcısı Microsoft.Testing.Platform'dur (`global.json`); xUnit v3, .NET 10 SDK'da VSTest ile çalışmaz.
- Paketleme: `dotnet pack -c Release -o artifacts/packages` (`src` altındaki on kütüphane; klasör `.gitignore`'dadır).
  Ayrıntısı aşağıda, "Paketleme".
- Belgelerin örnekleri: `dotnet test --project tests/Haldus.Docs.Tests`; yalnızca birkaç sayfa için
  `HALDUS_DOCS_ONLY=docs/guides/jobs.md,...`. Ayrıntısı `.claude/rules/docs.md`.
- Örnek uygulamanın `openapi.json` dosyası build sırasında yeniden üretilir.
- Örnek mağazanın istemcisi (`samples/Haldus.Sample.Shop.Client`): `pnpm install`, `pnpm generate:api`,
  `pnpm typecheck`. **Örnek mağazada bir uç veya DTO değiştiğinde (üretilen kod değiştiğinde de) backend derlendikten
  sonra istemci aynı görevde yeniden üretilir ve tip denetimi çalıştırılır;** `openapi.json` ile `src/api` birlikte
  commit'lenir. Aynı kural örnek arayüzlerin kendi istemcileri için de geçerlidir (aşağıda).
- Örnek yönetim paneli (`samples/Haldus.Sample.Shop.Web`): `pnpm install`, `pnpm dev`, `pnpm generate:api`,
  `pnpm typecheck`, `pnpm lint`, `pnpm build`. Paneli çalıştırmak için backend'in de çalışması gerekir (kendi
  README'sinde; `Shop:CreateDatabase` ayarı tabloları açılışta oluşturur). Panel yalnızca kullandığı etiketlerin
  istemcisini üretir (`orval.config.ts`: `filters.tags`); yeni bir kaynak kullanılacaksa etiketi oraya eklenir.
- Örnek mobil uygulama (`samples/Haldus.Sample.Shop.Mobile`): `pnpm install`, `pnpm dev` (Expo), `pnpm generate:api`,
  `pnpm typecheck`, `pnpm bundle:native` (iOS ve Android üretim paketlerini derler; iOS bu makinede çalıştırılamadığı
  için oradaki tek denetim budur). Backend'in adresi `EXPO_PUBLIC_API_URL` ile verilir. Tarayıcıda çalıştırılırken backend'e kaynak
  izni gerekir (`Shop:CorsOrigins`). pnpm burada düz `node_modules` kurar (`pnpm-workspace.yaml`: `nodeLinker: hoisted`);
  Metro, sembolik bağlı düzende dolaylı bağımlılıkları (`react-native-css-interop`) çözemiyor.
- Örnek arayüzlerin testleri: `pnpm test` (birim) ve `pnpm test:e2e` (etkileşim; kurulu Chrome veya Edge ister),
  panelde ve mobilde. Ayrıntısı `.claude/rules/sample-shop.md`.
- CI: `.github/workflows/ci.yml` (GitHub Actions, Ubuntu). Yukarıdaki komutların hepsini çalıştırır; ayrıca
  `openapi.json` dosyaları ve istemcilerin orval çıktısı commit'lenmiş halinden farklıysa başarısız olur (şemayı
  veya istemciyi yeniden üretmeyi unutmak CI'da yakalanır). Etkileşim testleri runner'daki Chrome ile koşar; iş önce
  tarayıcının var olduğunu denetler, çünkü testler tarayıcı bulamazsa atlanır. Yeni bir örnek arayüz veya test
  komutu eklenirse iş akışına da eklenir.
- Örnek uygulamada üretilen kod `samples/Haldus.Sample.Shop/obj/Generated` altında incelenebilir.
- Yük ölçümü: `dotnet run -c Release --project tools/Haldus.LoadTest` (çalışan bir örnek mağaza ister; backend
  `Shop:DemoIdentity=true` ile ve Release kipinde başlatılır; ayrıntısı aracın README'sinde). Sonuçlar makineye bağlıdır;
  rapordaki tablo değiştirilecekse ölçüm boş bir veritabanıyla yeniden yapılır.

## Yapı

- `src/Haldus.Abstractions`: bağımlılıksız sözleşmeler (sayfalama tipleri, trait arayüzleri).
- `src/Haldus.Generators`: Roslyn incremental generator (netstandard2.0, Roslyn 5.0.0'a sabit).
- `src/Haldus.EntityFrameworkCore`: model kayıt defteri, interceptor'lar, sorgu filtreleri.
- `src/Haldus.AspNetCore`: `AddHaldus`, `Map*` giriş noktaları, validasyon, hata yanıtları, `.resx`.
- `src/Haldus.Storage.S3`: S3 uyumlu nesne deposu (`IHaldusFileStorage`); `AWSSDK.S3` üzerine.
- `src/Haldus.Events.Sqs`, `src/Haldus.Events.RabbitMq`, `src/Haldus.Events.Kafka`: olayları Amazon SQS'e, RabbitMQ'ya
  ve Kafka'ya taşıyan yayıncılar (`IHaldusEventPublisher`); sırasıyla `AWSSDK.SQS`, `RabbitMQ.Client` ve
  `Confluent.Kafka` üzerine. Çekirdek bu paketlere başvurmaz; uygulama istediğini ekler.
- `src/Haldus.Jobs.InMemory`, `src/Haldus.Jobs.PostgreSql`, `src/Haldus.Jobs.Hangfire`: `MapJob` işlerini
  yürütücüye ulaştıran altyapı paketleri. Çekirdek yalnızca `IHaldusJobDispatcher` arayüzünü bilir.
- `tests/Haldus.Generators.Tests`: generator çıktısı testleri.
- `tests/Haldus.IntegrationTests`: örnek uygulamaya karşı gerçek PostgreSQL testleri.
- `tests/Haldus.Docs.Tests`: belgelerdeki C# örneklerini generator'dan geçirerek derler; bildirim dizininin eksiksiz,
  bağlantıların geçerli olduğunu denetler.
- `docs/`: kullanıcı belgeleri (`.claude/rules/docs.md`).
- `samples/Haldus.Sample.Shop`: örnek uygulama ve modelin sınandığı yer.
- `samples/Haldus.Sample.Shop.Client`: örnek mağazanın `openapi.json` dosyasından orval ile üretilen TypeScript
  istemcisi (tipler + TanStack Query hook'ları, `fetch`). Uygulama değildir; şemanın tüketilebildiğini kanıtlar.
- `samples/Haldus.Sample.Shop.Web`: örnek mağazanın yönetim paneli (Vite + React + TypeScript, shadcn/ui `sidebar-07`
  ve `login-01`, Tailwind). Ürünler, kategoriler ve siparişlerin iş akışı; kendi orval istemcisi `src/api` altındadır.
- `samples/Haldus.Sample.Shop.Mobile`: örnek mağazanın mobil uygulaması (Expo, Expo Router, React Native Reusables,
  NativeWind). Ürünler, favoriler ve kullanıcının sepeti; kendi orval istemcisi `api` altındadır.
- `samples/Haldus.Sample.Tracker`: belgelerdeki eğitimin bitmiş uygulaması (proje ve iş takibi); EF Core migration'ları
  taşıyan tek örnektir. Eğitimdeki istek ve yanıtlar bu uygulamadan alındı; uygulama değişirse eğitim de güncellenir.
- `samples/Haldus.Sample.Modular`: modüler yapının sınandığı küçük uygulama. `Catalog` ve `Sales` kendi
  bağlamına sahip modüllerdir; `Support` modülü ve `Host` ortak bağlamı kullanır.
- `tools/Haldus.LoadTest`: çalışan örnek mağazayı HTTP üzerinden yük altında ölçen konsol aracı (bağımlılığı yok).
  Kilide dayanan özellikleri kilitsiz bir yazmayla yan yana ölçer.
- `reports/`, `research_notes/`: archetype/trait taksonomisi araştırması. Tasarım kararlarının dayanağı
  `reports/Haldus archetype ve trait taksonomisi.md` dosyasındadır.

## Kural dosyaları

| Dosya | İçerik | Ne zaman yüklenir |
|---|---|---|
| `design-decisions.md` | Tasarım kararları (model, ilişkiler, aşama aşama kullanıcı kararları, proje tipleri, teknoloji) | `src`, `samples`, `tests`, `docs`, `reports` |
| `plans-history.md` | Üç planın aşamaları ve planlardan sonra yapılanlar; "N. aşama" atıfları buna göredir | aynı |
| `generator-core.md` | Ayrıştırma, bağlama, görünürlük, yaşam döngüsü, geçişler, toplu uçlar, sahiplik, modüller | `src`, generator ve entegrasyon testleri |
| `generator-traits-records.md` | Alan eklentileri, kayıt işlemleri, zincir, sırlar, tekrar serisi, tip/rol, genelleme, hassas alanlar | aynı |
| `generator-traits-content.md` | Değişiklik kümeleri, bakiye, eksenler, çeviriler, saklama, okundu, toplanan ve özel alanlar, sürümler, yayın, onay, belge | aynı |
| `generator-runtime.md` | İşler, olay yayını, aktarma, dosyalar, S3, kuyruk ve webhook yayıncıları, takılabilirler | aynı |
| `sample-shop.md` | Örnek arayüzlerin testleri, frontend ve mobil kararları | örnek mağaza projeleri, entegrasyon testleri, yük aracı |
| `sample-shop-auth.md` | Örnek mağazanın kimlik doğrulaması (parola, JWT, OIDC, oturumlar, personel rolü) | aynı |
| `packaging.md` | Paket ayarlarının nasıl kurulduğu, paketleme tuzakları | proje dosyaları, `Directory.*.props`, iş akışları, `CHANGELOG.md` |
| `docs.md` | Belge yazma kuralları, kılavuz, Context7, Docs7 | `docs`, README, `llms.txt`, `context7.json`, belge testleri |

## Tasarımın özü

- Her entity tam olarak bir archetype (`Map*`), sıfır veya daha çok ilişki bildirimi, en fazla bir durum modeli ve
  serbestçe birleşen trait'lerle tanımlanır. Genel bir `MapArchetype` giriş noktası yoktur.
- Zincir derleme zamanında okunur ve statik olmalıdır. Entity'yi Haldus üretir; kullanıcı kendi alanlarını
  `partial class` ile ekler.
- Üretilen kod somut `DbContext` tipine bağlanmaz (`IHaldusDbContextProvider`); tek proje ve modüler yapı birlikte
  desteklenir.
- Minimal API, EF Core + PostgreSQL; doğrulama Haldus'un kendi endpoint filtresinde, son kullanıcı mesajları `.resx`
  dosyalarındadır (en varsayılan, tr).
- Haldus backend'de kalır: frontend ve mobil için kod üretmez, sözleşme `openapi.json`'dır ve orval ile tüketilir.
- Bütün kararlar ve gerekçeleri: `.claude/rules/design-decisions.md`.

## Paketleme

Kullanıcı kararları (2026-11-25; soruldu ve yanıtlandı), yeniden sorma:

- Paket kimlikleri proje adlarıdır (`Haldus.*`); nuget.org'daki `Haldus` kuruluşu kullanıcınındır.
- İlk sürüm `0.1.0-preview.1`. Sürüm bütün paketlerde aynıdır ve tek yerde durur (`src/Directory.Build.props`:
  `VersionPrefix` + `VersionSuffix`).
- Generator ayrı bir paket değildir: `Haldus.AspNetCore` paketinin `analyzers/dotnet/cs` klasöründe gelir. Üretilen kod
  çalışma zamanı API'lerini çağırdığı için ikisinin sürümü ayrışmamalıdır.

Bilinenler:

- **`0.1.0-preview.1` nuget.org'da yayınlandı** (2026-11-27, kullanıcının isteğiyle; etiket `v0.1.0-preview.1` →
  `64b81ff`, on paketin hepsi). Sonraki sürümler de kullanıcının kararıdır. Yayın iş akışı
  (`.github/workflows/release.yml`): `v*` etiketiyle tetiklenir; etiketin `main`'de ve
  `src/Directory.Build.props`'taki sürümle aynı olduğunu ve `CHANGELOG.md`'de `## <sürüm>` bölümü bulunduğunu denetler,
  testleri çalıştırır, paketler, nuget.org'a iter ve notları o bölümden alan bir GitHub sürümü açar. NuGet'e giriş
  trusted publishing iledir (`NuGet/login@v1`, `nuget` ortamı, `NUGET_USER` sırrı); kalıcı API anahtarı yoktur. Kurulum
  ve sürüm adımları `CONTRIBUTING.md` içinde, "Sürüm yayınlamak" altında. **Etiketi itmek bir yayındır ve geri
  alınamaz; kullanıcı istemeden etiket oluşturulmaz.** İş akışı ilk yayında bütün adımlarıyla çalıştı. Kullanıcı 2026-11-27'de
  nuget.org politikasını (kapsam: yeni paket ve sürüm, desen `Haldus.*`) ve `nuget` ortamının `NUGET_USER` sırrını
  kurdu. Belgeler (README, kurulum, eğitim, SSS, paketler) `0.1.0-preview.1` için paketlerin nuget.org'da olduğunu
  söyler; yerel üretim ayrı bir ön sürüm adıyla (`--version-suffix local.1`) anlatılır.
- Lisans MIT'tir (kullanıcı kararı, 2026-11-27; telif hakkı sahibi kullanıcının kendisidir, "Haldus contributors" değil).
  Metin kökteki `LICENSE` dosyasındadır; paketler `PackageLicenseExpression` ve `Copyright` taşır
  (`src/Directory.Build.props`).

Nasıl kurulduğu ve öteki bilinenler: `.claude/rules/packaging.md`.

## Belgeler

- `docs/` Türkçedir; `docs/guide.md` İngilizcedir (ajanlar için; `Haldus.AspNetCore` paketiyle de gelir).
- Her `csharp` bloğunun önünde bir işaret vardır (`<!-- example: ad -->`, `<!-- fragment -->`...) ve örnekler testle
  derlenir; bağlantılar da denetlenir. Ayrıntı: `.claude/rules/docs.md` ve `CONTRIBUTING.md`, "Belgeler".

## Aşamalar

Üç plan (32 aşama) ve planlardan sonraki işler bitti; açık bir aşama yoktur. Yeni bir iş yeni bir plandır ve tasarım
soruları yeniden sorulur. Geçmiş `.claude/rules/plans-history.md` içinde, sınamanın sonucu
`reports/Haldus taksonomi sınaması.md` dosyasındadır. Yeni bir archetype veya trait taksonomi tablosundaki bir satırı
kapatıyorsa `tests/Haldus.Generators.Tests/TaxonomyTests.cs` ve rapor birlikte güncellenir.
