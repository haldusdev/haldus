# Frontend ve mobil istemciler

Haldus bir backend framework'üdür; frontend ve mobil için kod üretmez. İstemciye verdiği şey sözleşmedir: uygulamanın
OpenAPI belgesi. Bu belge her derlemede bir dosyaya (`openapi.json`) yazılır ve commit'lenir; istemcinin tipleri ve
istek kodu o dosyadan üretilir. Depodaki örnekler bunu [orval](https://orval.dev) ile ve
[TanStack Query](https://tanstack.com/query) hook'ları üreterek yapar. Bu sayfa şemanın nasıl üretildiğini, istemcinin
nasıl üretildiğini ve Haldus'a özgü birkaç ipucunu anlatır.

Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. `var builder = WebApplication.CreateBuilder(args);` ve
`var app = builder.Build();` satırları yoksa yazılmış sayılır.

## İçindekiler

- [Şemayı derlemede üretmek](#şemayı-derlemede-üretmek)
- [Şema üretilirken çalışan başlangıç kodu](#şema-üretilirken-çalışan-başlangıç-kodu)
- [orval ile istemci üretmek](#orval-ile-istemci-üretmek)
- [Örnek arayüzler](#örnek-arayüzler)
- [Öneriler](#öneriler)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Şemayı derlemede üretmek

Şemayı ASP.NET Core'un kendi paketi üretir: `Microsoft.AspNetCore.OpenApi`. Derleme sırasında dosyaya yazmak için
`Microsoft.Extensions.ApiDescription.Server` paketi eklenir. Örnek mağazanın proje dosyasındaki ayarlar:

```xml
<PropertyGroup>
  <!-- openapi.json build sırasında proje dizinine üretilir ve commit'lenir. -->
  <OpenApiGenerateDocuments>true</OpenApiGenerateDocuments>
  <OpenApiDocumentsDirectory>$(MSBuildProjectDirectory)</OpenApiDocumentsDirectory>
  <OpenApiGenerateDocumentsOptions>--file-name openapi</OpenApiGenerateDocumentsOptions>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
  <PackageReference Include="Microsoft.Extensions.ApiDescription.Server" PrivateAssets="all" />
</ItemGroup>
```

| Ayar | Ne yapar |
|---|---|
| `OpenApiDocumentsDirectory` | Dosyanın yazılacağı klasör. Varsayılan `obj/` altındadır; proje dizini, dosyanın commit'lenebilmesi içindir. |
| `OpenApiGenerateDocumentsOptions` | `--file-name openapi`: dosyanın adı `openapi.json` olur (verilmezse adı projenin adından gelir). |

Uygulamada belgeyi kaydeden satır `AddOpenApi()`'dir. `MapOpenApi()` aynı belgeyi çalışma anında
(`/openapi/v1.json`) sunar; dosyayı üretmek için gerekmez.

<!-- example: openapi -->
```csharp
using System.Text.Json.Serialization;

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddOpenApi();

// Sayılar yalnızca sayı olarak okunur; şemadaki sayı alanları "integer | string" değil "integer" olur.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

var app = builder.Build();

app.UseHaldus();
app.MapOpenApi();
app.MapCrud<Product>();

app.Run();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public int Stock { get; set; }
}
```

Haldus'un ürettiği uçlar şemaya her Minimal API ucu gibi girer. Haldus'a özgü olanlar:

- İşlemlerin adları (`operationId`) kaynağın ve işlemin adından gelir: `ListProducts`, `GetProduct`, `CreateProduct`,
  `PatchProduct`, `DeleteProduct`, `SearchProducts`; geçişler ve aksiyonlar kendi adlarıyla (`CancelOrder`,
  `PlaceOrder`). Üretilen hook adları bu yüzden öngörülebilirdir (`useListProducts`, `useCancelOrder`).
- Her kaynağın uçları kaynağın çoğul adıyla etiketlenir (`Products`, `Orders`). orval etiket başına bir klasör üretir
  ve istemciyi etiketlere göre süzebilir.
- İstek ve yanıt tipleri üretilen DTO'lardır (`ProductResponse`, `CreateProductRequest`, `PatchProductRequest`...).
  `PATCH` gövdesi şemada sıradan bir nesnedir; "gönderilmedi" ile "`null` gönderildi" ayrımı için bir sarmalayıcı tip
  görünmez.
- Durum alanları (`Status`) adlarıyla yazılır ve şemada bir `enum` olur (`"Draft"`, `"Placed"`...).
- Hata yanıtları `ProblemDetails` / `HttpValidationProblemDetails`'tir.
- Haldus'un istek ve yanıtlarda görünen kendi tiplerinin (`ApprovalDecision`, `BalanceEntry`...) belge yorumları şemada
  `description` olur. Haldus'u yükselttiğinizde şema bu yüzden değişebilir; istemciyi yeniden üretin.

`openapi.json` dosyası her derlemede yeniden yazılır. Bir uç veya DTO değiştiğinde (zinciri değiştirdiğinizde de)
backend'i derleyin, istemciyi yeniden üretin ve ikisini birlikte commit'leyin.

## Şema üretilirken çalışan başlangıç kodu

Belge derleme sırasında, uygulamanın kendisi başlatılarak üretilir: `Program.cs` çalışır ve arka plan servisleri
başlar, ama istek kabul edilmez. Açılışta veritabanına veya dış bir servise bağlanan kod bu sırada da çalışır ve
derlemeyi bozar (o anda veritabanı yoktur). Böyle kodu `HaldusJobs.IsGeneratingOpenApiDocument` ile koruyun:

<!-- example: openapi-guard -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddOpenApi();

var app = builder.Build();

// Derlemedeki şema üretimi sırasında veritabanına bağlanılmaz.
if (!HaldusJobs.IsGeneratingOpenApiDocument)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseHaldus();
app.MapCrud<Product>();

app.Run();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

Haldus'un kendi arka plan servisleri (bakım, olay dağıtıcısı, iş kuyrukları) bu kontrolü zaten yapar; şema üretilirken
veritabanına bağlanmazlar. Kendi arka plan servisinizi yazarsanız aynı kontrolü ekleyin. Bağlantı dizesi derleme
sırasında gerekmez: `AddDbContext`'e verilen işlev ancak bir bağlam istendiğinde çalışır.

## orval ile istemci üretmek

orval şemayı dosyadan okur; çalışan bir sunucuya ihtiyacı yoktur. Backend ile istemci aynı depodaysa şemayı
kopyalamayın, göreli yolundan okuyun. Depodaki [üretilmiş istemcinin](../../samples/Haldus.Sample.Shop.Client)
yapılandırması:

```ts
// orval.config.ts
import { defineConfig } from 'orval';

export default defineConfig({
  shop: {
    input: '../Haldus.Sample.Shop/openapi.json',
    output: {
      // Her kaynak (OpenAPI etiketi) kendi klasörüne, tipler ortak bir klasöre yazılır.
      mode: 'tags-split',
      target: 'src/api',
      schemas: 'src/api/model',
      client: 'react-query',
      httpClient: 'fetch',
      clean: true,
    },
  },
});
```

```json
{
  "scripts": {
    "generate:api": "orval",
    "typecheck": "tsc --noEmit"
  }
}
```

`pnpm generate:api` istemciyi yeniden üretir; `pnpm typecheck` üretilen kodun derlendiğini doğrular. Bu klasör bir
uygulama değildir: örnek mağazanın bütün uçları için üretilmiş istemcidir ve şemanın orval'dan hatasız geçtiğini
kanıtlar. Üretilen dosyaları elle değiştirmeyin; bir şey değişecekse şemayı (yani backend'i) veya orval'ın
yapılandırmasını değiştirip yeniden üretin.

`fetch` istemcisinin her işlevi gövdeyi, durum kodunu ve başlıkları birlikte döndürür (`{ data, status, headers }`).
Dönüş tipi, şemada bildirilen her yanıtın birleşimidir (başarılı yanıt ve `400` / `404` gibi hata yanıtları);
`status` alanına bakarak daraltılır.

### Ortak ayarlar: adres, token, dil

Gerçek bir uygulamada her istek aynı şeyleri taşır: backend'in adresi, oturumun token'ı ve arayüzün dili. orval bunun
için bir sarmalayıcı (`mutator`) alır. Yönetim panelinin yapılandırması:

```ts
// samples/Haldus.Sample.Shop.Web/orval.config.ts
export default defineConfig({
  shop: {
    input: {
      target: '../Haldus.Sample.Shop/openapi.json',
      // Panel yalnızca kullandığı kaynakların istemcisini taşır.
      filters: { tags: ['Auth', 'Products', 'Categories', 'Orders'] },
    },
    output: {
      mode: 'tags-split',
      target: 'src/api',
      schemas: 'src/api/model',
      client: 'react-query',
      httpClient: 'fetch',
      clean: true,
      override: {
        // Her istek bu işlevden geçer: token, dil ve hata davranışı orada.
        mutator: { path: './src/lib/api-fetch.ts', name: 'apiFetch' },
        // Başarısız yanıt hata olarak fırlatılır.
        fetch: { forceSuccessResponse: true },
      },
    },
  },
});
```

Panelin `apiFetch` işlevi ([src/lib/api-fetch.ts](../../samples/Haldus.Sample.Shop.Web/src/lib/api-fetch.ts))
`Authorization` ve `Accept-Language` başlıklarını ekler, `401` yanıtında oturumu bir kez yenileyip isteği yineler ve
başarısız yanıtın `ProblemDetails` gövdesini fırlatır. `forceSuccessResponse` ile hook'ların verisi yalnızca başarılı
yanıttır; bir listeyi okuyan bileşen şöyle yazılır:

```tsx
const products = useListProducts({ page: 1, pageSize: 20 });
const items = products.data?.data.items ?? [];
```

Yeni bir kaynak kullanmaya başladığınızda etiketini `filters.tags` listesine eklersiniz.

## Örnek arayüzler

Depoda örnek mağazayı tüketen iki arayüz vardır. İkisi de kendi istemcisini aynı `openapi.json` dosyasından, yalnızca
kullandığı etiketler için üretir:

| Örnek | Ne ile yazıldı | Ne gösterir |
|---|---|---|
| [Yönetim paneli](../../samples/Haldus.Sample.Shop.Web) | Vite, React, shadcn/ui, Tailwind | Ürünler (liste, arama, sıralama, yazma), kategori ağacı, siparişlerin iş akışı (`If-Match`, `Idempotency-Key`). |
| [Mobil uygulama](../../samples/Haldus.Sample.Shop.Mobile) | Expo, Expo Router, React Native Reusables, NativeWind | Sayfa sayfa yüklenen ürün listesi, favoriler (`/me/favorites`), kullanıcının sepeti (`/me/baskets`). |

İkisi de doğrulamayı backend'e bırakır: alan hataları yanıttaki `errors` nesnesinden okunur ve alanın altında
gösterilir. Nasıl çalıştırılacakları kendi README dosyalarındadır; ikisi de çalışan bir backend ister.

## Öneriler

**Sayıları katı okuyun.** ASP.NET Core'un varsayılan JSON ayarı metin içindeki sayıyı da (`"5"`) kabul eder ve bu
şemaya `integer | string` olarak yansır; üretilen tipler o zaman her sayıyı `number | string` görür. Uygulamanın JSON
ayarında `JsonNumberHandling.Strict` kullanın (yukarıdaki örnek). Bu uygulamanın ayarıdır; Haldus dayatmaz.

**`decimal` alanlar JavaScript'te `number` olur.** Şemada `type: number` olarak görünürler ve istemcide kayan noktalı
sayıdır. Para gibi kesinlik isteyen değerleri arayüzde hesaplamaya katarken bunu hesaba katın.

**Sürümü `ETag` başlığından okuyun.** `ConcurrencyChecked` kaynaklarda sürüm gövdede değil `ETag` başlığındadır ve
güncelleme, silme, geçiş istekleri onu `If-Match` başlığıyla geri gönderir (eksikse `428`, eskiyse `412`). Panel sürümü
yanıtın başlıklarından okur:

```tsx
const etag = order.data.headers.get('ETag') ?? '';
const request = { headers: { 'If-Match': etag } };
```

**Tekrar korumalı uçlara `Idempotency-Key` gönderin.** `Idempotent()` bildiren kaynakta aynı anahtarla gelen ikinci
istek ilk yanıtı alır; panel sipariş penceresi her açıldığında bir anahtar üretir, böylece aynı pencereden yinelenen
gönderim ikinci bir sipariş oluşturmaz.

**Dili backend'e taşıyın.** Haldus'un hata ve doğrulama mesajları `Accept-Language` başlığına göre İngilizce veya
Türkçe döner. Arayüzün dilini bu başlıkla gönderirseniz backend'in mesajlarını çevirmeden gösterebilirsiniz.

**Tarayıcı başka bir kaynaktan çağırıyorsa başlıkları açın.** Arayüz backend'den farklı bir kaynaktan (origin) çalışıyorsa
CORS gerekir ve tarayıcı `ETag` ve `Location` başlıklarını ancak açıkça izin verilirse okutur. Örnek mağaza yalnızca
sayılan kaynaklara izin verir (`Shop:CorsOrigins`) ve bu iki başlığı açar:

<!-- example: cors -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:8081")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("ETag", "Location")));

var app = builder.Build();

app.UseCors();
app.UseHaldus();
app.MapCrud<Product>();

app.Run();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

Panel geliştirmede bu sorunu yaşamaz: Vite, `/api` önekiyle gelen istekleri backend'e yönlendirir ve istekler aynı
kaynaktan gider. Cihazda çalışan mobil uygulama da CORS'a ihtiyaç duymaz; backend'in adresini `EXPO_PUBLIC_API_URL` ile
alır.

## Sınırlar

- Haldus frontend veya mobil kod üretmez; form, liste veya ekran bileşeni yoktur.
- Doğrulama kuralları (DataAnnotations) şemaya girer, ama istemcide ayrıca çalıştırılmaz; örnekler doğrulamayı
  backend'e bırakır.
- Şemanın üretimi uygulamanın başlatılmasına dayanır. Başlangıçta koşulsuz bir dış bağlantı kuran kod derlemeyi bozar.
- Mobil örnek iOS'ta çalıştırılmadı (Expo'nun web hedefinde ve Android emülatöründe denendi).

## Ayrıca bakın

- [Kiracılar, kullanıcılar ve yetkilendirme](tenancy-and-ownership.md): örnek mağazanın kimlik doğrulaması.
- [HTTP sözleşmesi](../reference/http.md)
- [Örnekler](../samples.md)
- [Üretime hazırlık](production.md)
