# Örnek mağazanın istemcisi

Bu klasör bir uygulama değildir: [örnek mağazanın](../Haldus.Sample.Shop) API'sinden **üretilen** TypeScript
istemcisidir. Haldus'un frontend ve mobil tarafına verdiği şeyin ne olduğunu ve nasıl tüketildiğini gösterir.

- Backend her derlemede `openapi.json` dosyasını üretir ve bu dosya commit'lenir.
- [orval](https://orval.dev) o dosyadan tipleri ve [TanStack Query](https://tanstack.com/query) hook'larını üretir
  (`fetch` istemcisiyle). Üretilen kod `src/api` altındadır ve o da commit'lenir.
- Hiçbir tip veya hook elle yazılmaz; `src/api` altındaki dosyalar elle düzenlenmez.

## Yeniden üretmek

Backend'de bir uç veya DTO değiştiğinde önce backend derlenir (şema yenilenir), sonra istemci yeniden üretilir:

```bash
dotnet build ../Haldus.Sample.Shop
```

```bash
pnpm install
```

```bash
pnpm generate:api
```

```bash
pnpm typecheck
```

`pnpm typecheck` üretilen kodun hatasız derlendiğini doğrular; şemanın orval'dan sorunsuz geçtiğinin kanıtı budur.

## Kullanmak

Üretilen hook'lar bir React (web) veya React Native (Expo) uygulamasında aynı şekilde kullanılır; her biri bir
`QueryClientProvider` altında çalışır:

```tsx
import { useListCars, useCreateCar } from './api/cars/cars';

function Cars() {
  const cars = useListCars({ pageSize: 20 });
  const create = useCreateCar();

  // cars.data?.data.items: CarResponse[]
  // create.mutate({ data: { plate: '34 ABC 123', seats: 5 } })
}
```

Adresler görelidir (`/cars`). Başka bir sunucuya gidecekse temel adres orval yapılandırmasında (`output.baseUrl`) ya da
özel bir `fetch` sarmalayıcısıyla (`output.override.mutator`) verilir; oturumun token'ı (`Authorization`) da orada
eklenir. Çalışan örneği yönetim panelinde ve mobil uygulamadadır.

## Bilinmesi gerekenler

- Durum alanları (`status`, `kind` gibi) adlarıyla taşınır ve istemcide metin birleşimi (union) tipleri olur.
- Sayfalı listeler `PagedResultOf…Response` tipindedir: `items`, `page`, `pageSize`, `totalCount`.
- Hata yanıtları `ProblemDetails` / `HttpValidationProblemDetails` biçimindedir; alan hataları `errors` altındadır.
- Örnek mağaza sayıları katı okur (`JsonNumberHandling.Strict`). ASP.NET Core'un varsayılan ayarı metin içindeki
  sayıyı da kabul eder ve şemaya `integer | string` olarak yansır; istemci tipleri o zaman her sayıyı `number | string`
  görürdü.
