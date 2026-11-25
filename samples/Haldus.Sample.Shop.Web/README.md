# Örnek mağazanın yönetim paneli

[Örnek mağazanın](../Haldus.Sample.Shop) API'sini tüketen küçük bir yönetim paneli: Vite + React + TypeScript,
shadcn/ui (`sidebar-07` yerleşimi, `login-01` giriş ekranı) ve Tailwind. Haldus'un ürettiği sözleşmenin gerçek bir
arayüzde nasıl kullanıldığını gösterir; üretim için bir başlangıç şablonu değildir.

Neleri gösterir:

- **Ürünler:** sayfalı liste, arama, sıralama, oluşturma, güncelleme, silme. Doğrulamayı backend yapar; alan
  hataları yanıttan okunur ve ilgili alanın altında gösterilir.
- **Kategoriler:** ağaç (`Hierarchical`) ve sıra (`Orderable`); ürünü olan kategorinin silinememesi (409).
- **Siparişler:** durum makinesi. Taslak verilir, gönderilir, iptal edilir, yeniden açılır; her geçiş kendi ucudur
  (`POST /orders/{id}/place`). Sipariş sürüm kontrollüdür: sürüm `ETag` başlığından okunur ve her yazmada `If-Match`
  ile geri gönderilir. Oluşturma isteği `Idempotency-Key` taşır. Siparişler kiracıya aittir.
- **Dil:** arayüz İngilizce ve Türkçedir; seçilen dil `Accept-Language` ile backend'e de gider, böylece backend'in
  mesajları (doğrulama hataları, reddedilen geçişin nedeni) aynı dilde gelir.

## Çalıştırmak

Önce bir PostgreSQL ve örnek backend gerekir. Aşağıdaki parola yalnızca yerel bir deneme içindir; kendi değerinizi
yazın.

```bash
docker run -d --name haldus-shop-demo -e POSTGRES_PASSWORD=<parola> -e POSTGRES_DB=shop -p 127.0.0.1:5432:5432 postgres:17-alpine
```

