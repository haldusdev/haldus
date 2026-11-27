# Örnek mağaza

Haldus'un bildirimlerinin hemen hepsinin gerçek bir alanda kullanıldığı büyük örnek: katalog, sipariş, stok, tedarik,
içerik, etkileşim ve platform kaynakları; 80'i aşkın `Map*` çağrısı. Entegrasyon testleri (`tests/Haldus.IntegrationTests`)
bu uygulamaya karşı çalışır; yönetim paneli, mobil uygulama ve TypeScript istemcisi de bu uygulamanın şemasını tüketir.

Haldus'u öğrenmek için önce [eğitime](../../docs/getting-started/tutorial.md) ve onun küçük örneğine
([Haldus.Sample.Tracker](../Haldus.Sample.Tracker)) bakın. Bu uygulama bir bildirimin gerçek bir kullanımını aramak için
iyi bir yerdir.

## Düzen

| Klasör | Kaynaklar |
|---|---|
| `Features/Catalog` | Ürünler, markalar, kategori ağacı, etiketler, ürün yorumları; fiyatlama aksiyonu, içe ve dışa aktarma işleri. |
| `Features/Sales` | Siparişler ve satırları, sepetler, kuponlar, müşteriler ve adresleri, ödemeler, iadeler, makbuzlar, teslimatlar, teklifler, rezervasyonlar; rapor işi. |
| `Features/Stock` | Stok hareketleri, stok seviyeleri, envanter ve izin bakiyeleri. |
| `Features/Supply` | Tedarikçiler (kişileri, fiyatları, şartları, sertifikaları), satın alma ve bütçe talepleri, gider talepleri, sözleşmeler, makineler. |
| `Features/Content` | Makaleler ve bölümleri, dosyalar ve ekler, yorumlar, etiket listesi. |
| `Features/Account` | Kullanıcıya ait kaynaklar: sepetler ve misafir sepeti, favoriler, alışveriş listeleri, ürün notları. |
| `Features/Engagement` | Duyurular, bildirimler, tartışmalar ve yanıtları, faturalar ve ödemeleri, toplantılar, biletler, kayıtlar, kişiler; tip, rol ve alt tip örnekleri (cihaz modelleri, personel, araçlar), giysi varyantları. |
| `Features/Platform` | Mağaza ayarları ve bakım duyurusu (tek kayıt), özellik bayrakları ve tercihler, ülkeler, planlar; sayım işi, bir sorgu ve bir aksiyon örneği. |
| `Features/Integrations` | API anahtarları, webhook uçları, davetler; imzalama aksiyonu. |
| `Features/Auth` | Örneğin kendi kimlik doğrulaması: hesaplar, oturumlar, parola sıfırlama, e-posta doğrulama, JWT ve OpenID Connect sağlayıcıları. Haldus'un parçası değildir. |

Bir bildirimin gerçek kullanımını bulmak için adını bu klasörde arayın (ör. `.Approvable(`).

Üretilen kod derlemeden sonra `obj/Generated` altındadır. `openapi.json` her derlemede yeniden üretilir ve commit'lenir.

## Çalıştırmak

PostgreSQL'i başlatın (parolayı kendiniz seçin):

```bash
docker run -d --name haldus-shop-demo -e POSTGRES_PASSWORD=<parola> -e POSTGRES_DB=shop -p 127.0.0.1:5432:5432 postgres:17-alpine
```

Bu örnek bilerek migration taşımaz; tabloları açılışta oluşturan bir deneme ayarı vardır. PowerShell'de:

```powershell
$env:ConnectionStrings__Default = "Host=127.0.0.1;Port=5432;Database=shop;Username=postgres;Password=<parola>"
$env:Shop__CreateDatabase = "true"
dotnet run --project samples/Haldus.Sample.Shop --urls http://localhost:5080
```

Örnek güncellendikten sonra eski deneme veritabanıyla açılmaz ve bunu açılışta söyler; kapsayıcıyı silip yeniden
oluşturun. Giriş yapmadan hiçbir uca ulaşılmaz: önce `POST /auth/register` ile bir hesap açın (her hesap kendi
kiracısıyla doğar) ve dönen token'ı `Authorization: Bearer` başlığında gönderin. Ortak kaynakları (ürünler, kategoriler)
yalnızca personel yazar; bkz. `Shop:Auth:Staff`.

## Ayarlar

| Ayar | Varsayılan | Etkisi |
|---|---|---|
| `ConnectionStrings:Default` | yok | PostgreSQL bağlantı dizesi. |
| `Shop:CreateDatabase` | `false` | Tabloları açılışta oluşturur. Yalnızca deneme içindir. |
| `Shop:CorsOrigins` | boş | Tarayıcıdan gelen isteklere izin verilen kaynaklar (mobil uygulamanın web hedefi gibi). |
| `Shop:DemoIdentity` | `false` | Kimliği doğrulamadan başlıklardan okur (`X-User-Id`, `X-Tenant-Id`, `X-User-Roles`). Yalnızca geliştirme ortamında açılabilir; testler ve yük ölçümü kullanır. |
| `Shop:Auth:Staff` | boş | Ortak kaynakları yazabilen personelin e-postaları (e-posta doğrulandıktan sonra). |
| `Shop:Auth:RequireVerifiedEmail` | `false` | Doğrulanmamış hesap yalnızca kimlik uçlarını kullanabilir. |
| `Shop:Auth:Jwt` | kapalı | Bir kimlik sağlayıcının JWT erişim token'larını kabul eder (`Authority`, `Audience`, `ClientId`). |
| `Shop:Auth:Oidc` | kapalı | Backend'in yönettiği OpenID Connect girişi ve çerez oturumu (`Authority`, `ClientId`, `ClientSecret`). |
| `Shop:Smtp:Host` | yok | Doğrulama ve sıfırlama kodlarının gönderildiği SMTP sunucusu. Yoksa e-posta gönderilmez; geliştirme ortamında kod günlüğe yazılır. |
| `Shop:KnownProxies` | boş | Önündeki vekillerin adresleri (`X-Forwarded-*` başlıkları ve hız sınırı için). |

Kimlik doğrulamanın tasarımı ve sınırları depo kökündeki `.claude/rules/sample-shop-auth.md` dosyasında ayrıntılıdır.

## Bu uygulamayı tüketenler

- [TypeScript istemcisi](../Haldus.Sample.Shop.Client): şemadan orval ile üretilir.
- [Yönetim paneli](../Haldus.Sample.Shop.Web): Vite + React.
- [Mobil uygulama](../Haldus.Sample.Shop.Mobile): Expo.
- [Yük ölçümü](../../tools/Haldus.LoadTest): kilide dayanan özelliklerin yük altındaki davranışı.
