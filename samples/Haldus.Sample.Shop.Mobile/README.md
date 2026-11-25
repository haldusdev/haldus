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
claim'den okur. Backend bir kimlik sağlayıcı da sunuyorsa giriş ekranında onunla giriş düğmesi de görünür
([aşağıda](#kimlik-sağlayıcıyla-giriş)).

- Token'lar cihazda işletim sisteminin güvenli deposunda durur (`lib/session.ts`, `lib/storage.ts`); güvenli depo
  büyük değerleri her platformda kabul etmediği için her token ayrı bir anahtardadır.
- `lib/api-fetch.ts` erişim token'ını `Authorization` başlığına koyar. Backend 401 dönerse yenileme token'ı ile yeni
  bir oturum alır ve isteği bir kez yineler; aynı anda 401 alan istekler tek bir yenilemeyi bekler. Yenileme de
  reddedilirse oturum kapanır.

- Giriş ekranı kayıt, giriş ve parola sıfırlamayı (e-postayla gelen kod) taşır. Hesap sekmesinde
  e-posta doğrulanır, parola değiştirilir ve çıkış yapılır; çıkış oturumu backend'de de iptal eder.
- Posta ayarı verilmediyse e-posta gönderilmez; yerelde denemek için backend geliştirme ortamında başlatılır
  (`-- --environment Development`) ve kod backend'in günlüğünden okunur.

Bilinen sınırlar: biyometrik kilit ve iki adımlı doğrulama yoktur.

### Kimlik sağlayıcıyla giriş

Giriş ekranı açılırken backend'e hangi giriş yollarını sunduğunu sorar (`GET /auth/config`). Yanıt bir sağlayıcı
taşıyorsa (`provider`: adresi, istemci kimliği, kapsamlar) giriş ekranında parola formunun altında "Kimlik
sağlayıcıyla giriş yap" düğmesi görünür. Yapılandırma yüklenirken veya okunamadığında ekran yalnızca parola formudur;
backend parola girişini kapattıysa (`passwordSignIn: false`) ve bir sağlayıcı sunuyorsa yalnızca düğme kalır.

Akış OpenID Connect'in yetkilendirme kodu + PKCE akışıdır ve [`expo-auth-session`](https://docs.expo.dev/versions/latest/sdk/auth-session/)
ile yapılır (`lib/provider.ts`, `lib/auth.ts`, `lib/provider-sign-in.ts`):

1. Sağlayıcının uçları adresinden keşfedilir (`<authority>/.well-known/openid-configuration`) ve durum (`state`) ile
   PKCE değerleri üretilir. Bu, düğmeye basılmadan önce yapılır: tarayıcılar açılır pencereye yalnızca dokunuşun
   hemen ardından izin verir.
2. Düğme sağlayıcının sayfasını açar: cihazda sistem tarayıcısının kimlik doğrulama oturumu, web'de bir açılır
   pencere.
3. Sağlayıcı uygulamaya bir kodla döner; uygulama kodu, PKCE doğrulayıcısıyla, sağlayıcının token ucunda token'lara
   çevirir. Uygulama gizli anahtar taşımaz (public client).
4. Sağlayıcının erişim token'ı ile backend'e hesabın kim olduğu sorulur (`GET /auth/me`) ve oturum saklanır.

Backend bu giriş için **kendi token'ını vermez**: sağlayıcının erişim token'ını (JWT) her uçta olduğu gibi kabul
eder. Bu yüzden iki oturum türü vardır ve tür oturumla birlikte saklanır (`lib/session.ts`):

| | Parola oturumu | Sağlayıcı oturumu |
|---|---|---|
| Token'ları veren | Backend | Kimlik sağlayıcı |
| Hesap kaydı (`…session.account`) | `accountId`, `email`, `tenantId`, `emailVerified` | Aynıları + `kind: "provider"` ve `provider: { clientId, tokenEndpoint }` |
| 401'de yenileme | `POST /auth/refresh` | Sağlayıcının token ucu (`grant_type=refresh_token`) |
| Yenileme token'ı | Her zaman vardır | Sağlayıcı vermeyebilir; o zaman anahtarı yoktur |
| Çıkış | Backend'de oturum iptal edilir | Yalnızca cihazdaki token'lar silinir |
| Hesap ekranı | E-posta doğrulama, parola değiştirme | İkisi de gizlenir (sağlayıcı yönetir) |

- Parola oturumunun kaydı tür taşımaz: biçimi bu özellikten önceki sürümün yazdığıyla aynıdır, böylece cihazda
  saklı eski oturumlar okunmaya devam eder. Tanınmayan bir tür oturum yok sayılır.
- Yenilemenin güvenceleri iki türde de aynıdır: aynı anda 401 alan istekler tek yenilemeyi bekler, yenilenen
  token'lar o sırada değişmiş bir oturuma (başka hesap, çıkış) yazılmaz ve oturumu yalnızca kesin bir ret bitirir.
  Sağlayıcıda kesin ret, token ucunun RFC 6749 §5.2'deki hata kodlarıdır (`invalid_grant`, `invalid_client`…); ağ
  hatası, 5xx, JSON olmayan bir yanıt ve `temporarily_unavailable` gibi kodlar oturumu kapatmaz.
- Sağlayıcı yenileme token'ı vermediyse oturum yenilenemez: backend erişim token'ını reddettiğinde (401) biter ve
  giriş ekranına dönülür. Yenileme token'ı çoğu sağlayıcıda `offline_access` kapsamıyla istenir; kapsamları backend
  bildirir.

#### Sağlayıcıda kaydedilecekler

İstemci gizli anahtarsız (public), yetkilendirme kodu + PKCE (S256) kullanan bir istemcidir. Dönüş adresi
`lib/provider.ts` içindeki `providerRedirectUri()` ile kurulur ve yolu giriş ekranınınkidir (`sign-in`): Android'de
dönüş bir derin bağlantıdır, Expo Router onu da açar ve var olmayan bir yol "bulunamadı" ekranına düşerdi. Uygulamanın
şeması `app.json` içinde zaten vardı (`haldus-shop`); bu özellik için değiştirilmedi.

| Nerede çalışıyor | Kaydedilecek dönüş adresi |
|---|---|
| Development build, mağaza derlemesi | `haldus-shop://sign-in` |
| Expo Go | `exp://<bilgisayarın adresi>:8081/--/sign-in` (ör. `exp://192.168.1.20:8081/--/sign-in`) |
| Web | `<uygulamanın kaynağı>/sign-in` (ör. `http://localhost:8081/sign-in`) |

- **Expo Go:** Expo, OAuth / OpenID Connect denemeleri için Expo Go'yu değil development build'i önerir. Expo Go'da
  şema uygulamanın değil Expo Go'nundur (`exp://`) ve adres bilgisayarın ağ adresine göre değişir; bazı sağlayıcılar
  böyle bir adresi kabul etmez, edenlerde de her değişimde yeniden kaydetmek gerekir.
- **Web:** keşif ve token istekleri tarayıcıdan gider; sağlayıcı uygulamanın kaynağına CORS izni vermelidir. Giriş
  bittiğinde açılır pencere uygulamanın adresine döner; uygulama o pencerede yalnızca dönüş adresini asıl pencereye
  iletir (`app/_layout.tsx`: `WebBrowser.maybeCompleteAuthSession()`).
- Erişim token'ı backend'in doğrulayabildiği bir JWT olmalıdır; alıcı (`audience`) ayarı sağlayıcıya ve backend'in
  yapılandırmasına bağlıdır.

#### Bilinen sınırlar

- **Çıkış yalnızca cihazdadır.** Sağlayıcının oturumu kapatılmaz (`end_session_endpoint` çağrılmaz) ve token'lar
  sağlayıcıda iptal edilmez: süreleri dolana kadar geçerli kalırlar, kullanıcı yeniden "sağlayıcıyla giriş"e
  bastığında sağlayıcı parola sormadan geri dönebilir.
- Kimlik token'ı (`id_token`) doğrulanmaz ve kullanılmaz; hesabın kim olduğunu backend söyler. `nonce` gönderilmez.
- Sağlayıcının token ucu oturumla birlikte saklanır; sağlayıcı ucunu taşırsa oturum yenilenemez ve yeniden giriş
  gerekir. Backend'in sunduğu sağlayıcı değişirse eski oturum ancak yenileme reddedilince ya da elle çıkışla biter.
- Yenileme başarılı olduğu halde backend yeni token'ı da reddederse (ör. sağlayıcıyla giriş backend'de kapatıldı)
  sağlayıcı oturumu kapatılır ve giriş ekranına dönülür; yoksa her istek sağlayıcıda bir yenileme token'ı harcardı.
- Sağlayıcının adresi ve keşif belgesinin söylediği yetkilendirme ve token uçları `https` olmalıdır. Düz `http`
  yalnızca cihazın kendisinde (`localhost`, `127.0.0.1`) ve, yalnızca geliştirme derlemesinde, Android emülatörünün
  bilgisayar adresinde (`10.0.2.2`) kabul edilir; kullanıcı bilgisi taşıyan adres (`http://localhost:1@başka.sunucu`)
  hiç kabul edilmez. Saklı token ucu her yenilemede yeniden denetlenir: kurala uymayan bir uç taşıyan eski oturum
  yenilenmez, biter.
- Güvenli depo yaklaşık 2 KB'tan büyük değerleri her platformda kabul etmez; çok sayıda claim taşıyan büyük bir
  erişim token'ı iOS'ta saklanamayabilir.
- Tarayıcıda iki sekme aynı anda yenilerse ve sağlayıcı yenileme token'ını her kullanımda değiştiriyorsa ikinci sekme
  ret alır; bu sekme ötekinin yazdığı token'larla sürer. Sağlayıcı eskimiş token'ın yeniden sunulmasını hırsızlık
  sayıp bütün token ailesini iptal ediyorsa (bazıları eder) iki sekme de bir sonraki yenilemede oturumdan düşer.
- Sağlayıcının giriş sayfası uygulamanın diline çevrilmez (`ui_locales` gönderilmez).
- Web'de PKCE değerleri tarayıcının şifreleme arayüzüyle üretilir; o yalnızca güvenli bağlamda vardır (`https` ya da
  `localhost`). Uygulama düz `http` ile bir ağ adresinden açılırsa giriş hazırlanamaz ve ekran sağlayıcıya
  ulaşılamadığını söyler.

