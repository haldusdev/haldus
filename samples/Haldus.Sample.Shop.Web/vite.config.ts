import { resolve } from "node:path"
import tailwindcss from "@tailwindcss/vite"
import react from "@vitejs/plugin-react"
import { defineConfig, loadEnv } from "vite"

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, import.meta.dirname, "")

  return {
    plugins: [react(), tailwindcss()],
    resolve: {
      alias: {
        "@": resolve(import.meta.dirname, "./src"),
      },
    },
    server: {
      // Geliştirmede istekler /api önekiyle gelir ve örnek backend'e yönlendirilir; böylece tarayıcı tek bir
      // kaynakla konuşur ve backend'de CORS açmak gerekmez.
      proxy: {
        "/api": {
          target: env.SHOP_API_URL || "http://localhost:5080",
          changeOrigin: true,
          rewrite: (path) => path.replace(/^\/api/, ""),
        },
      },
    },
  }
})
