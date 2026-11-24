import { defineConfig } from 'orval';

// İstemci, backend'in build sırasında ürettiği ve commit'lenen şemadan üretilir. Şema kopyalanmaz; göreli yoldan
// okunur. Üretim çalışan bir sunucuya bağlı değildir: `pnpm generate:api`.
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