## Komutlar

| Komut | Ne yapar |
|---|---|
| `pnpm dev` | Expo geliştirme sunucusu |
| `pnpm android` / `pnpm ios` / `pnpm web` | Doğrudan o hedefte açar |
| `pnpm generate:api` | `api` altındaki istemciyi backend'in `openapi.json` dosyasından yeniden üretir |
| `pnpm typecheck` | Tip denetimi |
| `pnpm test` | Oturum, giriş ve istek mantığının testleri (`tests/`) |
| `pnpm typecheck:tests` | Testlerin tip denetimi |
| `pnpm test:e2e` | Ekran testleri: uygulamayı web'e dışa aktarır ve gerçek bir tarayıcıda sürer (`tests/e2e`) |

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
| `lib` | Oturum, kimlik sağlayıcıyla giriş, kalıcı depo, API sarmalayıcısı, sepet ve favori mantığı |

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

**Kimlik sağlayıcıyla giriş yalnızca testlerle ve yalnızca web hedefinde doğrulandı.** Ekran testleri girişin
tamamını (açılır pencere, kodun çevrilmesi, oturumun saklanması, sağlayıcıdan yenileme, reddedilen yenileme) sahte
mağazanın içindeki bir sağlayıcı taklidine karşı sürer. Gerçek bir sağlayıcıya (Keycloak, Entra ID, Auth0…) ve
gerçek backend'e karşı denenmedi; cihazda ve emülatörde de denenmedi: sistem tarayıcısının açılması, uygulamanın
şemasına dönüş, Android'de Expo Router'ın dönüş bağlantısını ayrıca açması ve büyük bir token'ın güvenli depoya
sığması gözle görülmedi.

