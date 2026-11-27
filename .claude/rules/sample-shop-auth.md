---
paths:
  - "samples/Haldus.Sample.Shop*/**"
  - "tests/Haldus.IntegrationTests/**"
  - "tools/Haldus.LoadTest/**"
---

# Örnek mağaza: kimlik doğrulama

Örnek mağazanın kimlik doğrulamasına ilişkin kararlar ve gerekçeleri. Kalın cümleler kuraldır.

- Kimlik doğrulama (planlardan sonra; kimlik sağlayıcı seçimi sorulamadığı için yeni bağımlılık gerektirmeyen yol
  seçildi, **ayrıca onaylanmadı**): örnek mağaza e-posta + parola ile giriş yapar ve ASP.NET Core'un yerleşik
  taşıyıcı token şemasını kullanır (`AddBearerToken`; `Features/Auth/AuthEndpoints.cs`). Hesap (`ShopAccount`) elle
  yazılmış bir tablodur, Haldus kaynağı değildir; her hesap kendi kiracısıyla doğar. Parola `PasswordHasher<T>` ile
  özetlenir; ikisi de paylaşılan çatıdadır, paket eklenmedi. ASP.NET Core Identity kullanılmadı.
  - Haldus'ta hiçbir şey değişmedi: kullanıcıyı ve kiracıyı zaten claim'lerden okuyordu.
  - **Kimlik sağlayıcı** (kullanıcı kararı, 2026-11-25; `Features/Auth/ExternalSignIn.cs`). Parola girişi her zaman
    vardır; sağlayıcı iki ayrı yoldan, her biri kendi ayarıyla eklenir ve ikisi de varsayılan olarak kapalıdır:
    - `Shop:Auth:Jwt` (`Authority`, `Audience`, `ClientId`): API, sağlayıcının JWT erişim token'ını kabul eder
      (`Microsoft.AspNetCore.Authentication.JwtBearer`). Panel ve mobil uygulamanın sağlayıcıyla girişi buna
      dayanır: akışı kendileri yapar (Authorization Code + PKCE) ve token'ı sunarlar. `Audience` zorunludur (başka
      bir API için verilmiş token kabul edilmez).
    - `Shop:Auth:Oidc` (`Authority`, `ClientId`, `ClientSecret`): backend'in kendisi tarayıcıyı sağlayıcıya
      yönlendirir (`GET /auth/oidc/sign-in?returnUrl=`) ve çerezle oturum açar
      (`Microsoft.AspNetCore.Authentication.OpenIdConnect`). Çerez kendiliğinden gönderildiği için, çerezle kimliği
      doğrulanan ve yazan her istek `X-Shop-Request` başlığını taşımalıdır (CSRF). Giriş istemeyen uçlarda kural
      ikiye ayrılır: çerezin oturumunu değiştiren uç (`/auth/oidc/sign-out`; `ChangesSession` metadatası) başlığı
      yine ister, ötekilerde (parola girişi, kayıt, yenileme, token'larımızın çıkışı, misafir sepeti) başlıksız
      istek reddedilmez ama çerez kimlik sayılmaz, istek kimliksiz sürer. Başlığı giriş istemeyen her uçta isteyen
      ara hal, tarayıcıda çerez dururken parola girişini 403 ile kapatıyordu (bağımsız incelemede çıktı). **Giriş
      istemeyen ama çerezin oturumunu değiştiren yeni bir uç `ChangesSession` ile işaretlenmelidir.** Çerezle çıkış,
      çerezi taşımayan isteğe çerezi sildirmez (çerez `SameSite=Lax` olduğu için başka bir siteden gönderilen form
      onu taşımaz; yanıt yine de sildirseydi o site kullanıcıyı oturumdan düşürebilirdi). Aynı nedenle sağlayıcının
      ön kanal çıkış adresi (`/signout-oidc`) kapalıdır (404). Dönüş adresi
      yalnızca uygulamanın içindeki bir yol olabilir. Bozuk veya sahte bir
      dönüş isteği 400 alır; günlüğe yalnızca hatanın türü yazılır (metni isteği yapanın yazdığı bir metin olabilir).
      Bu uçlar yalnızca ayar verildiyse eşlenir ve şemaya girmez. Bir vekilin arkasında
      istek şeması da vekilden okunur (`X-Forwarded-Proto`; `Shop:KnownProxies`).
      Çerez kendi başına geçerli değildir: girişte sunucuda bir oturum (`ShopSession`) açılır ve çerez ona bağlanır
      (`sid`); her istekte oturumun açık olduğu sorulur ve kimlik hesaptan yeniden kurulur (rol ve doğrulama
      bilgisi çerezin sekiz saatlik ömrü boyunca donmaz). `POST /auth/oidc/sign-out` ve `/auth/logout-all` oturumu
      kapatır. İlk yazımda çerez sekiz saat boyunca iptal edilemiyordu (otomatik incelemede çıktı).
    - Biri açıksa varsayılan şema, isteğe bakıp seçen bir şemadır (`ExternalSignIn.Select`): nokta ile ayrılmış üç
      parçalı taşıyıcı token sağlayıcının JWT'sidir, öteki taşıyıcı token bizim token'ımızdır, başlık yoksa çerez.
      İkisi de kapalıyken kimlik doğrulama eskisi gibi, yalnızca kendi şemamızla kurulur.
    - Sağlayıcının tanıttığı kişi (`iss` + `sub`) yerel bir hesaba çevrilir (`ShopAccount.ExternalIssuer` /
      `ExternalSubject`); ilk gelişinde hesap ve kiracısı oluşturulur ve çıkan claim'ler parola girişindekilerle
      aynıdır. Her istekte bir sorgudur. **Var olan bir hesaba adres üzerinden bağlanılmaz** (aynı adresi taşıyan bir
      dış kimlik, parolayla açılmış hesabı ele geçirmemelidir; kendi ayrı hesabını ve kiracısını alır). Adres
      yalnızca sağlayıcı onu doğruladıysa (`email_verified`) hesaba yazılır; değilse hesap, kimseye ait olamayacak
      bir adresle (`…@external.invalid`) ve doğrulanmamış olarak açılır. Yer tutucu adres rastgeledir.
      **Adres yalnızca parola hesapları arasında benzersizdir** (`UX_ShopAccounts_Email` süzülmüş bir indekstir:
      `ExternalIssuer IS NULL`): parola uçları hesabı adresle bulur, sağlayıcının hesabı ise kimliğiyle (`iss` +
      `sub`) bulunur ve adresi, sağlayıcının doğruladığı bir bilgidir. Aynı adres bu yüzden hem bir parola hesabında
      hem bir sağlayıcı hesabında durabilir; ikisi ayrı hesaplardır. İlk yazımda benzersizlik bütün hesapları
      kapsıyordu: personel adresiyle önceden (doğrulamadan) parola hesabı açan biri, adresin sahibinin sağlayıcıyla
      gelince adresini ve rolünü almasını kalıcı olarak engelleyebiliyordu (ikinci incelemede çıktı; önce bilinen
      sınır olarak yazılmış, sonra böyle kapatılmıştır; ayrıca onaylanmadı). **Adresle hesap arayan yeni bir kod
      `PasswordAccounts` üzerinden okumalıdır; `Set<ShopAccount>()` üzerinden adresle arama birden çok hesap bulabilir.**
      Adres paylaşılabildiği için rol ayrıca korunur: **adreslerine şu an güvenilen bir sağlayıcının hesabı bir
      adresi doğrulanmış olarak taşıyorsa, aynı adresli parola hesabı personel rolü almaz**
      (`AuthEndpoints.IsStaffAsync`; her token verilişinde ve `/auth/me`'de sorulur). Bu kural olmasa
      personelin posta kutusunu bir kez okuyabilen biri aynı adresle bir parola hesabı açıp kodla doğrular ve role,
      sağlayıcının denetimlerinin (iki adımlı doğrulama, hesabın kapatılması) dışında kalan kalıcı bir parolayla
      ulaşırdı (süzülmüş indeksin incelemesinde çıktı). Kural yalnızca şu an güvenilen sağlayıcıların hesaplarına
      bakar (`ShopAuthOptions.TrustedIssuers`): güveni sonradan kapatılmış bir sağlayıcının eskiden yazdığı adres,
      adresin sahibini rolünden etmez. Hesaplar sağlayıcının adıyla (token'daki `iss`; hesabın `ExternalIssuer`'ı)
      tanınır ve ad, ayardaki değerle (`Issuer`, verilmezse `Authority`) küçük harfe çevrilip sondaki eğik çizgi
      sayılmadan, veritabanında değil kodda karşılaştırılır (`ExternalSignIn.NormalizeIssuer`; veritabanının
      `lower()`'ı yerele göre değişir). **Adreslerine güvenilen bir sağlayıcının `iss` değeri ayardakiyle
      eşleşmiyorsa giriş reddedilir** (`ResolveAsync`; günlük iki değeri de söyler): ASP.NET Core `iss`'i keşif
      belgesindeki değerle doğrular, `Authority` ile değil, ve ikisi farklıysa kural bu hesapları göremez, sessizce
      işlemezdi (kuralı sağlayıcıyla sınırlayan ilk yazımda öyleydi; incelemede çıktı). Öyle bir kurulumda
      `Shop:Auth:Jwt:Issuer` / `Shop:Auth:Oidc:Issuer` token'daki değere eşitlenir. **Parola hesabına rol veren yeni
      bir yol `IsStaffAsync`'ten geçmelidir; `ShopAuthOptions.IsStaff` tek başına yetmez.**
      Kuralın koruması, sağlayıcının hesabı adresi taşımaya başladıktan sonra başlar; ondan önce (personel
      sağlayıcıyla hiç girmediyse ya da token'ı adresi taşımıyorsa) parola hesabında rolün dayanağı yine adrese
      gönderilen koddur. Bunu da kapatmak isteyen `Shop:Auth:StaffThroughProviderOnly` ayarını açar (varsayılan
      kapalı; ayrıca onaylanmadı): rolü o zaman yalnızca sağlayıcının hesapları alır ve adreslerine güvenilen bir
      sağlayıcı ayarlı değilse uygulama açılmaz. Personel listesindeki adres, hesabın adresi gibi küçük harfe
      çevrilip birebir karşılaştırılır (gevşek karşılaştırma `σ` / `ς` gibi çiftlerde başka bir posta kutusunu eşit
      sayardı). Bilinen etkiler: rol token'a yazıldığı için parola hesabı, erişim token'ı yenilenene kadar (ömrü
      bir saattir; `AddBearerToken` varsayılanı) rolünü korur; güvenilen sağlayıcı bir adresi başka bir kimliğe de
      doğrulanmış olarak verirse o kimlik de rolü alır (güvenin anlamı budur); `iss` değeri kiracıya göre değişen
      (çok kiracılı, şablonlu) bir sağlayıcı, adreslerine güvenilerek kullanılamaz (tek bir `Issuer` değeri
      yazılabilir). Süzülmüş indeks, `Shop:CreateDatabase` ile daha
      önce oluşturulmuş bir veritabanında yoktur (örnekte migration yok): o veritabanında adresi paylaşan bir
      sağlayıcı hesabı yazılamaz (500); veritabanı yeniden oluşturulur.
      Hesap açılırken yalnızca bir çakışma karşılanır (aynı kimliğin eşzamanlı ilk isteği); başka bir hata
      yutulmaz, istek hata verir (yönlendirmeli girişte de 400 ile örtülmez, 500'dür) ve sonraki istek yeniden
      dener. Adresini ilk gelişinde alamamış hesap (erişim token'ı adresi taşımıyordu, sağlayıcı henüz
      doğrulamamıştı) doğrulanmış adres sonradan gelirse onu alır; yoksa sonsuza kadar doğrulanmamış kalırdı. Bir
      kez yazılan adres değişmez.
    - **Sağlayıcı hesabının parolası yoktur ve parola uçlarından bir parola da edinemez.** Giriş, "parolamı
      unuttum" ve sıfırlama uçları öyle bir hesabı hiç yokmuş gibi yanıtlar (`PasswordAccounts`); parola değiştirme,
      e-posta doğrulama ve kodu yeniden gönderme 409 döner (`ManagedByProvider`). İlk yazımda sıfırlama ucu hesaba
      parola koyabiliyordu: adresin posta kutusunu bir kez okuyabilen biri kalıcı bir parola ve bizim token'larımızı
      alır, sağlayıcının denetimlerini (iki adımlı doğrulama, hesabın kapatılması) aşardı (bağımsız incelemede
      çıktı). **Hesabı e-postayla bulan yeni bir parola ucu `PasswordAccounts` üzerinden okumalıdır.**
    - **Sağlayıcının `email_verified` beyanına varsayılan olarak güvenilmez** (`TrustEmailVerified`, iki bölümde
      ayrı ayrı; varsayılan kapalı). Kapalıyken dış kimliğin hesabı her zaman adressiz ve doğrulanmamış açılır;
      personel rolü alamaz. Herkesin hesap açabildiği veya adresi doğrulamadan beyan eden bir sağlayıcıda bu beyan,
      personel adresini yazan herkese rol verirdi (ilk yazımda beyana her zaman güveniliyordu; otomatik incelemede
      çıktı). Ayar yalnızca adresleri gerçekten doğrulayan, kuruluşun kendi sağlayıcısı için açılır. Güven her istekte
      yeniden uygulanır: ayar sonradan kapatılırsa, daha önce doğrulanmış yazılmış hesap da rolünü kaybeder; `/auth/me`
      de dış hesap için kayıttaki değeri değil, isteğin taşıdığı doğrulama ve rol bilgisini döndürür. İki
      bölüm aynı sağlayıcıyı gösteriyorsa ayar ikisinde aynı olmalıdır (adresler yazılışlarıyla değil anlamlarıyla
      karşılaştırılır); değilse uygulama açılmaz. `RequireVerifiedEmail` açıkken adresine güvenilmeyen bir sağlayıcı
      da uygulamayı açtırmaz: hesapları adreslerini hiçbir yoldan doğrulayamazdı. Açılış denetiminin göremediği
      durum: sağlayıcıya güvenilir ama token doğrulanmış bir adres taşımaz (erişim token'ları çoğu zaman taşımaz);
      öyle bir hesap açılırken uyarı günlüğe yazılır (hesap başına bir kez; testi vardır) ve hesap, adres gelene
      kadar kimlik uçlarının dışında kalır.
    - İstemciler neyin sunulduğunu `GET /auth/config` ucundan okur (gizli bir şey taşımaz).
    - Oturum denetimi (`sid`) yalnızca kendi token'larımız içindir; sağlayıcının token'ının ömrünü ve iptalini
      sağlayıcı yönetir. `RequireVerifiedEmail` her yoldan giriş için geçerlidir.
    - Testler iki katmandır. Kurallar, sağlayıcıyı testin içinde kuran testlerle sınanır (`ExternalSignInTests`:
      keşif belgesi, imza anahtarı, token ucu; `BackchannelHttpHandler`); token'lar gerçekten imzalanır ve
      Microsoft'un doğrulayıcılarından geçer. Gerçek bir sağlayıcıyla birlikte çalışma, kapsayıcıda çalışan
      Keycloak'a karşı sınanır (`KeycloakSignInTests`; imaj özetiyle sabit, `HALDUS_TEST_KEYCLOAK_IMAGE`):
      sağlayıcının verdiği ve yenilediği erişim token'ının kabulü, başka bir API için verilmiş token'ın reddi ve
      Keycloak'ın kendi giriş sayfasından geçen yönlendirmeli giriş (ASP.NET Core burada PAR da kullanır). JWT
      kabulü ikinci bir gerçek sağlayıcıya, kapsayıcıdaki Ory Hydra'ya karşı da sınanır (`HydraSignInTests`;
      `HALDUS_TEST_HYDRA_IMAGE`): bir makine istemcisinin (client credentials) token'ı hesaba ve kiracıya çevrilir,
      başka bir API'yi adlandıran veya hiçbirini adlandırmayan token reddedilir, "her yerden çık" token'ın `iat`
      alanıyla çalışır. Hydra kendi adını açılışta bilmelidir; bağlantı noktası bu yüzden kapsayıcıdan önce seçilir.
      Barındırılan sağlayıcılara (Entra ID, Auth0...) karşı sınanmadı: hesap ister. Sınırlar: tek sağlayıcı; sağlayıcıdaki adres sonradan değişirse hesaba
      yansımaz; sağlayıcıdan çıkış (tek oturum kapatma) yoktur; dış hesabın parolası yoktur ve parola uçları onu
      kabul etmez; erişim token'ında `email` / `email_verified` yoksa hesap adressiz açılır (sonradan gelirse yazılır).
    - Çıkış ve token'ın türü (incelemede sınır olarak not edilmiş, sonra düzeltilmiştir):
      - `/auth/logout-all` sağlayıcının o ana kadar verdiği **erişim** token'larını da geçersiz kılar: hesap o anı
        taşır (`ShopAccount.SignedOutEverywhereAt`) ve o andan önce verilmiş token (`iat`) reddedilir. `iat`
        taşımayan token ilk istekten reddedilir (yoksa hesap çalışır görünür ve ilk "her yerden çık"tan sonra
        kalıcı olarak kilitlenirdi; uyumsuz sağlayıcı kurulurken fark edilir). `nbf` onun yerine geçmez: sabit veya
        geriye çekilmiş bir `nbf` aynı kilitlenmeyi getirirdi (ikinci incelemede çıktı). Beş dakikadan daha ileride
        verilmiş görünen token da reddedilir (doğrulayıcı `iat`'e bakmaz; öyle bir token her sınırın üstünde
        kalırdı). Sınır en az, isteği yapan token'ın verildiği andır (saati ileride bir sağlayıcının o token'ı da
        ölür) ve geri alınmaz. Sağlayıcıda yeniden alınan token geçer. **Bu, sağlayıcıdaki oturumu kapatmaz:**
        yenileme token'ı tutan başka bir cihaz yeni bir erişim token'ı alıp geri gelir; onu ancak sağlayıcı iptal
        eder. Saat farkının bilinen etkisi iki yönlüdür ve farkla sınırlıdır: sağlayıcının saati gerideyse, çıkıştan
        sonra o fark kadar süre içinde aldığı token'lar da reddedilir (kullanıcı o süre dolana kadar giremez);
        ilerideyse, çıkıştan hemen önce o fark kadar süre içinde verdiği başka token'lar sınırın üstünde kalabilir.
      - `Audience` bir istemcinin kimliği olamaz (uygulama açılmaz; sondaki eğik çizgi sayılmaz, öteki bölümün
        istemcisi de sayılır): kimlik token'ının
        audience'ı istemcinin kimliğidir ve öyle bir ayarda kimlik token'ı da erişim token'ı sayılırdı. Token'ın
        türü (`typ: at+jwt`) ayrıca istenmez: Keycloak erişim token'ını `JWT` türüyle verir.
      - Çerez oturumunun ömrü ayardır (`Shop:Auth:Oidc:SessionMinutes`, varsayılan 480). Sağlayıcıdaki oturum
        kapandığında backend'e haber gelmez: sağlayıcının ön kanal çıkışı `SameSite=Lax` çerezle çalışmaz, arka
        kanal çıkışı (imzalı çıkış token'ının doğrulanması) yazılmadı. Çerez en geç bu süre sonunda biter; süre
        kısaltılarak daraltılır. Oturumu kapanmış bir çerez sunulduğunda tarayıcıdan sildirilir (silme ada göredir:
        aynı anda başka bir sekmede yeni bir giriş tamamlanırsa o yeni çerez de silinebilir ve yeniden girilir).
        `Audience`'ın öteki bölümün istemci kimliğiyle karşılaştırılması iki ayrı sağlayıcıda da uygulamayı durdurur
        (kapalı tarafa düşer; mesaj iki ayarın adını da söyler).
      - Örnekte migration yoktur: `ShopAccount`'a eklenen kolonlar (`ExternalIssuer`, `SignedOutEverywhereAt`...)
        `Shop:CreateDatabase` ile daha önce oluşturulmuş bir veritabanına eklenmez. Öyle bir veritabanı sessizce
        kullanılmaz: `DemoDatabase.EnsureAsync` veritabanına, onu kuran şemanın parmak izini yazar
        (`ShopDemoSchema`; modelin oluşturma betiğinin özeti) ve şema değiştiyse ya da işaret yoksa uygulama,
        veritabanının silinmesi gerektiğini söyleyerek açılmaz. Veri kendiliğinden silinmez. (İlk yazımda eski
        veritabanı çalışma anında bulunamayan kolonlarla, 500 vererek bozuluyordu.) Testlerin veritabanı
        `EnsureCreated` ile doğrudan kurulur ve bu denetimden geçmez; **modeli değiştiren bir iş bu yüzden deneme
        veritabanını geçersiz kılar ve bunu söylemek gerekir.**
    - **İstemciler** (kullanıcının onayladığı paketlerle; panelde `oidc-client-ts`, mobilde `expo-auth-session`).
      İkisi de sağlayıcıyı `/auth/config` yanıtından öğrenir ve Authorization Code + PKCE akışını kendisi yapar;
      backend'e sağlayıcının erişim token'ını sunar ve hesabı `/auth/me` ile sorar. Oturum kaydı türünü taşır
      (`kind`: parola veya sağlayıcı; tür yazılmamış eski kayıt parola oturumudur). Sağlayıcı oturumu 401 üzerine
      sağlayıcının token ucundan, yenileme token'ı ile yenilenir ve parola oturumuyla **aynı** yenileme
      hattından geçer (`api-fetch`: tek yenileme, sekmeler arası kilit, başka hesabın oturumuna yazmama, yalnızca
      kesin rette çıkış; sağlayıcı için kesin ret RFC 6749'un ret kodlarıdır, ağ hatası ve 5xx oturumu korur).
      Sağlayıcı yenilediği halde backend yeni token'ı da reddederse sağlayıcı oturumu kapatılır (yoksa her istek
      sağlayıcıda bir yenileme token'ı harcardı; incelemede çıktı).
      Çıkış yalnızca yereldir. Parola değiştirme ve e-posta doğrulama sağlayıcı hesabında gizlenir.
      - Panel: dönüş `/login/callback` rotasındadır. Token'lar yalnızca panelin kendi oturum kaydında durur;
        kitaplığın kullanıcı deposu bellektedir ve her işlemden sonra boşaltılır (tek kaynak: sekmeler arası
        eşitleme iki tür için aynı koddur), `state` ve PKCE doğrulayıcısı `sessionStorage`'dadır, kitaplığın
        zamanlayıcıyla ve iframe ile yenilemesi kapalıdır (`src/lib/provider.ts`).
      - Mobil: dönüş adresi `haldus-shop://sign-in`'dir (Expo Go'da `exp://…/--/sign-in`, web'de `/sign-in`);
        sağlayıcının token ucu oturumla birlikte saklanır (`lib/provider.ts`, `lib/provider-sign-in.ts`).
        Sağlayıcının adresi HTTPS olmalıdır (düz HTTP yalnızca cihazın kendisinde, `localhost`, ve yalnızca
        geliştirme derlemesinde emülatörün `10.0.2.2` adresinde; kullanıcı bilgisi taşıyan adres,
        `http://localhost:1@başka.sunucu`, hiç kabul edilmez; keşif belgesinin söylediği yetkilendirme ve token
        uçları da aynı kuraldan geçer, saklı token ucu her yenilemede yeniden denetlenir) ve
        keşif belgesinin `issuer` alanı istenen adresle aynı olmalıdır.
      - Sınananlar: iki projede birim testleri ve sahte bir sağlayıcıya karşı etkileşim testleri (mobilde web
        hedefinde, açılır pencere dahil). Panel ayrıca gerçek backend ve kapsayıcıdaki Keycloak ile tarayıcıda elle
        denendi: Keycloak'ın giriş sayfasından giriş, doğrulanmış personel adresiyle ortak kaynağa yazma, imzası
        bozulan erişim token'ının sağlayıcıdan yenilenmesi, çıkış, sağlayıcı açıkken parola ile kayıt. İncelemelerin
        düzeltmelerinden (`0d7eade`) sonra tur yinelendi: giriş, personel adresiyle ürün oluşturma, "her yerden
        çık"tan sonra eski token'ın 401 alması ve panelin token'ı Keycloak'tan yenileyip sürmesi, çıkış, parola ile
        kayıt. (Keycloak'ın erişim token'ı `iat` taşır; türü `JWT`'dir.) Adres kuralı değiştikten (`3e0767c`) sonra
        bir tur daha: sağlayıcıyla giren personel ürün oluşturdu; aynı adresle açılıp kodla doğrulanan parola hesabı
        oluşturamadı (403 pencerede göründü).
        Mobilde giriş Android emülatöründe (Android 17, Expo Go 56; Metro, backend ve Keycloak `adb reverse` ile)
        gerçek backend ve Keycloak'a karşı tamamlandı (`af0ce80`): düğme sistem tarayıcısını açtı; sağlayıcının
        dönüş bağlantısı (`exp://127.0.0.1:8081/--/sign-in?code=…&state=…`) uygulamaya ulaşınca uygulama kodu PKCE
        doğrulayıcısıyla Keycloak'ın token ucunda token'a çevirdi, hesabı `/auth/me` ile sordu ve ürünler ekranını
        açtı; hesap ekranı sağlayıcının adresini gösterdi ve parola / e-posta doğrulama bölümlerini göstermedi;
        uygulama kapatılıp açılınca oturum güvenli depodan geri geldi; erişim token'ının süresi dolunca üç istek 401
        aldı, uygulama token'ı Keycloak'tan bir kez yeniledi ve üçü de 200 ile yinelendi; çıkıştan sonra yeniden
        başlatınca oturum geri gelmedi. **Tarayıcıdaki adım cihazda yapılmadı:** emülatördeki Chrome ilk açılış
        ekranındadır ve devam etmek Google'ın hizmet şartlarını kabul etmek demektir (kullanıcı adına yapılmaz;
        kurulu iki sistem imajı da Play Store imajıdır ve cihazda `https` bağlantısı açabilen tek uygulama da tek
        Custom Tabs sağlayıcısı da Chrome'dur: `cmd package query-activities` / `query-services` ile bakıldı).
        Onun yerine uygulamanın hazırladığı yetkilendirme adresi
        cihaz günlüğünden okundu (geçici, commit'lenmeyen bir `console.log`), Keycloak'ın giriş formu o adresle
        bilgisayardan dolduruldu ve Keycloak'ın döndürdüğü bağlantı `adb shell am start` ile uygulamaya iletildi.
        Yani cihazda sınanmayan tek şey, Keycloak'ın sayfasının Chrome'da çizilmesi ve Chrome'un `exp://`
        bağlantısını uygulamaya kendisinin vermesidir. İki tuzak: `expo start --localhost` Metro'yu yalnızca IPv6
        döngü adresinde dinletir ve `adb reverse` ona ulaşamaz (Expo Go "Failed to download remote update" der);
        `REACT_NATIVE_PACKAGER_HOSTNAME=127.0.0.1` ile başlatılır. Emülatörde kullanıcının başka bir projesinin
        uygulaması da kuruludur ve öne gelebilir; ona dokunulmaz, Expo Go bileşen adıyla açılır
        (`-n host.exp.exponent/.LauncherActivity`).
      - Sınırlar: sağlayıcının oturumu kapatılmaz ve token'ı iptal edilmez (çıkış yereldir); kimlik token'ı
        (`id_token`) kullanılmaz; yalnızca kapsam gönderilir (`audience` / `resource` parametresi isteyen
        sağlayıcılar yapılandırılamaz); panelde `serverSignIn` için düğme yoktur; panel paketi `oidc-client-ts`'i
        sağlayıcı ayarlanmamışken de yükler.
  - `Shop:DemoIdentity` açıksa kimlik, doğrulanmadan, başlıklardan okunur (`X-User-Id`, `X-Tenant-Id`,
    `X-User-Roles`). Varsayılan olarak kapalıdır ve **yalnızca geliştirme ortamında açılabilir**: başka ortamda
    uygulama açılmaz. Entegrasyon testleri açar (`ShopFixture`), kimlik doğrulama testleri kapalı haliyle ikinci
    bir uygulama açar (`CreateAuthenticatedAppClient`); yük ölçümü de bu kipi kullanır.
  - `UseSessionValidation`: reddedilen bir token sunan istek her kaynakta 401 alır (`AllowAnonymous` uçlar
    hariç). İlk yazımda yoktu; panel tarayıcıda denenirken çıktı: geçersiz token'lı istek anonim sayılıyor,
    kiracıya ait kaynakta 403 alıyor ve istemci oturumu yenilemiyordu. Aynı ara katman token'ın oturumunu her
    istekte veritabanına sorar (aşağıda). Giriş istemeyen uçlar reddedilen token yüzünden kapanmaz, ama iptal
    edilmiş bir oturumun token'ı onlarda da kimlik sayılmaz: istek kimliksiz sürer (`context.User` boşaltılır).
    İlk yazımda bu uçlarda oturum hiç sorulmuyordu ve çıkış yapılmış bir oturumun token'ı, süresi dolana kadar
    kullanıcıyı ve kiracıyı tanıtmaya devam ediyordu (bağımsız incelemede çıktı). **Giriş istemeyen ama kimliğe
    bakan bir uç yazılırsa bu kurala güvenir; ara katmanın o dalı kaldırılmamalıdır.**
  - **Giriş yapılmadan hiçbir uca ulaşılmaz** (`FallbackPolicy`; demo kimliği kipinde konmaz). Açık kalanlar bunu
    kendileri söyler: giriş/kayıt/yenileme/sıfırlama uçları, Haldus'un token ile erişilen ve süreli bağlantı
    uçları, misafir sepeti (`guests.AllowAnonymous()`), API şeması. İlk yazımda yoktu: kiracıya ait olmayan
    kaynaklar (ürünler, kategoriler) token'sız yazılabiliyordu (bağımsız güvenlik incelemesinde çıktı). **Örnek
    mağazaya herkese açık olması gereken yeni bir uç eklenirse `AllowAnonymous` ile işaretlenmelidir.**
  - Oturumlar sunucudadır (`ShopSession`; token'daki `sid` claim'i) ve en çok 30 gün yaşar. Çıkış (`/auth/logout`,
    `/auth/logout-all`), parola sıfırlama ve parola değişimi oturumu iptal eder; iptal edilen oturumun erişim
    token'ı da yenileme token'ı da hemen reddedilir. Bedeli, token'lı her istekte bir birincil anahtar sorgusudur.
    - Çıkış oturumsuz da çağrılır ve oturumu yenileme token'ı da tanıtır (gövdede, isteğe bağlı; sırası geçmiş bir
      token da yeter). İlk yazımda geçerli bir erişim token'ı istiyordu: token'ı çalan kişi sahibinden önce
      yenilediyse sahibin yenilemesi 409 alıyor, çıkışı da 401 alıyor ve oturum sunucuda açık kalıyordu
      (incelemede çıktı). İstemciler çıkarken yenileme token'ını her zaman gönderir.
    - Parola değişimi hesabın **bütün** oturumlarını kapatır, isteği yapanınkini de, ve isteği yapana yeni bir
      oturum açar (yanıt yeni token'lardır). İlk yazımda isteği yapanın oturumu açık kalıyordu; token'lar
      kopyalanmışsa kopyalanan da tam o oturumdu (incelemede çıktı).
  - Yenileme token'ı bir kez kullanılır (`RefreshVersion`; token'daki `rv` claim'i, tek bir koşullu `UPDATE` ile
    ilerler). Eskimiş bir yenileme token'ı yeniden sunulursa oturum çalınmış sayılır ve kapatılır; yenilemeden
    hemen sonra (`RefreshGraceSeconds`, 30 sn) bir önceki token'ı sunan istek oturumu kapatmaz, ama yeni token da
    alamaz: aynı anda yenileyen ikinci sekme oturumu düşürmez, eskimiş bir token ise hiçbir zaman geçerli bir oturuma
    çevrilmez. (Toleranssız ilk hali ikinci incelemede, toleransta yeni token veren ikinci hali otomatik incelemede
    çıktı.) Tolerans içindeki yanıt ayrıdır (409): istemciler onu ret saymaz, oturumu kapatmaz ve yeni token'ları
    yenilemeyi yapan istekten (öteki sekmenin depoya yazmasından) kısa bir süre bekler. Web paneli ayrıca yenilemeyi
    sekmeler arasında sıraya sokar (Web Locks; yalnızca güvenli bağlamda vardır) ve oturumu `storage` olayıyla
    eşitler. Tolerans yalnızca bir önceki sırayı kapsar; e-posta doğrulaması yenileme sırasını ilerletmez (ilerletseydi
    onunla aynı anda gelen sıradan bir yenileme yeniden kullanım sayılırdı; toleransı iki sıraya genişleten ara
    çözüm tespiti zayıflattığı için geri alındı). 409 yanıtı, eskimiş token'ı sunana oturumun hâlâ açık olduğunu
    söyler; istemcilerin oturumu yanlışlıkla kapatmaması için bilerek kabul edildi. Bilinen ödünleşimler:
    yanıtı yolda kaybolan istemci tolerans bitince yeniden giriş yapar; çalan kişi asıl sahibinden önce yenilerse ve
    sahibin eskimiş token'ı tolerans içinde gelirse hırsızlık o an fark edilmez (sahip oturumdan düşer, oturum
    kapanmaz).
  - Parola sıfırlama ve e-posta doğrulama, e-postayla gönderilen kodladır (bağlantı değil: mobilde derin bağlantı,
    web'de adreste token gerekmez). Kod sekiz karakterdir (Crockford base32, 40 bit); özeti saklanır, 15 dakika
    yaşar ve bir kez kullanılır; yazılışı normalleştirilir (küçük harf, tire, O/0, I/L/1). **Hesap başına deneme
    sınırı bilerek yoktur.** İki önceki hali de kusurluydu: altı haneli kod + kod başına beş deneme "kod iste, beş
    kez dene" döngüsüyle bulunabiliyordu; pencereye bağlanan deneme sınırı ise başkasının yanlış kodlar göndererek
    hesabın parola sıfırlamasını engellemesine izin veriyordu (ikisi de incelemelerde çıktı). Uzun kod ikisini de
    kapatır: denenerek bulunamaz, kilitlenecek bir sayaç da yoktur. Sınırlı olan gönderimdir (hesap + amaç başına
    saatte üç kod; adrese e-posta yağdırılmasın diye). Sıfırlama e-postası yanıt beklenmeden, 30 saniyelik bir süre
    sınırıyla gönderilir (süre, adresin kayıtlı olduğunu ele vermesin diye).
    `Shop:Auth:RequireVerifiedEmail` (varsayılan kapalı) doğrulanmamış hesabı kimlik uçlarının dışında tutar.
    Zaten doğrulanmış hesapta doğrulama ucu token vermez (204).
  - Hesap kilidi: art arda beş başarısız parola denemesinden sonra 15 dakika (`Shop:Auth`). Deneme, parola
    sınanmadan önce tek bir `UPDATE` ile sayılır (eşzamanlı denemeler sınırı aşamaz); kilitli hesabın yanıtı yanlış
    paroladakiyle aynıdır. Parola değiştirirken sorulan geçerli parola da aynı sayaca yazılır. Giriş, oturumu
    yazdıktan sonra "parola bu arada değişmedi" koşuluyla tamamlanır; değiştiyse oturum geri alınır.
  - Parola ve kod deneyen uçlar ayrıca adres başına dakikada 30 istekle sınırlıdır (ASP.NET Core'un yerleşik hız
    sınırlayıcısı); IPv6'da adres değil /64 bloğu sayılır. Bir vekilin arkasında vekil tanıtılmalıdır
    (`Shop:KnownProxies`), yoksa herkes tek bir sınırı paylaşır. Kayıt, adresin alınmış olduğunu söyler (409;
    bilinçli seçim).
  - E-posta `IAccountMailer` ile gönderilir: `Shop:Smtp:Host` verildiyse SMTP (`System.Net.Mail`), verilmediyse
    gönderilmez. Yalnızca geliştirme ortamında kod günlüğe yazılır (arayüzler posta sunucusu olmadan denensin
    diye); başka ortamda kod hiçbir yere yazılmaz.
  - İstemciler token'ı `Authorization` başlığında taşır, 401'de bir kez yeniler (eşzamanlı istekler tek yenilemeyi
    bekler) ve giriş/kayıt/yenileme uçlarına token eklemez. Web'de token'lar `localStorage` içindedir (XSS'e açık;
    örnek için kabul edilmiş ödünleşim), mobilde güvenli depoda, her biri ayrı anahtarda.
  - **Ortak kaynakları yalnızca personel yazar** (`SharedWrites`; varsayılan yetki kuralının parçası). Kayıt herkese
    açıktır; kimseye ait olmayan kaynakları (ürünler, kategoriler, özellik bayrakları…) herkes okur, ama yazmak
    `staff` rolü ister. Personel, `Shop:Auth:Staff` ayarında e-postası sayılan **ve e-postasını doğrulamış**
    hesaplardır (liste boşken ortak kaynakları kimse yazamaz). Doğrulama şartı ilk yazımda yoktu: adres kullanıcının
    yazdığı bir metin olduğu için, personel adresiyle ilk kaydolan kişi rolü alırdı (otomatik incelemede çıktı). Bir kiracıya veya kullanıcıya ait kaynaklar kuralın dışındadır. Hangi kaynağın ortak
    olduğu elle sayılmaz: üretilen uçların taşıdığı `HaldusResourceScope` metadatasından okunur, böylece sonradan
    eklenen ortak bir kaynak kendiliğinden korunur. **Kural kapalı başlar:** yazan bir uç ancak kapsamı açıkça bir
    kiracı veya kullanıcıysa serbesttir; kapsamını bildirmeyen uçlar (entity'ye bağlı olmayan `MapAction`, elle
    yazılmış uçlar) ortak sayılır ve personel ister. Okuma, güvenli HTTP metotları ve üreticinin `HaldusReadOnly` ile
    işaretlediği `POST` uçlarıdır (arama, toplu okuma, önizleme); adresin sonuna bakılmaz. Kendi yetki kuralını
    bildiren uca (`.RequireAuthorization()`) varsayılan kural uygulanmaz. İlk yazımda rol yoktu ve kayıt olan
    herkes ortak kaynakları yazabiliyordu (incelemede çıktı); ikinci yazımda kural metadata yokken açık kalıyordu
    (bütün ürünlerin fiyatını değiştiren `/apply-discount` her hesaba açıktı) ve okumayı adresin sonundan tanıyordu
    (ikisi de otomatik incelemede çıktı).
  - Bilinen sınırlar: rol tektir ve yapılandırmadan gelir (rol yönetimi uçları yoktur); rol token'a yazıldığı için
    listeden çıkarılan hesap, erişim token'ı yenilenene kadar personel kalır. İki adımlı doğrulama, e-posta değiştirme ve hesap silme
    yoktur; hesap kilidi bir hesabın başkası tarafından kilitlenmesine izin verir (parola sıfırlama kilidi açar);
    iptal edilmiş oturum ve kullanılmış kod kayıtları kendiliğinden temizlenmez; süreç kapanırken yolda olan
    sıfırlama e-postası gitmez. Token'lar Data Protection anahtarlarına bağlıdır; anahtarların kalıcılığı ve
    paylaşımı uygulamanın işidir.
