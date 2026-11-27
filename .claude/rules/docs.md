---
paths:
  - "docs/**"
  - "README.md"
  - "CONTRIBUTING.md"
  - "CHANGELOG.md"
  - "llms.txt"
  - "context7.json"
  - "tests/Haldus.Docs.Tests/**"
---

# Belgeler ve kılavuz

Kökteki CLAUDE.md'deki "Belgeler" ve "Kılavuz" kurallarının ayrıntısı.

## Belgeler

Kullanıcının isteği (2026-11-25): projenin tamamı, açık kaynak bir projedeki gibi, örnekleriyle belgelensin.

- `docs/` Türkçedir (projenin belge dili); `docs/guide.md` İngilizce kalır (ajanlar için; "Kılavuz" bölümü).
  Düzen: `getting-started` (kurulum, eğitim), `concepts`, `guides` (özellik alanı başına bir sayfa), `reference`
  (bildirim dizini, yapılandırma, HTTP sözleşmesi, tanılar, paketler), `faq.md`, `samples.md`; giriş `docs/README.md`.
  Kökte `README.md` (kısa tanıtım), `CONTRIBUTING.md` (geliştirme ve belge yazma kuralları), `CHANGELOG.md`,
  `SECURITY.md`; `.github` altında issue ve pull request şablonları.
- **Örnekler derlenir.** Her `csharp` bloğunun önünde `<!-- example: ad -->` (birden çok ad boşlukla), 
  `<!-- example: ad, error: HALDUS004 -->` veya `<!-- fragment -->` vardır; işaretsiz blok testin hatasıdır. Kurallar
  ve örtük `using`'ler `CONTRIBUTING.md` içinde "Belgeler" altında. Örnek, generator hiçbir şey üretmezse başarısız
  sayılır.
- **Herkese açık yeni bir bildirim `docs/reference/declarations.md` dizinine yazılmadan eklenemez** (`DeclarationIndexTests`;
  altyapı paketlerindeki kayıt metotları da denetlenir). Bağlantılar ve başlık bağlantıları (`#...`) da testle
  denetlenir; GitHub'ın bağlantı adı kuralı testte yeniden yazıldı. **Bağlantı verilen bir başlık büyük "İ" ile
  başlamamalıdır:** GitHub onu `i̇` (iki karakter) yapar ve bağlantı tutmaz.
- Bir bildirimin davranışı değişince ilgili rehber sayfası, `docs/guide.md` ve gerekiyorsa eğitim aynı değişiklikte
  güncellenir. Testler adların ve örneklerin derlendiğini denetler, anlatımın doğruluğunu denetlemez.
- `http` / `json` blokları derlenmez; gerçek çıktılardan (eğitim) veya üretilen koddan ve entegrasyon testlerinden
  yazılır.
- Lisans MIT'tir (`LICENSE`); belgeler (README, `docs/faq.md`, `docs/reference/packages.md`) ona bağlanır.

## Kılavuz (AI bilinebilirliği)

Haldus hiçbir modelin eğitim verisinde yoktur; onu kullanan bir projede çalışan ajan ne okursa onu bilir. Bunun için
(kullanıcının isteği, 2026-11-25):

- `docs/guide.md`: bütün `Map*` ve zincir metotlarının, iş mantığı sözleşmelerinin, tanıların ve sık yapılan hataların
  tek dosyalık başvurusu. **İngilizcedir (kullanıcı kararı; soruldu ve yanıtlandı)**; projenin "belgeler Türkçe" kuralının
  bilerek yapılmış istisnasıdır. README Türkçe kalır.
- `llms.txt` (kökte; llmstxt.org düzeni): kılavuza işaret eden kısa dizin.
- Kılavuz `Haldus.AspNetCore` paketinin içinde de gelir (`docs/guide.md`); sonunda, tüketen projenin `AGENTS.md` /
  `CLAUDE.md` dosyasına yapıştırılacak bir parça vardır.
- Her analyzer tanısı kılavuzdaki kendi başlığına bağlanır (`helpLinkUri`; `HaldusDiagnostics.Guide`). Mesajların
  kendisi denetlendi: İngilizce, neyin yanlış olduğunu ve çoğunda nasıl yazılacağını örnekle söylüyor; değiştirilmedi.
- **Herkese açık yeni bir `Map*` metodu, zincir metodu, `AddHaldus*` / `UseHaldus` metodu veya tanı kılavuza yazılmadan
  eklenemez:** `GuideTests` adlarını yansımayla bulur ve kılavuzda arar. Test anlatımın doğruluğunu sınamaz; bir
  bildirimin davranışı değişince kılavuzdaki satırı da elle güncellenir. Öteki paketlerdeki kayıt metotları
  (`AddHaldusS3Storage`...) test projesinin başvurmadığı derlemelerdedir ve denetlenmez.
- Kılavuzdaki imzalar README'den değil koddan (builder'ların imzaları, sözleşme arayüzleri, enum üyeleri) yazıldı.
  Davranış anlatımları README ile bu dosyaya dayanır; her satırı ayrıca çalıştırılarak doğrulanmadı.

- **Context7** (kullanıcı 2026-11-26'da kütüphaneyi ekledi: `/haldusdev/haldus`). Neyin indekslendiğini kökteki
  `context7.json` söyler: `research_notes/`, `reports/`, `tests/`, `tools/` ve depoda çalışanlara yönelik dosyalar
  (`CLAUDE.md`, `CONTRIBUTING.md`, `SECURITY.md`) dışarıdadır, çünkü ilk indekslemede araştırma raporu sorgu
  sonuçlarına giriyordu ve yazılmamış önerileri (`MapChangeSet<T>()` gibi) gerçek API sanırdı. `rules` (her kural en çok 255 karakter; Context7 uzununu reddeder) kılavuzun "Rules"
  bölümünden alındı; kılavuzun kuralları değişirse orası da güncellenir. Türkçe sayfalar da indekslenir. Ayar
  geçerlidir: 2026-11-27'de bir sorgu kuralları ve `0.1.0-preview.1` eğitimini döndürdü. Sahiplik alanları (`url` + `public_key`) kullanıcının
  Context7 hesabında üretildi ve aynı dosyadadır; `public_key` gizli değildir, Context7 onu depoda bulunmak üzere verir. İndekslemede görülen "Source API indexing failed; continuing with
  documentation parsing" uyarısı belgeleri etkilemez.
- **Docs7** (Context7'nin belge sitesi; `docs.json` ister). 2026-11-27'de yerelde denendi (`@upstash/docs7` CLI,
  `docs/`'un bir kopyasında) ve kullanıcı şimdilik vazgeçti. Bulgular: 38 sayfanın hepsi açılıyor (`.md` ve
  `<!-- example -->` yorumları sorun değil); ama `.md` bağlantıları sitede ham Markdown'a gider (Docs7 o adreste
  ajanlar için Markdown sunar; belgelerde 535 böyle bağlantı var), sayfa adları dosya adından gelir (`title` eklenince
  düzelir ama `# Başlık` ile iki kez görünür) ve `docs/` dışına giden bağlantılar 404'tür. Uzantısız bağlantı sitede
  çalışır ama GitHub'da ve belge testlerinde kırılır. Yeniden ele alınırsa yol, `docs/`'u olduğu gibi bırakıp CI'da
  dönüştürülmüş bir kopya üretmek ve `docs7 deploy` ile yayınlamaktır (CI'da oturum açmanın yolu doğrulanmadı).

Yapılmayanlar: belge sitesi, `dotnet new` şablonu, İngilizce README. Bunlar kullanıcının
kararıdır.
