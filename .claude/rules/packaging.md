---
paths:
  - "src/**/*.csproj"
  - "src/Directory.Build.props"
  - "Directory.Build.props"
  - "Directory.Packages.props"
  - ".github/workflows/**"
  - "CHANGELOG.md"
  - "CONTRIBUTING.md"
---

# Paketleme: nasıl kurulu

Kökteki CLAUDE.md'deki "Paketleme" bölümünün ayrıntısı.

Nasıl kurulu:

- Paket ayarları `src/Directory.Build.props` içindedir; kökteki dosya her şeyi paketlenmez yapar (`IsPackable=false`),
  `src` altı geri açar. Her kütüphanenin kendi proje dosyasında yalnızca `Description` vardır. **`src` altına eklenen
  yeni bir kütüphane `Description` taşımalıdır;** paket olmayacaksa (`Haldus.Generators` gibi) `IsPackable=false` yazar.
- `Haldus.AspNetCore`, generator projesine yalnızca derleme sırası için başvurur (`ReferenceOutputAssembly="false"`,
  `PrivateAssets="all"`: generator'ı kendisi çalıştırmaz, pakete bağımlılık olarak yazılmaz) ve derlenen dosyayı pakete
  koyar. Örnekler ve testler generator'a eskisi gibi proje başvurusuyla, analyzer olarak başvurur.
- **`Haldus.AspNetCore`'a başvuran her paket bunu `PrivateAssets="none"` ile yapar** (`Haldus.Jobs.*`,
  `Haldus.Storage.S3`). Varsayılan başvuru pakete `exclude="Build,Analyzers"` yazar; o zaman yalnızca
  `Haldus.Jobs.PostgreSql` ekleyen bir uygulama `MapCrud` çağrısını derler ama generator çalışmaz ve hata ancak çalışma
  anında görülür.
- Belge yorumları pakete girer (`GenerateDocumentationFile`). Eksik yorum hata değildir (`CS1591`, `CS1573` kapalı);
  yanlış yorum (bozuk XML, var olmayan parametre veya tip parametresi, çözülemeyen `cref`) derlemeyi durdurur.
  **Belge yorumları API şemasına da girer:** ASP.NET Core'un OpenAPI üreticisi başvurulan derlemelerin belge
  dosyalarını okur, bu yüzden Haldus'un istek ve yanıtlarda görünen tiplerinin (`ApprovalDecision`, `BalanceEntry`...)
  ve alanlarının yorumları tüketen uygulamanın `openapi.json` dosyasında `description` olur (örnek mağazada ve
  istemcilerinde öyle oldu). Böyle bir tipin yorumunu değiştirmek şemayı değiştirir; istemciler yeniden üretilir.
  Derleyici çok satırlı yorumu işletim sisteminin satır sonuyla yazar; örnek mağaza açıklamaları bu yüzden LF'ye
  çevirir (`Program.cs`, `AddOpenApi` içindeki şema dönüştürücüsü), yoksa Windows'ta üretilen şema CI'daki (Linux)
  şemadan farklı çıkar. CI'ın ilk çalıştırmasında çıktı.
- Semboller derlemenin içindedir (`DebugType=embedded`); ayrı sembol paketi üretilmez.
- Paketin README'si kökteki `README.md`'dir (bütün paketlerde aynı).

Denenen (elle, depo dışında bir klasörde; otomatik testi yoktur): yalnızca `Haldus.AspNetCore` paketine başvuran bir
uygulama derlendi, generator çalıştı ve uygulama geçici bir PostgreSQL kapsayıcısına karşı çalıştırıldı (oluşturma,
alan düzeyinde 400, Türkçe mesaj: uydu kaynak derlemesi de paketten geliyor). Sonra uygulama yalnızca
`Haldus.Jobs.InMemory` paketine ve paketi kullanan bir sınıf kütüphanesine (modül) başvuracak biçimde değiştirildi:
generator ikisinde de çalıştı ve modülün ucu yanıt verdi.

## Bilinenler (devamı)

- NuGet paketi sürümüyle önbelleğe alır: aynı sürümle yeniden üretilen paket tüketene ulaşmaz. Denerken ya sürüm
  ilerletilir (`--version-suffix preview.2`) ya da tüketen tarafta ayrı bir paket klasörü kullanılır
  (`nuget.config`: `globalPackagesFolder`).
- Pakete yazılan commit kimliği paketlemenin yapıldığı andaki `HEAD`'dir; commit'lenmemiş değişiklikle üretilen paket
  yanlış commit'i gösterir.
- Çözüm paketlenirken iki web projesi için "paketleme devre dışı" uyarısı yazılır; zararsızdır.
