# Katkıda bulunmak

Haldus'a katkı vermek istediğiniz için teşekkürler. Bu belge geliştirme ortamını kurmayı, testleri çalıştırmayı, bir
değişikliği hazırlamayı ve belgeleri yazmayı anlatır.

## İçindekiler

- [Ortam](#ortam)
- [Depo düzeni](#depo-düzeni)
- [Derlemek ve test etmek](#derlemek-ve-test-etmek)
- [Bir değişikliği hazırlamak](#bir-değişikliği-hazırlamak)
- [Yeni bir trait veya archetype eklemek](#yeni-bir-trait-veya-archetype-eklemek)
- [Belgeler](#belgeler)
- [Commit ve pull request](#commit-ve-pull-request)
- [Sürüm yayınlamak](#sürüm-yayınlamak)

## Ortam

| Araç | Neden |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Derleme ve testler. Paketler 10.x hattında tutulur. |
| Docker | Entegrasyon testleri PostgreSQL'i (ve bazı testler Keycloak, MinIO, RabbitMQ, Kafka...) Testcontainers ile kapsayıcıda başlatır. |
| Node.js ve [pnpm](https://pnpm.io) | Yalnızca örnek arayüzler (`samples/Haldus.Sample.Shop.Web`, `.Mobile`, `.Client`) için. |

```bash
git clone https://github.com/haldusdev/haldus.git
cd haldus
dotnet build
```

## Depo düzeni

| Klasör | İçeriği |
|---|---|
| `src/Haldus.Abstractions` | Bağımlılıksız sözleşmeler: sayfalama tipleri, trait arayüzleri, iş mantığı arayüzleri. |
| `src/Haldus.Generators` | Roslyn incremental source generator (netstandard2.0). `Map*` çağrılarını okur, kodu üretir. |
| `src/Haldus.EntityFrameworkCore` | Model kayıt defteri, interceptor'lar, sorgu filtreleri. |
| `src/Haldus.AspNetCore` | `AddHaldus`, `Map*` giriş noktaları, doğrulama, hata yanıtları, mesajlar (`.resx`). Generator bu paketin içinde dağıtılır. |
| `src/Haldus.Jobs.*` | `MapJob` işlerinin altyapıları: bellek içi, PostgreSQL, Hangfire. |
| `src/Haldus.Storage.S3` | S3 uyumlu dosya deposu. |
| `src/Haldus.Events.*` | Olay yayıncıları: SQS, RabbitMQ, Kafka. |
| `tests/Haldus.Generators.Tests` | Generator çıktısının testleri; snapshot'lar ve derleme denetimleri. |
| `tests/Haldus.IntegrationTests` | Örnek uygulamalara karşı gerçek PostgreSQL testleri. |
| `tests/Haldus.Docs.Tests` | Belgelerdeki örneklerin derlendiğini ve bildirim dizininin eksiksiz olduğunu denetler. |
| `samples/` | Örnek uygulamalar; bkz. [docs/samples.md](docs/samples.md). |
| `docs/` | Kullanıcı belgeleri. |
| `reports/`, `research_notes/` | Tasarımın dayandığı taksonomi araştırması. |

Tasarım kararlarının ve generator'ın iç işleyişinin ayrıntılı kaydı [CLAUDE.md](CLAUDE.md) dosyasındadır; bir
özelliğe dokunmadan önce ilgili bölümünü okuyun.

## Derlemek ve test etmek

```bash
dotnet build
```

```bash
dotnet test
```

Derleme uyarıları hatadır (`TreatWarningsAsErrors`); NuGet güvenlik açığı uyarıları da buna dahildir.

Test çalıştırıcısı Microsoft.Testing.Platform'dur (`global.json`). Tek bir proje:

```bash
dotnet test --project tests/Haldus.Generators.Tests
```

### Snapshot'lar

Generator testlerinin bir kısmı üretilen kodu `tests/Haldus.Generators.Tests/Snapshots` altındaki dosyalarla
karşılaştırır. Üretilen kod değiştiğinde test, yanına bir `.received.cs` dosyası yazar ve başarısız olur. Farkı
inceleyin; doğruysa onaylayın:

```bash
HALDUS_ACCEPT_SNAPSHOTS=1 dotnet test --project tests/Haldus.Generators.Tests
```

Bekleyen `.received.cs` dosyası varken commit atmayın.

### Örnek arayüzler

Örnek mağazada bir uç veya DTO değiştiğinde (generator'ın ürettiği kod değiştiğinde de) şema yeniden üretilir ve
istemciler güncellenir:

```bash
dotnet build samples/Haldus.Sample.Shop
cd samples/Haldus.Sample.Shop.Client && pnpm generate:api && pnpm typecheck
```

Panel ve mobil uygulamanın kendi komutları kendi README dosyalarındadır.

### Sürekli tümleştirme

Her push ve pull request'te GitHub Actions şunları çalıştırır (`.github/workflows/ci.yml`, Ubuntu):

- `dotnet build`, `dotnet test` (entegrasyon testleri dahil; kapsayıcıları runner'ın Docker'ı çalıştırır) ve
  `dotnet pack`.
- Örneklerin `openapi.json` dosyalarının ve istemcilerin orval çıktısının güncel olduğu: derleme ve
  `pnpm generate:api` çalışma kopyasında bir fark bırakırsa iş başarısız olur. Şemayı veya istemciyi yeniden üretip
  commit'leyin.
- Örnek istemcinin tip denetimi; panelin tip denetimi, lint'i, testleri, derlemesi ve etkileşim testleri; mobil
  uygulamanın tip denetimleri, testleri, etkileşim testleri ve iOS / Android üretim paketleri. Etkileşim testleri
  runner'daki Chrome ile çalışır (`HALDUS_TEST_BROWSER`). Testler tarayıcı bulamazsa atlandığı için iş
  önce tarayıcının var olduğunu denetler.

## Bir değişikliği hazırlamak

1. Davranışı değiştiren her iş testle gelir: generator'ın çıktısı için `Haldus.Generators.Tests`, çalışma zamanı
   davranışı için `Haldus.IntegrationTests`.
2. Üretilen kod, tüketen projede örtük `using`'ler kapalıyken de derlenmelidir; generator testleri bunu doğrular.
3. Archetype'ın anlamıyla çelişen bir bildirim sessizce yok sayılmaz, `HALDUS007` tanısı olur.
4. Herkese açık API değiştiyse [belgeleri](#belgeler) aynı değişiklikte güncelleyin. `docs/reference/declarations.md`
   ve `docs/guide.md` her herkese açık bildirimi saymak zorundadır; testler eksik olanı yakalar.
5. Commit'ten önce `dotnet test` çalıştırın.

## Yeni bir trait veya archetype eklemek

Kısa yol haritası (ayrıntısı [CLAUDE.md](CLAUDE.md) içinde, "Generator'ın işleyişi"):

- **Trait:** `Model/TraitCatalog` (arayüz ve alanlar), `Parsing/ChainReader` (zincirdeki adı),
  `ResourceBuilderTraitExtensions` (çalışma zamanında boş zincir metodu), gerekiyorsa `Emit/EndpointEmitter` ve
  `Emit/ModuleEmitter.AppendModel`.
- **Archetype:** `Model/ResourceModel.cs` içindeki `Archetype` enum'u ve uzantıları, `MapCallParser.ArchetypeOf` ve
  `Conflicts`, `EndpointEmitter`, `ModuleEmitter.AppendModel` ve çalışma zamanındaki `Map*` metodu.
- **Tanı:** `Diagnostics/HaldusDiagnostics.cs` ve `AnalyzerReleases.Unshipped.md` birlikte güncellenir; tanı
  [docs/reference/diagnostics.md](docs/reference/diagnostics.md) ve `docs/guide.md` içinde açıklanır.

## Belgeler

Kullanıcı belgeleri `docs/` altındadır ve Türkçedir. Tek istisna [docs/guide.md](docs/guide.md): kodlama ajanları için
yazılmış, İngilizce, tek dosyalık başvurudur ve paketle birlikte dağıtılır.

### Düzen

| Klasör | Ne yazılır |
|---|---|
| `docs/getting-started` | Hiç bilmeyen birini adım adım çalışan bir uygulamaya götüren eğitim. |
| `docs/concepts` | Haldus'un nasıl çalıştığı ve bütün kaynaklarda geçerli kurallar. |
| `docs/guides` | Bir özellik alanının nasıl kullanıldığı: bildirim, üretilen uçlar, örnek istek ve yanıtlar, kurallar, sınırlar. |
| `docs/reference` | Bakıp bulunacak listeler: bütün bildirimler, yapılandırma, HTTP sözleşmesi, tanılar, paketler. |

### Rehber sayfasının iskeleti

Her rehber sayfası aynı sırayı izler; okuyan her sayfada aynı yerde aynı bilgiyi bulur:

1. Başlık ve bir paragraflık giriş: bu özellik hangi sorunu çözer, ne zaman kullanılır.
2. **İçindekiler** (sayfa uzunsa).
3. Her bildirim için bir bölüm:
   - kısa açıklama,
   - derlenen bir C# örneği (bildirim ve entity),
   - üretilen uçların tablosu (yöntem, adres, ne yaptığı),
   - örnek istek ve yanıt (`http` ve `json` blokları),
   - kurallar ve hata yanıtları (hangi durumda hangi durum kodu),
   - seçenekler (parametreler ve varsayılanları).
4. **Birlikte kullanım**: hangi bildirimlerle birleşir, hangileriyle birleşmez (`HALDUS007`).
5. **Sınırlar**: bilerek yazılmamış olanlar.
6. **Ayrıca bakın**: ilgili sayfalara bağlantılar.

### Örnekler

Belgelerdeki her `csharp` bloğu `tests/Haldus.Docs.Tests` tarafından Haldus generator'ından geçirilerek derlenir.
Bloğun hemen önüne, boş satır bırakmadan, bir HTML yorumu yazılır (GitHub'da görünmez):

```text
<!-- example: ad -->                    derlenir; aynı dosyada aynı adı taşıyan bloklar birlikte derlenir
<!-- example: ad1 ad2 -->               blok iki örneğe de girer (ör. eğitimin adımlarında ortak bir entity)
<!-- example: ad, error: HALDUS004 -->  derlenir ve o tanıyı vermesi beklenir
<!-- fragment -->                       derlenmez: tek başına anlamı olmayan bir parça (bir satır, bir imza)
```

Derlenen örneklerde şu `using`'ler yazılmış sayılır; bu, okura da söylenir:
web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve `System.ComponentModel.DataAnnotations`.

Üst düzey komut içeren bir örnek `Program.cs` sayılır. `var builder = WebApplication.CreateBuilder(args);` ve
`var app = builder.Build();` satırları örnekte yoksa yazılmış sayılır; böylece kısa örnekler yalnızca konuyla ilgili
satırları gösterir. `AppDbContext` örnekte tanımlanmadıysa `partial` bir bağlam olarak eklenir.

İşaretsiz bir `csharp` bloğu testin hatasıdır. Bir parçayı `fragment` yapmadan önce onu derlenen bir örneğe çevirmeyi
deneyin; okuyanın kopyaladığı kod çalışmalıdır.

Yalnızca birkaç sayfanın örneklerini denetlemek için:

```bash
HALDUS_DOCS_ONLY=docs/guides/jobs.md,docs/guides/files.md dotnet test --project tests/Haldus.Docs.Tests
```

`http` ve `json` blokları derlenmez. İstek ve yanıt örneklerini üretilen koddan
(`samples/Haldus.Sample.Shop/obj/Generated`) ve entegrasyon testlerinden çıkarın; alan adları camelCase, durum kodları
gerçek olmalıdır.

### Üslup

- Okura "siz" diye seslenin; kısa, düz cümleler kurun.
- Kod adları (tip, metot, alan, adres) her zaman ters tırnak içindedir ve İngilizcedir; çevrilmez.
- Bir davranışı yazmadan önce kodda veya testte doğrulayın. Emin olmadığınız bir şeyi yazmayın.
- Sınırları gizlemeyin: bir özellik bir şeyi yapmıyorsa "Sınırlar" bölümünde söyleyin.

## Commit ve pull request

- Commit mesajları [Conventional Commits](https://www.conventionalcommits.org) biçimindedir; tip öneki (`feat:`,
  `fix:`, `docs:`, `test:`, `build:`, `chore:`) İngilizce, açıklama Türkçedir: `feat: bakiyeye tutma eklendi`.
- Bir commit tek bir işi taşır ve testleriyle birlikte gelir.
- Pull request açıklaması neyin neden değiştiğini, nasıl sınandığını ve bilinen sınırları söyler.
- Herkese açık API'yi kıran değişiklikler [CHANGELOG.md](CHANGELOG.md) içinde "Kıran değişiklikler" altında yazılır.

Gönderdiğiniz katkılar projenin lisansı olan [MIT](LICENSE) ile lisanslanır.

Güvenlik açığı bildirmek için bkz. [SECURITY.md](SECURITY.md).

## Sürüm yayınlamak

Paketleri `.github/workflows/release.yml` yayınlar: `v` ile başlayan bir etiket itildiğinde etiketin `main` dalında ve
paket sürümüyle aynı olduğunu denetler, testleri çalıştırır, on paketi üretir, nuget.org'a iter ve paketleri ekleyen
bir GitHub sürümü açar. Sürüm notları `CHANGELOG.md` içindeki aynı adlı bölümdür; adında `-` olan sürüm (`preview`)
GitHub'da ön sürüm olarak işaretlenir.

NuGet'e giriş [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) iledir; depoda
kalıcı bir API anahtarı yoktur. Bir kez yapılacak kurulum:

1. nuget.org'da kullanıcı adınız → **Trusted Publishing** → yeni politika. Sahip: paketlerin sahibi olan `Haldus`
   kuruluşu. Repository Owner `haldusdev`, Repository `haldus`, Workflow File `release.yml`, Environment `nuget`.
2. GitHub'da depo ayarları → Secrets and variables → Actions: `NUGET_USER` sırrına nuget.org kullanıcı adınız
   (e-posta değil).
3. İsteğe bağlı: Settings → Environments → `nuget` ortamına onaylayıcı ekleyin; yayın, onaylanana kadar bekler.

Her sürümde:

1. `src/Directory.Build.props` içindeki `VersionPrefix` / `VersionSuffix`'i ayarlayın.
2. `CHANGELOG.md` içinde "Yayınlanmadı" bölümünün maddelerini `## <sürüm>` başlığının altına taşıyın.
3. Commit'leyip `main`'e itin ve CI'ın geçmesini bekleyin.
4. Etiketi oluşturup itin:

```bash
git tag v0.1.0-preview.1
```

```bash
git push origin v0.1.0-preview.1
```

NuGet aynı sürümü ikinci kez kabul etmez ve yayınlanmış bir sürüm silinemez (yalnızca listeden kaldırılır). Hatalı bir
sürüm yeni bir sürümle düzeltilir. Yarıda kalan bir yayın yeniden çalıştırılabilir; zaten yüklenmiş paketler atlanır.
