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
bu deneme için; gerçek bir uygulama şemasını migration'larla yönetir). PowerShell'de:

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
`SHOP_API_URL` ortam değişkeni). Giriş ekranında parola yoktur: örnek backend kullanıcıyı ve kiracıyı istek
başlıklarından okur (`DemoIdentity`), bu yüzden istediğiniz adı yazarsınız. Bu, örneğe özgüdür ve güvenli değildir.

## Komutlar

| Komut | Ne yapar |
|---|---|
| `pnpm dev` | Geliştirme sunucusu |
| `pnpm generate:api` | `src/api` altındaki istemciyi backend'in `openapi.json` dosyasından yeniden üretir |
| `pnpm typecheck` | Tip denetimi |
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
- Her istek `src/lib/api-fetch.ts` içindeki sarmalayıcıdan geçer: kimlik başlıkları, dil ve hata davranışı oradadır.
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
- **Test:** test altyapısı kurulmadı.
