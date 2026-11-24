import { defineConfig } from "orval"

// İstemci, backend'in build sırasında ürettiği ve commit'lenen şemadan üretilir: `pnpm generate:api`.
// Şema kopyalanmaz, göreli yoldan okunur; üretim çalışan bir sunucuya bağlı değildir.
export default defineConfig({
  shop: {
    input: {
      target: "../Haldus.Sample.Shop/openapi.json",
      // Örnek mağazanın yetmişten çok kaynağı var; panel yalnızca kullandıklarının istemcisini taşır.
      filters: { tags: ["Auth", "Products", "Categories", "Orders"] },
    },
    output: {
      mode: "tags-split",
      target: "src/api",
      schemas: "src/api/model",
      client: "react-query",
      httpClient: "fetch",
      clean: true,
      override: {
        // Her istek bu sarmalayıcıdan geçer: oturumun token'ı, dil ve hata davranışı orada.
        mutator: { path: "./src/lib/api-fetch.ts", name: "apiFetch" },
        // Hook'lar yalnızca başarılı yanıtı döndürür; başarısız yanıt hata olarak fırlatılır.
        fetch: { forceSuccessResponse: true },
      },
    },
  },
})
