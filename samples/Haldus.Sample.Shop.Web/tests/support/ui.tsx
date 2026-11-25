// Bileşen testlerinin ortak kurulumu. DOM kütüphanesi yoktur: bileşenler `react-dom/server` ile HTML metnine çizilir
// ve o metin okunur (html.ts). Bu yüzden yalnızca İLK çizim sınanır; tıklama, yazma, effect, odak ve portal içeriği
// (açık pencere, açılır menü) burada görülemez.
import "./browser.ts"
import "./fetch.ts"

import type { ReactNode } from "react"
import { renderToStaticMarkup } from "react-dom/server"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"

import en from "../../src/i18n/locales/en.json"
import tr from "../../src/i18n/locales/tr.json"
import i18n, { languages, type Language } from "../stubs/i18n-translated.ts"
import {
  MemoryRouter,
  redirects,
  type InitialEntry,
  type NavigateProps,
} from "../stubs/react-router.ts"
import { parse, type HtmlElement } from "./html.ts"
import { replaceModule } from "./modules.ts"

replaceModule("@/i18n", "stubs/i18n-translated.ts")
replaceModule("react-router", "stubs/react-router.ts", true)

// Yukarıdaki değişikliklerden sonra yüklenmeleri gerekir.
const { ThemeProvider } = await import("@/components/theme-provider")
const { TooltipProvider } = await import("@/components/ui/tooltip")

export const { signIn, signOut } = await import("@/lib/session")

export { languages, type Language }
export const locales = { en, tr }

export async function setLanguage(language: Language) {
  await i18n.changeLanguage(language)
}

/** Çeviri metnindeki `{{ad}}` yer tutucularını doldurur; beklenen metni i18next'e sormadan kurmak için. */
export function fill(
  template: string,
  values: Record<string, string | number>
): string {
  return template.replace(/\{\{(\w+)\}\}/g, (_, name: string) =>
    String(values[name])
  )
}

/** Yeniden denemeyen ve süreci ayakta tutacak zamanlayıcı kurmayan bir önbellek. */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: Infinity, staleTime: Infinity },
    },
  })
}

export type Rendered = {
  html: string
  root: HtmlElement
  /** Çizim sırasında istenen yönlendirmeler (`<Navigate>`). */
  redirects: NavigateProps[]
}

/** Bileşeni uygulamadaki sarmalayıcılarıyla (tema, önbellek, ipucu, yönlendirici) çizer. */
export function render(
  ui: ReactNode,
  options: { at?: InitialEntry; client?: QueryClient } = {}
): Rendered {
  redirects.length = 0

  const html = renderToStaticMarkup(
    <ThemeProvider>
      <QueryClientProvider client={options.client ?? createQueryClient()}>
        <TooltipProvider>
          <MemoryRouter initialEntries={[options.at ?? "/"]}>{ui}</MemoryRouter>
        </TooltipProvider>
      </QueryClientProvider>
    </ThemeProvider>
  )

  return { html, root: parse(html), redirects: [...redirects] }
}
