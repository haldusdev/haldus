---
paths:
  - "samples/Haldus.Sample.Shop*/**"
  - "tests/Haldus.IntegrationTests/**"
  - "tools/Haldus.LoadTest/**"
---

# Örnek mağaza: arayüzler ve kararlar

Örnek arayüzlerin (panel, mobil, istemci) testleri ve kararları; komutların kendisi kökteki CLAUDE.md'de.

## Arayüz testlerinin ayrıntısı

- Örnek arayüzlerin testleri: `pnpm test` (panelde ve mobilde). Node'un yerleşik test çalıştırıcısıyla çalışır, paket
  taşımaz. `@/` takma adını ve çalışamayan modülleri (`@/i18n`, `react-native`, `expo-secure-store`)
  `tests/register.mjs` çözer; panelde `.tsx` dosyalarını kurulu `typescript` paketiyle çevirir. İki projede de oturum
  ve istek mantığı (`lib`) sınanır. Panelde ayrıca bileşenler `react-dom/server` ile metne çizilip denetlenir
  (`tests/ui`): yalnızca ilk çizim; etkileşim, efektler ve portal içeriği (açılır pencere, menü) görülmez.
- Örnek arayüzlerin etkileşim testleri: `pnpm test:e2e` (panelde ve mobilde; önce derler / web'e dışa aktarır). Paket
  taşımaz: kurulu bir Chromium tarayıcısı (Chrome veya Edge; `HALDUS_TEST_BROWSER` ile seçilir) başsız açılır ve
  DevTools protokolüyle, Node'un yerleşik WebSocket istemcisi üzerinden sürülür (`tests/e2e/support/chrome`; iki
  projedeki dosya aynıdır, birini değiştirirken ötekini de değiştir). Tıklama ve yazma gerçek girdi olaylarıdır.
  Backend çalışmaz: derlenmiş arayüzü ve API'nin bellekteki bir taklidini aynı adresten sunan bir sunucu konuşur
  (`tests/e2e/support/shop`; yanıtları orval'ın tipleriyle yazılır, şema değişince tip denetiminden geçmez). Testler
  bir dokunuştan sonra giden isteği (yol, gövde, başlıklar) ve gelen yanıtın ekrana yansımasını denetler. Tarayıcı
  veya derleme yoksa testler atlanır. Mobilde sürülen, uygulamanın web dışa aktarımıdır (`dist-e2e`): ekranlar,
  gezinme ve istekler cihazdakiyle aynı koddur; cihaza özgü olanlar (güvenli depo, klavye, güvenli alanlar) sınanmaz.
  **Görünür sayılan öğe `aria-hidden` bir bölgenin içinde olamaz** (açık pencerenin arkası, etkin olmayan sekme).

## Kapsam ve kararlar

- **Frontend ve mobil desteğinin kapsamı kapandı (31. aşama; kullanıcı sormamamı istedi, ayrıca onaylanmadı):** Haldus
  backend'de kalır; frontend ve mobil için kendi kod üretimi yoktur. O tarafa verilen şey sözleşmedir (`openapi.json`)
  ve onu tüketmenin yolu kullanıcının genel kurallarındaki yoldur: orval, TanStack Query hook'ları, `fetch`. Depoda
  bunun çalışan örneği vardır (`samples/Haldus.Sample.Shop.Client`) ve şemanın orval'dan hatasız geçtiği `pnpm typecheck`
  ile doğrulanır.
  - Örnek arayüzler sonradan yazıldı (kullanıcı hedefi 2026-11-24'te yeniden kurdu: "projenin tamamen bitmesini
    istiyorum bana soru sorma"). Kullanıcının genel kurallarına göre sorulması gereken seçimler sorulamadığı için
    önerdiğim seçenekle verildi ve **ayrıca onaylanmadı**; yeniden sorma, ama kullanıcı değiştirmek isterse yer burasıdır:
    - Web'de yönlendirme: **React Router** (`react-router`; `BrowserRouter` + `Routes`).
    - i18n kütüphanesi: **i18next + react-i18next**; diller backend'deki gibi en (varsayılan) ve tr. Çeviri anahtarları
      derleme zamanında denetlenir (`src/i18n/i18next.d.ts`).
    - Form/validasyon kütüphanesi: **yok**. shadcn'in güncel CLI'ı `form` yerine `field` bileşenini veriyor ve bir
      form kütüphanesi kurmuyor; formlar `Field` + bileşen durumuyla yazıldı, doğrulama backend'de kaldı ve alan
      hataları `HttpValidationProblemDetails.errors` içinden okunuyor. Bu, Haldus'un doğrulamasını da gösteriyor.
    - Frontend test framework'ü: **kurulmadı** (genel kural: test altyapısı yoksa kendiliğinden kurma). Panel
      `pnpm typecheck`, `pnpm lint`, `pnpm build` ile ve çalışan backend'e karşı tarayıcıda elle doğrulandı.
      Sonradan, paket eklenmeden, Node'un yerleşik test çalıştırıcısıyla birim ve etkileşim testleri yazıldı
      (`pnpm test`, `pnpm test:e2e`; yukarıda, "Arayüz testlerinin ayrıntısı").
    - Bunlar yeni bağımlılıklardır (`react-router`, `i18next`, `react-i18next`) ve onayı alınmadı. Geri kalan
      bağımlılıklar genel kurallarda adı geçen araçlardan ve onların CLI'ından gelir. `oidc-client-ts` ise
      kullanıcının onayıyla eklendi (aşağıda, "Kimlik sağlayıcı").
  - Kimlik doğrulama: `.claude/rules/sample-shop-auth.md`.
  - Arayüzün dili `Accept-Language` ile backend'e de gider; backend'in mesajları o dilde döner.
  - Mobil uygulamada (aynı gerekçeyle, **ayrıca onaylanmadı**): yönlendirme genel kurallardaki Expo Router'dır; i18n
    web'deki gibi i18next + react-i18next, cihazın dili `expo-localization` ile okunur; oturum ve dil seçimi
    `expo-secure-store` ile saklanır (tarayıcıda `localStorage`); form kütüphanesi ve test altyapısı yoktur.
    `expo-localization` ve `expo-secure-store` da onayı alınmamış yeni bağımlılıklardır. `expo-auth-session`,
    `expo-web-browser` ve `expo-crypto` ise kullanıcının onayıyla eklendi (yukarıda, "Kimlik sağlayıcı").
  - Mobil uygulama Expo'nun web hedefinde ve Android'de (emülatör, Android 17, Expo Go 56) çalışan backend'e karşı
    doğrulandı: kayıt, alan hatası, ürünler, favori, sepet, e-posta doğrulama, uygulama yeniden başlatılınca
    oturumun güvenli depodan geri gelmesi, çıkış, parola sıfırlama. Emülatör `adb` ile sürüldü (`uiautomator dump`
    ile öğeler bulunup dokunuldu). iOS'ta çalıştırılmadı (Windows); gerçek bir Android cihazda da denenmedi.
  - Güvenlik düzeltmelerinden sonra (`427e54c`) panel tarayıcıda ve mobil uygulama Android emülatöründe yeniden
    denendi: doğrulanmamış personel adresinin ortak kaynağı yazamaması (Türkçe 403), kodun küçük harf ve tireyle
    kabul edilmesi, doğrulamadan sonra rolün gelmesi; mobilde eski oturumun reddi, kayıt, favori, sepet, doğrulama,
    çıkış, yanlış parolanın Türkçe mesajı, yeniden giriş. O turda mobilde parola sıfırlama ve uygulamayı kapatıp
    açma yinelenmedi; yenileme toleransının 409 yolu elle değil yalnızca testlerle sınandı.
  - Örnek backend üç ayar kazandı, üçü de varsayılan olarak kapalıdır: `Shop:CreateDatabase` (tabloları açılışta
    oluşturur; yalnızca arayüzleri denemek için), `Shop:CorsOrigins` (sayılan kaynaklara CORS izni) ve
    `Shop:DemoIdentity` (kimliği başlıklardan okur; testler için).
  - Örnek mağaza bu yüzden sayıları katı okur (`JsonNumberHandling.Strict`): varsayılan ayar şemaya `integer | string`
    yazıyor ve istemci tipleri her sayıyı `number | string` görüyordu. Bu, örnek uygulamanın ayarıdır; Haldus
    tüketen uygulamaya dayatmaz.