## Bu örnek için verilen kararlar

Bu seçimler kullanıcıya sorulmadan, önerilen seçenekle verildi (deponun `CLAUDE.md` dosyasında da yazılıdır):

- **i18n:** i18next + react-i18next; cihazın dili `expo-localization` ile okunur. Diller en (varsayılan) ve tr.
- **Kalıcı depo:** `expo-secure-store`.
- **Formlar:** ayrı bir form kütüphanesi yok; tek form giriş ekranıdır ve doğrulaması backend'dedir.
- **Kimlik doğrulama:** backend'in kendi parola girişi ve taşıyıcı token'ı; backend sunuyorsa ayrıca bir kimlik
  sağlayıcı (OpenID Connect, `expo-auth-session` ile; `expo-web-browser` ve `expo-crypto` onun gereğidir).
- **Test:** yeni bir paket eklenmedi. Oturum ve istek mantığı (`lib`: token'ın eklenmesi, 401'de tek yenileme, güvenli
  depodaki üç anahtar, oturumun türü ve eski kayıtların okunması, sağlayıcıyla giriş ve sağlayıcıdan yenileme, hata
  gövdeleri) Node'un yerleşik test çalıştırıcısıyla sınanır (`pnpm test`; `react-native`, `expo-secure-store`,
  `expo-auth-session` ve `fetch` elle yazılmış taklitlerle). `expo-auth-session` taklidi tarayıcıyı açmaz, ama token
  ucuyla kütüphanenin konuştuğu gibi konuşur (form gövdesi, yanıtın durumuna bakmadan JSON okuma, `error` alanında
  `TokenError`); kesin ret ile geçici arızanın ayrılması bu davranışa dayanır. `pnpm typecheck:tests`, Expo'nun
  dolaylı getirdiği `@types/node` paketine dayanır.