Backend bağlantı dizesini ortamdan okur. `Shop__CreateDatabase=true` tabloları açılışta, yoksa oluşturur (yalnızca
bu deneme için; gerçek bir uygulama şemasını migration'larla yönetir). Var olan tabloları değiştirmez: örnek
güncellendikten sonra (yeni bir kolon geldiyse) deneme veritabanı silinip yeniden oluşturulur. PowerShell'de:

```powershell
$env:ConnectionStrings__Default = "Host=127.0.0.1;Port=5432;Database=shop;Username=postgres;Password=<parola>"
$env:Shop__CreateDatabase = "true"
dotnet run --project ../Haldus.Sample.Shop --urls http://localhost:5080
```

Sonra panel:

```bash
pnpm install
```

```bash
pnpm dev
```

Geliştirme sunucusu `/api` ile başlayan istekleri `http://localhost:5080` adresine yönlendirir (başka bir adres için
`SHOP_API_URL` ortam değişkeni). Giriş ekranında "Hesap oluştur" ile bir hesap açılır; her hesap kendi kiracısıyla
başlar.

## Kimlik doğrulama

Örnek backend e-posta ve parola ile giriş yapar ve ASP.NET Core'un yerleşik taşıyıcı (bearer) token şemasını kullanır
(`/auth/*`). Oturumlar sunucudadır: çıkış ve parola değişimi token'ları hemen geçersiz kılar. Haldus kullanıcıyı ve
kiracıyı token'daki claim'lerden okur. Panelde:

- Giriş ekranı kayıt, giriş ve parola sıfırlamayı (e-postayla gelen kod) taşır.
- E-postası doğrulanmamış hesaba üstte bir şerit gösterilir; kod orada girilir.
- Hesap menüsünden parola değiştirilir (hesabın bütün oturumları kapanır; bu sekme backend'in açtığı yeni oturumla
  devam eder) ve çıkış yapılır (oturum sunucuda iptal edilir; erişim token'ı geçmese de yenileme token'ıyla).

### Kimlik sağlayıcıyla giriş

Backend bir kimlik sağlayıcının (OpenID Connect) JWT erişim token'larını kabul edecek biçimde ayarlandıysa
(`Shop:Auth:Jwt`: `Authority`, `Audience`, `ClientId`; ayarlar [backend'in](../Haldus.Sample.Shop)
`Features/Auth/ExternalSignIn.cs` dosyasında) `GET /auth/config` yanıtında `provider` dolu gelir ve giriş ekranında
parola formunun altında "Kimlik sağlayıcınızla giriş yapın" düğmesi görünür. Yapılandırma yüklenirken veya
yüklenemezse yalnızca o düğme görünmez; parola formu olduğu gibi çalışır.

- Akış Authorization Code + PKCE'dir ve [`oidc-client-ts`](https://github.com/authts/oidc-client-ts) ile yürütülür.
  Panel herkese açık bir istemcidir (gizli anahtarı yoktur). Sağlayıcıda izinli yönlendirme adresi olarak panelin
  `/login/callback` adresi (ör. `http://localhost:5173/login/callback`) kayıtlı olmalıdır.
- Dönüş sayfası kodu token'lara çevirir, hesabın kim olduğunu sağlayıcının erişim token'ıyla backend'e sorar
  (`GET /auth/me`), oturumu yazar ve kullanıcının girişten önce istediği sayfaya gider. Sağlayıcı hata döndürürse,
  `state` bu sekmede başlatılmış bir girişe ait değilse veya backend token'ı kabul etmezse nedenini söyler ve giriş
  ekranına dönüş yolu verir; oturum açılmaz ve token'lar hiçbir yere yazılmaz.
- Backend sağlayıcı girişi için kendi token'ını vermez: her istek sağlayıcının erişim token'ını taşır. Token 401
  alınca sağlayıcının yenileme token'ı ile, yine sağlayıcıdan yenilenir (`signinSilent`; gizli iframe kullanılmaz);
  backend'in `/auth/refresh` ucu bu oturumlar için çağrılmaz. Sağlayıcı yenileme token'ı vermiyorsa (çoğu sağlayıcıda
  `offline_access` kapsamı ister; kapsamlar backend'in `Scopes` ayarından gelir) oturum, erişim token'ı reddedildiği
  anda biter.
- Hesap menüsünde parola değiştirme, üstte e-posta doğrulama şeridi bu oturumda gösterilmez: ikisi de parola
  hesaplarının uçlarıdır.
- Çıkış yalnızca bu tarayıcıdaki oturumu siler.

Posta ayarı (`Shop__Smtp__Host` …) verilmediyse e-posta gönderilmez. Yerelde denemek için backend geliştirme
ortamında başlatılır (`dotnet run … -- --environment Development`); kod o zaman backend'in günlüğüne yazılır.

Panelin kimlikle ilgili kodu `src/lib` altındadır:

- `session.ts`: token'lar, hesabın kimliği ve oturumun türü (`kind`: `password` veya `provider`; sağlayıcı
  oturumunda token'ları veren sağlayıcının adresi ve istemci kimliği de). `localStorage` içinde durur; bu, sayfaya
  sızan bir betiğin onları okuyabileceği anlamına gelir. Örnek için kabul edilmiş bir ödünleşimdir; üretimde token'ları
  sayfanın betiğinden saklayan bir düzen (`HttpOnly` çerez) tercih edilir. Kimlik sağlayıcıyla giriş bunu değiştirmez:
  sağlayıcının erişim ve yenileme token'ları da aynı kayıtta durur. Tür alanı eklenmeden önce yazılmış oturumlar
  parola oturumu olarak okunur; kimse oturumdan düşmez.
- `api-fetch.ts`: erişim token'ını `Authorization` başlığına koyar. Backend 401 dönerse yenileme token'ı ile yeni
  token'lar alır ve isteği bir kez yineler; aynı anda 401 alan istekler tek bir yenilemeyi bekler, sekmeler yenilemeyi
  sıraya sokar (Web Locks). Yenilenen token'lar yalnızca hâlâ aynı olan oturuma yazılır; oturumu yalnızca kesin bir ret
  bitirir (backend'in 400 / 401 yanıtı, sağlayıcının `invalid_grant` / `invalid_client` / `unauthorized_client`
  yanıtı), ağ ve sunucu hataları bitirmez. Bu kurallar iki oturum türü için aynı koddur; yalnızca yenilemeyi kimin
  yaptığı değişir.
- `provider.ts`: `oidc-client-ts` ile konuşan tek dosya: yönlendirmeyi başlatır, dönüşü tamamlar, sağlayıcıdan
  yeniler. Kitaplığın ayarları orada, gerekçeleriyle yazılıdır. Özeti: kitaplığın kullanıcı deposu bellektedir ve her
  işlemden sonra boşaltılır, yani token'lar yalnızca `session.ts`'in kaydında durur ve kimlik token'ı (`id_token`) ile
  profil hiçbir yere yazılmaz; `state` ve PKCE doğrulayıcısı yönlendirme süresince `sessionStorage`'da bekler ve
  kullanılınca silinir; kitaplığın zamanlayıcıyla yenilemesi, iframe'leri ve `userinfo` isteği kapalıdır; sağlayıcıya
  giden istekler 10 saniyede yanıtlanmazsa bırakılır (oturum kapanmaz).
- `auth.ts`: giriş yanıtından veya sağlayıcıdan dönüşten oturumu açar, çıkışta parola oturumunu backend'de kapatır ve
  yenilemeyi (oturumun türüne göre backend'e veya sağlayıcıya) `api-fetch.ts`'e bağlar.

**Ürün ve kategorileri yalnızca personel yazar.** Kayıt herkese açıktır, ama kimseye ait olmayan (ortak) kaynakları
değiştirmek `staff` rolü ister; öteki hesaplar onları yalnızca okur ve yazmaya çalışınca 403 alır. Paneli denemek
için backend, giriş yapacağınız e-posta personel sayılarak başlatılır:

```powershell
$env:Shop__Auth__Staff__0 = "siz@example.com"
```

Rol, hesap e-postasını doğruladıktan sonra geçerli olur (üstteki şeritten; kod, geliştirme ortamında backend'in
günlüğüne yazılır): adres kullanıcının yazdığı bir metindir ve doğrulanmadan yetki vermez. Siparişler kiracıya aittir
ve bu kuralın dışındadır: her hesap kendi siparişlerini yazar.

Bilinen sınırlar: rol tektir ve yapılandırmadan gelir. İki adımlı doğrulama, e-posta değiştirme ve hesap silme
yoktur. Kimlik sağlayıcıyla girişin sınırları:

- **Yalnızca Keycloak'a karşı denendi.** Akış, testlerde panelle aynı adresten sunulan sahte bir sağlayıcıya karşı,
  gerçek `oidc-client-ts` ile sınanır (aşağıda) ve gerçek backend ile kapsayıcıda çalışan Keycloak'a karşı tarayıcıda
  elle denendi (aşağıda, "Elle doğrulama"). Entra ID, Auth0 gibi başka ürünlerle çalıştırılmadı.
- Çıkış sağlayıcının çıkış ucunu (`end_session_endpoint`) çağırmaz ve token'ları sağlayıcıda iptal etmez: kullanıcının
  sağlayıcıdaki oturumu açık kalır (düğmeye yeniden basınca parola sorulmadan girilebilir) ve erişim token'ı süresi
  dolana kadar geçerlidir. Backend'de de bu oturumlar için kapatılacak bir kayıt yoktur.
- Yetkilendirme isteğine yalnızca kapsamlar eklenir. API için ayrı bir `audience` / `resource` parametresi isteyen
  sağlayıcılar (ör. Auth0) için ayar yoktur; backend ise yalnızca kendi `Audience` değerini taşıyan JWT erişim
  token'larını kabul eder.
- Kimlik token'ının imzası tarayıcıda doğrulanmaz (`oidc-client-ts` 3 doğrulamaz) ve içeriği kullanılmaz: hesabı,
  erişim token'ını doğrulayan backend tanıtır.
- Tek bir sağlayıcı sunulur ve düğmede adı yazmaz (backend bir ad bildirmez). `/auth/config` yanıtındaki
  `passwordSignIn` ve `serverSignIn` alanları panelde kullanılmaz: parola formu her zaman gösterilir, backend'in çerezli
  sunucu tarafı girişi için düğme yoktur.
- Sağlayıcı oturumunun e-postası panelden doğrulanamaz; hesabın doğrulanmış (ve personel) sayılması backend'in
  sağlayıcıya güvenme ayarına bağlıdır. Güvenilen sağlayıcının doğrulanmış olarak taşıdığı bir adres için yetkili
  sağlayıcıdır: aynı adresle açılmış bir parola hesabı, adresini kodla doğrulasa da personel sayılmaz.
- Sağlayıcının yenileme isteği kilit altında yapılır; Web Locks'un olmadığı (güvenli olmayan) bir bağlamda, yenileme
  token'ını döndüren bir sağlayıcıda iki sekme aynı anda yenilerse biri reddedilebilir. Reddedilen sekme ötekinin
  yazdığı token'ları alır, ama sağlayıcı yeniden kullanımı hırsızlık sayıp bütün token ailesini iptal ediyorsa oturum
  kapanır.
- `state` kaydı sekmeye özgüdür (`sessionStorage`): sağlayıcı kullanıcıyı başka bir sekmeye veya pencereye geri
  gönderirse (bazı uygulama içi tarayıcılar) giriş tamamlanmaz ve yeniden başlatmak gerekir.
- `oidc-client-ts` ana pakete girer (küçültülmüş ~75 kB); sağlayıcı sunulmayan kurulumlarda da indirilir.

## Komutlar

| Komut | Ne yapar |
|---|---|
| `pnpm dev` | Geliştirme sunucusu |
| `pnpm generate:api` | `src/api` altındaki istemciyi backend'in `openapi.json` dosyasından yeniden üretir |
| `pnpm typecheck` | Tip denetimi |
| `pnpm test` | Oturum ve istek mantığının ve bileşenlerin ilk çiziminin testleri (`tests/`) |
| `pnpm test:e2e` | Etkileşim testleri: paneli derler ve gerçek bir tarayıcıda sürer (`tests/e2e`) |
| `pnpm lint` | ESLint |
| `pnpm build` | Tip denetimi + üretim derlemesi |
| `pnpm format` | Prettier |

## API istemcisi

`src/api` [orval](https://orval.dev) çıktısıdır: tipler ve [TanStack Query](https://tanstack.com/query) hook'ları.
Elle yazılmaz ve elle düzenlenmez; backend'de bir uç veya DTO değişince `pnpm generate:api` ile yeniden üretilir ve
commit'lenir.

- Kaynak, backend'in build sırasında ürettiği `../Haldus.Sample.Shop/openapi.json` dosyasıdır; kopyalanmaz.
- Örnek mağazanın yetmişten çok kaynağı vardır. Panel yalnızca kullandıklarını üretir (`orval.config.ts` içinde
  `filters.tags`); bütün uçların istemcisi [Haldus.Sample.Shop.Client](../Haldus.Sample.Shop.Client) klasöründedir.
- Her istek `src/lib/api-fetch.ts` içindeki sarmalayıcıdan geçer: oturumun token'ı, dil ve hata davranışı oradadır.
  Başarısız yanıtta gövde (`ProblemDetails`) fırlatılır; hook'ların hata tipi de odur.

```tsx
const order = useGetOrder(id)
const etag = order.data?.headers.get("ETag") ?? ""

// Geçiş, okunan sürüme bağlanır; kayıt bu sırada değiştiyse backend 412 döner.
const ship = useShipOrder({ request: { headers: { "If-Match": etag } } })
```

## Yapı

| Yol | İçerik |
|---|---|
| `src/api` | Üretilen istemci (orval) |
| `src/components/ui` | shadcn/ui bileşenleri; `pnpm dlx shadcn@latest add <ad>` ile eklenir |
| `src/components` | Yerleşim (`dashboard-layout`, `app-sidebar`, `nav-*`), giriş formu ve paylaşılan parçalar |
| `src/pages` | Ekranlar |
| `src/i18n` | Çeviri kaynakları (`locales/en.json`, `locales/tr.json`); anahtarlar derleme zamanında denetlenir |
| `src/lib` | Oturum, API sarmalayıcısı, hata ve biçimlendirme yardımcıları |

## Bu örnek için verilen kararlar

Bu seçimler kullanıcıya sorulmadan, önerilen seçenekle verildi (deponun `CLAUDE.md` dosyasında da yazılıdır):

- **Yönlendirme:** React Router.
- **i18n:** i18next + react-i18next; diller en (varsayılan) ve tr.
- **Formlar:** ayrı bir form kütüphanesi yok. Formlar shadcn `Field` bileşenleri ve bileşen durumuyla yazılır;
  doğrulama backend'dedir.
- **Kimlik doğrulama:** backend'in kendi parola girişi ve taşıyıcı token'ı; backend bir kimlik sağlayıcı tanıtıyorsa
  onun yanında OpenID Connect ile giriş (`oidc-client-ts`).
- **Test:** test için yeni bir paket eklenmedi. Oturum ve istek mantığı (`src/lib`: token'ın eklenmesi, 401'de tek
  yenileme, sekmeler arası eşitleme, hata gövdeleri, oturum türü ve eski kayıtların okunması, sağlayıcıdan yenileme ve
  çıkış) Node'un yerleşik test çalıştırıcısıyla sınanır (`pnpm test`; `tests/` altında, `fetch`, `localStorage` ve Web
  Locks elle yazılmış taklitlerle). Sağlayıcıyla ilgili testlerde `oidc-client-ts` yerine de bir taklit yüklenir
  (`tests/stubs/oidc-client-ts.ts`): bu testler protokolü değil, panelin kitaplığı nasıl çağırdığını ve sonucuyla ne
  yaptığını sınar. Taklidin varsaydıkları ayrıca gerçek kitaplığa sorulur (`tests/provider-library.test.ts`: yenileme
  isteğinin biçimi, hangi yanıtın kesin ret sayıldığı, zaman aşımı, iframe'e düşülmemesi; yalnızca `fetch` taklit
  edilir).
  Bileşenler ve sayfalar `react-dom/server`
  ile metne çizilip çıktıları denetlenir (`tests/ui`): etiketler, erişilebilir adlar, iki dilde de çevrilmemiş metin
  kalmaması, giriş yönlendirmeleri, yüklenen ve boş liste durumları. Bu testler yalnızca ilk çizimi görür:
  tıklama, yazma, veri yükleme ve açılır pencerelerin içeriği orada sınanmaz.
- **Etkileşim testleri:** yine paket eklenmedi. `pnpm test:e2e` paneli derler, kurulu bir Chromium tarayıcısını (Chrome
  veya Edge; `HALDUS_TEST_BROWSER` ile seçilir) başsız açar ve DevTools protokolüyle sürer: tıklama ve yazma gerçek
  girdi olaylarıdır, açılır listeler ve pencereler kullanıcının elindeki gibi davranır. Backend çalışmaz; testler
  derlenmiş paneli ve API'nin bellekteki bir taklidini aynı adresten sunan bir sunucuyla konuşur
  (`tests/e2e/support/shop.ts`; yanıtları orval'ın tipleriyle yazılır). Her senaryo bir işlemden sonra giden isteği
  (yol, gövde, `Authorization`, `Accept-Language`, `If-Match`, `Idempotency-Key`) ve yanıtın sayfaya yansımasını
  denetler: giriş, kayıt, parola sıfırlama, dil değişimi, ürünlerde arama / sıralama / sayfalama / oluşturma /
  düzenleme / silme, siparişin iş akışı ve sürüm çakışması, kategoriler, e-posta doğrulama, token yenileme (tek
  yenileme, reddedilen yenileme, sunucu hatası, 409) ve iki sekmenin oturumu paylaşması. Kimlik sağlayıcıyla giriş de
  burada, gerçek `oidc-client-ts` ile sınanır: aynı sunucu `/idp` altında sahte bir sağlayıcı gibi davranır
  (`tests/e2e/support/identity-provider.ts`: keşif belgesi, hemen kodla geri yönlendiren yetkilendirme ucu, PKCE
  doğrulayıcısını ve istemciyi denetleyen, kodu ve yenileme token'ını bir kez kullandıran token ucu). Senaryolar:
  düğmenin yalnızca backend sağlayıcı tanıtınca görünmesi, yönlendirmeyle giriş ve isteklerin sağlayıcının token'ını
  taşıması, girişten önce istenen sayfaya dönüş, süresi dolan token'ın sağlayıcıdan bir kez yenilenmesi, reddedilen
  yenileme, sağlayıcı arızası, yenileme token'sız oturum, iki sekme, dönüşteki hatalar (sağlayıcının reddi, yabancı
  `state`, backend'in token'ı reddetmesi), çıkış ve parola girişinin sağlayıcı sunulurken de çalışması. Taklit
  backend'in yerine geçmez: backend'in kendi davranışı entegrasyon testlerindedir, panelin gerçek backend'le birlikte
  çalıştığı elle doğrulanır. Tarayıcı bulunamazsa testler atlanır.
- **Elle doğrulama:** panel, çalışan backend'e karşı tarayıcıda denendi. Güvenlik incelemelerinin düzeltmelerinden
  sonraki son turda: e-postasını doğrulamamış personel adresi ürün oluşturamadı (backend'in Türkçe 403 mesajı
  pencerede göründü), "Yeniden gönder" yeni bir kod üretti, küçük harf ve tireyle yazılan kod hesabı doğruladı ve
  ardından ürün oluşturulabildi. Kimlik sağlayıcıyla giriş, gerçek backend ve kapsayıcıda çalışan Keycloak (26.4) ile
  ayrıca denendi; sağlayıcı girişinin son güvenlik düzeltmelerinden sonra yinelenen turda: giriş ekranındaki düğme
  Keycloak'ın giriş sayfasına götürdü (PKCE, `S256`), dönüşte istenen sayfa sağlayıcı oturumuyla açıldı, sağlayıcının
  doğruladığı personel adresiyle ürün oluşturuldu, "her yerden çık"tan sonra eski erişim token'ı 401 aldı ve panel
  token'ı Keycloak'tan yenileyip devam etti, hesap menüsünde parola değiştirme görünmedi, çıkış oturumu sildi ve
  sağlayıcı açıkken parola ile kayıt çalıştı. Adres kuralı değiştikten sonraki turda: sağlayıcıyla giren personelin
  adresiyle ayrıca bir parola hesabı açılabildi ve kodla doğrulandı, ama ürün oluşturamadı ("Only staff can change
  this resource."); sağlayıcının hesabı aynı adresle ürün oluşturmaya devam etti.
