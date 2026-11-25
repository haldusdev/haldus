# Örnek mağazanın mobil uygulaması

[Örnek mağazanın](../Haldus.Sample.Shop) API'sini tüketen küçük bir mobil uygulama: React Native + Expo, Expo Router,
[React Native Reusables](https://reactnativereusables.com) ve NativeWind. Haldus'un ürettiği sözleşmenin bir mobil
uygulamada nasıl kullanıldığını gösterir; üretim için bir başlangıç şablonu değildir.

Neleri gösterir:

- **Ürünler:** aşağı kaydırdıkça sayfa sayfa yüklenen liste (orval'ın ürettiği sonsuz sorgu), arama.
- **Favoriler:** geçerli kullanıcıya bağlı bağ (`MapAssociation<Favorite, Product>().ForCurrentUser()`):
  `PUT` / `DELETE /me/favorites/{productId}` ve ürünleri döndüren `GET /me/favorites`.
- **Sepet:** kullanıcıya ait kaynak (`/me/baskets`), gömülü satırlarıyla. Her değişiklik satırların tamamını
  gönderir; backend onları eşitler. Sepet kullanıldıkça yaşar (`Expiring`, `sliding`).
- **Dil:** uygulama cihazın diliyle açılır (en veya tr); seçilen dil `Accept-Language` ile backend'e de gider.

## Çalıştırmak

Önce örnek backend çalışmalıdır; nasıl başlatılacağı [yönetim panelinin README'sinde](../Haldus.Sample.Shop.Web)
anlatılır. Sonra:

```bash
pnpm install
```

```bash
pnpm dev
```

Expo geliştirme sunucusu açılır; `a` Android emülatöründe, `i` iOS simülatöründe (yalnızca macOS), `w` tarayıcıda
çalıştırır. Gerçek cihazda [Expo Go](https://expo.dev/go) ile QR kod okutulur.

Backend'in adresi `EXPO_PUBLIC_API_URL` ortam değişkeniyle verilir; verilmezse `http://localhost:5080` kullanılır.
Yerel ağdaki bir adrese `http://` ile bağlanmak parolayı ve token'ları ağda açık taşır; yalnızca kendi ağınızda
deneme için uygundur, gerçek bir kurulumda adres `https://` olmalıdır.

| Nerede çalışıyor | Adres |
|---|---|
| iOS simülatörü, tarayıcı | `http://localhost:5080` |
| Android emülatörü | `http://10.0.2.2:5080` |
| Gerçek cihaz | Bilgisayarın ağ adresi (ör. `http://192.168.1.20:5080`); backend de o adresi dinlemelidir (`--urls http://0.0.0.0:5080`) |

Tarayıcıda çalıştırırken uygulama backend'den farklı bir kaynaktadır; backend'e bu kaynak için izin verilmelidir
(`Shop__CorsOrigins__0=http://localhost:8081`). Cihazda ve emülatörde buna gerek yoktur.

## Kimlik doğrulama

Giriş e-posta ve parola iledir; ilk açılışta giriş ekranından bir hesap oluşturulur. Örnek backend ASP.NET Core'un
yerleşik taşıyıcı (bearer) token şemasını kullanır ve Haldus kullanıcıyı (favoriler ve sepet onundur) token'daki
claim'den okur.

- Token'lar cihazda işletim sisteminin güvenli deposunda durur (`lib/session.ts`, `lib/storage.ts`); güvenli depo
  büyük değerleri her platformda kabul etmediği için her token ayrı bir anahtardadır.
- `lib/api-fetch.ts` erişim token'ını `Authorization` başlığına koyar. Backend 401 dönerse yenileme token'ı ile yeni
  bir oturum alır ve isteği bir kez yineler; aynı anda 401 alan istekler tek bir yenilemeyi bekler. Yenileme de
  reddedilirse oturum kapanır.

- Giriş ekranı kayıt, giriş ve parola sıfırlamayı (e-postayla gelen kod) taşır. Hesap sekmesinde
  e-posta doğrulanır, parola değiştirilir ve çıkış yapılır; çıkış oturumu backend'de de iptal eder.
- Posta ayarı verilmediyse e-posta gönderilmez; yerelde denemek için backend geliştirme ortamında başlatılır
  (`-- --environment Development`) ve kod backend'in günlüğünden okunur.

Bilinen sınırlar: biyometrik kilit, ayrı bir kimlik sağlayıcı ve iki adımlı doğrulama yoktur.

## Komutlar

| Komut | Ne yapar |
|---|---|
| `pnpm dev` | Expo geliştirme sunucusu |
| `pnpm android` / `pnpm ios` / `pnpm web` | Doğrudan o hedefte açar |
| `pnpm generate:api` | `api` altındaki istemciyi backend'in `openapi.json` dosyasından yeniden üretir |
| `pnpm typecheck` | Tip denetimi |
| `pnpm test` | Oturum ve istek mantığının testleri (`tests/`) |
| `pnpm typecheck:tests` | Testlerin tip denetimi |

## API istemcisi

`api` klasörü [orval](https://orval.dev) çıktısıdır: tipler ve [TanStack Query](https://tanstack.com/query) hook'ları.
Elle yazılmaz ve elle düzenlenmez; backend'de bir uç veya DTO değişince `pnpm generate:api` ile yeniden üretilir ve
commit'lenir.

- Kaynak, backend'in build sırasında ürettiği `../Haldus.Sample.Shop/openapi.json` dosyasıdır; kopyalanmaz.
- Uygulama yalnızca kullandığı kaynakları üretir (`orval.config.ts` içinde `filters.tags`).
- Her istek `lib/api-fetch.ts` içindeki sarmalayıcıdan geçer: backend'in adresi, oturumun token'ı, dil ve hata
  davranışı oradadır.
- `lib/basket.ts` ve `lib/favorites.ts` üretilen hook'ları birleştirir; kendi isteklerini veya tiplerini yazmazlar.

## Yapı

| Yol | İçerik |
|---|---|
| `app` | Ekranlar (Expo Router): `sign-in`, `(tabs)` altında ürünler, favoriler, sepet, hesap |
| `api` | Üretilen istemci (orval) |
| `components/ui` | React Native Reusables bileşenleri; `pnpm dlx @react-native-reusables/cli@latest add <ad>` ile eklenir |
| `components` | Paylaşılan parçalar (ürün satırı, yükleniyor / boş / hata durumları) |
| `i18n` | Çeviri kaynakları (`locales/en.json`, `locales/tr.json`); anahtarlar derleme zamanında denetlenir |
| `lib` | Oturum, kalıcı depo, API sarmalayıcısı, sepet ve favori mantığı |

Oturum ve dil seçimi cihazda işletim sisteminin güvenli deposunda (`expo-secure-store`), tarayıcıda `localStorage`
içinde saklanır.

## Nasıl doğrulandı

`pnpm typecheck` ile ve çalışan backend'e karşı elle, iki hedefte:

- **Android** (emülatör, Android 17, Expo Go 56; backend `http://10.0.2.2:5080`): hesap oluşturma (alan hatası dahil),
  ürün listesi, favori ekleme, sepete ekleme, e-posta doğrulama (yanlış ve doğru kod), uygulama tümüyle kapatılıp
  açılınca oturumun güvenli depodan geri gelmesi, çıkış, parola sıfırlama, eski parolanın reddedilip yenisiyle giriş.
- **Web** (Expo'nun web hedefi): yukarıdakilere ek olarak geçersiz erişim token'ının yenilenmesi ve dil değişimi.

Güvenlik incelemelerinin düzeltmelerinden sonra (sekiz karakterlik kod, yalnızca personelin yazdığı ortak kaynaklar,
yenileme toleransı) Android'deki tur yinelendi: başka bir veritabanından kalan oturumun reddedilip giriş ekranına
dönülmesi, hesap oluşturma, panelden eklenen ürünün listede görünmesi, favori, sepet, kodun küçük harf ve tireyle
yazılarak doğrulanması, dilin Türkçeye çevrilmesi, çıkış, yanlış parolada backend'in Türkçe mesajı, doğru parolayla
giriş. Bu turda parola sıfırlama ve uygulamayı kapatıp açma yinelenmedi.

**iOS'ta çalıştırılmadı** (geliştirildiği makine Windows); gerçek bir Android cihazda da denenmedi. Klavyenin alanı
örtmesi, güvenli alanlar ve koyu tema Android'de ayrıca gözle denetlenmedi.

## Bu örnek için verilen kararlar

Bu seçimler kullanıcıya sorulmadan, önerilen seçenekle verildi (deponun `CLAUDE.md` dosyasında da yazılıdır):

- **i18n:** i18next + react-i18next; cihazın dili `expo-localization` ile okunur. Diller en (varsayılan) ve tr.
- **Kalıcı depo:** `expo-secure-store`.
- **Formlar:** ayrı bir form kütüphanesi yok; tek form giriş ekranıdır ve doğrulaması backend'dedir.
- **Kimlik doğrulama:** backend'in kendi parola girişi ve taşıyıcı token'ı; ayrı bir kimlik sağlayıcı yok.
- **Test:** yeni bir paket eklenmedi. Oturum ve istek mantığı (`lib`: token'ın eklenmesi, 401'de tek yenileme, güvenli
  depodaki üç anahtar, hata gövdeleri) Node'un yerleşik test çalıştırıcısıyla sınanır (`pnpm test`; `react-native`,
  `expo-secure-store` ve `fetch` elle yazılmış taklitlerle). Ekranların testi yoktur: onları çizmek bir React Native
  test kütüphanesi (yeni bir bağımlılık) ister. `pnpm typecheck:tests`, Expo'nun dolaylı getirdiği `@types/node`
  paketine dayanır.