- **Ekran testleri:** yine paket eklenmedi. `pnpm test:e2e` uygulamayı web'e dışa aktarır (`dist-e2e`), kurulu bir
  Chromium tarayıcısını (Chrome veya Edge; `HALDUS_TEST_BROWSER` ile seçilir) başsız açar ve DevTools protokolüyle
  sürer. Backend çalışmaz; testler dışa aktarımı ve API'nin bellekteki bir taklidini sunan bir sunucuyla konuşur
  (`tests/e2e/support/shop.mts`). Senaryolar: giriş, kayıt, parola sıfırlama, ürün arama ve sayfanın sonunda
  sıradaki sayfanın istenmesi, favori ekleme / çıkarma, sepetin oluşması ve satırlarının değişmesi, e-posta
  doğrulama, dil değişimi, parola değişimi, çıkış, token yenileme. Sürülen web hedefidir: ekranlar, gezinme ve
  istekler cihazdakiyle aynı koddur, ama cihaza özgü olanlar (güvenli depo, klavye, güvenli alanlar, yerel
  bileşenlerin görünümü) bu testlerde yoktur; onlar emülatörde elle denenir (yukarıda).
  - Kimlik sağlayıcı senaryoları (`tests/e2e/provider.e2e.mts`): sahte mağaza aynı adreste bir sağlayıcının da
    taklididir (`/idp`: keşif, yetkilendirme, token ucu). Yetkilendirme sayfası kullanıcıya bir şey sormadan kodla
    geri döner; token ucu istemciyi, dönüş adresini, PKCE doğrulayıcısını ve kodun / yenileme token'ının tek
    kullanımını denetler. Senaryolar: düğmenin yalnızca sağlayıcı sunulunca görünmesi, sağlayıcı sunulurken ve
    yapılandırma okunamazken parola girişi, girişin tamamı, kapatılan pencere, sağlayıcının reddi, backend'in
    kabul etmediği token, ulaşılamayan sağlayıcı, sağlayıcıdan yenileme, reddedilen yenileme, yenileme sırasında
    sağlayıcının arızası, yenileme token'ı olmayan oturum, Hesap ekranı ve çıkış. Sürücü açılır pencereyi sürmez;
    pencereyi uygulama açar ve kapatır. Gerçek bir sağlayıcının giriş sayfası ve cihazdaki akış bu testlerde yoktur.
