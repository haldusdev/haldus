import { defineConfig } from 'orval';

// İstemci, backend'in build sırasında ürettiği ve commit'lenen şemadan üretilir: `pnpm generate:api`.
// Şema kopyalanmaz, göreli yoldan okunur; üretim çalışan bir sunucuya bağlı değildir.
export default defineConfig({
  shop: {
    input: {
      target: '../Haldus.Sample.Shop/openapi.json',
      // Uygulama yalnızca kullandığı kaynakların istemcisini taşır.
      filters: { tags: ['Products', 'Favorites', 'Baskets'] },
    },
    output: {
      mode: 'tags-split',
      target: 'api',
      schemas: 'api/model',
      client: 'react-query',
      httpClient: 'fetch',
      clean: true,
      override: {
        // Her istek bu sarmalayıcıdan geçer: backend'in adresi, kimlik başlıkları, dil ve hata davranışı orada.
        mutator: { path: './lib/api-fetch.ts', name: 'apiFetch' },
        // Hook'lar yalnızca başarılı yanıtı döndürür; başarısız yanıt hata olarak fırlatılır.
        fetch: { forceSuccessResponse: true },
        // Ürün listesi sayfa sayfa, aşağı kaydırdıkça yüklenir.
        operations: {
          ListProducts: { query: { useInfinite: true, useInfiniteQueryParam: 'page' } },
        },
      },
    },
  },
});
