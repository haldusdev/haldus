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

Bilinen sınırlar: çıkış yalnızca token'ları cihazdan siler (sunucuda iptal yoktur), e-posta doğrulaması, parola
sıfırlama ve biyometrik kilit yoktur.

## Komutlar

| Komut | Ne yapar |
|---|---|
| `pnpm dev` | Expo geliştirme sunucusu |
| `pnpm android` / `pnpm ios` / `pnpm web` | Doğrudan o hedefte açar |
| `pnpm generate:api` | `api` altındaki istemciyi backend'in `openapi.json` dosyasından yeniden üretir |
| `pnpm typecheck` | Tip denetimi |

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

`pnpm typecheck` ile ve Expo'nun **web** hedefinde, çalışan backend'e karşı elle: hesap oluşturma (alan hatası
dahil), giriş, sayfa yenilenince oturumun geri gelmesi, geçersiz erişim token'ının yenilenmesi, ürün listesi, favori
ekleme, sepete ekleme ve adet değiştirme, dil değişimi. **Android ve iOS'ta çalıştırılmadı** (geliştirildiği makinede
emülatör yoktu); o hedeflere özgü davranış (güvenli depo, klavye, güvenli alanlar) sınanmadı.

## Bu örnek için verilen kararlar

Bu seçimler kullanıcıya sorulmadan, önerilen seçenekle verildi (deponun `CLAUDE.md` dosyasında da yazılıdır):

- **i18n:** i18next + react-i18next; cihazın dili `expo-localization` ile okunur. Diller en (varsayılan) ve tr.
- **Kalıcı depo:** `expo-secure-store`.
- **Formlar:** ayrı bir form kütüphanesi yok; tek form giriş ekranıdır ve doğrulaması backend'dedir.
- **Kimlik doğrulama:** backend'in kendi parola girişi ve taşıyıcı token'ı; ayrı bir kimlik sağlayıcı yok.
- **Test:** test altyapısı kurulmadı.
