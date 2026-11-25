// Etkileşim testlerinin ortak kurulumu: dosya başına bir tarayıcı, test başına yeni bir sahte mağaza ve yalıtılmış
// bir tarayıcı oturumu. Tarayıcı veya derlenmiş panel yoksa testler atlanır ve nedenini söyler.
import { existsSync } from "node:fs"
import path from "node:path"
import { after, before, test, type TestContext } from "node:test"

import en from "../../../src/i18n/locales/en.json" with { type: "json" }
import tr from "../../../src/i18n/locales/tr.json" with { type: "json" }

import { Browser, findBrowser, type Context, type Page } from "./chrome.ts"
import { Shop } from "./shop.ts"

export { en, tr }
export const SESSION_KEY = "haldus-shop.session"
export const LANGUAGE_KEY = "haldus-shop.language"

const executable = findBrowser()
const built = existsSync(
  path.resolve(import.meta.dirname, "../../../dist/index.html")
)

const skip = !executable
  ? "no Chromium browser found (set HALDUS_TEST_BROWSER)"
  : !built
    ? "the panel is not built (run `pnpm build`, or `pnpm test:e2e` which builds first)"
    : false

let browser: Browser | undefined

before(async () => {
  if (executable && built) {
    browser = await Browser.launch(executable)
  }
})

after(async () => {
  await browser?.close()
})

export type Scene = {
  shop: Shop
  context: Context
  page: Page
  /** Hesabın oturumunu depoya yazar ve verilen sayfayı açar; giriş formundan geçmeden başlamak için. */
  signIn: (
    email?: string,
    options?: {
      verified?: boolean
      staff?: boolean
      path?: string
      page?: Page
    }
  ) => Promise<void>
  /** Çevirideki `{{ad}}` yer tutucularını doldurur. */
  fill: (template: string, values: Record<string, string | number>) => string
}

/** Bir senaryo: sahte mağaza ve boş bir sekmeyle başlar, sonunda ikisi de kapatılır. */
export function scene(
  name: string,
  body: (scene: Scene, t: TestContext) => Promise<void>
) {
  void test(name, { skip, timeout: 60_000 }, async (t) => {
    if (!browser) {
      throw new Error("The browser was not started.")
    }

    const shop = new Shop()
    await shop.start()
    const context = await browser.context()

    try {
      const page = await context.page()

      await body(
        {
          shop,
          context,
          page,
          fill: (template, values) =>
            template.replace(/\{\{(\w+)\}\}/g, (_, key: string) =>
              String(values[key])
            ),
          signIn: async (email = "ada@example.com", options = {}) => {
            const account =
              shop.accounts.find((candidate) => candidate.email === email) ??
              shop.addAccount(email, "correct-horse-battery", {
                emailVerified: options.verified ?? true,
                isStaff: options.staff ?? true,
              })
            const tokens = shop.issue(account)
            const target = options.page ?? page

            // Depo kaynağa bağlıdır; önce o kaynaktan bir sayfa açılır.
            await target.goto(`${shop.origin}/login`)
            await target.setStorage(
              SESSION_KEY,
              JSON.stringify({
                accessToken: tokens.accessToken,
                refreshToken: tokens.refreshToken,
                accountId: account.id,
                email: account.email,
                tenantId: account.tenantId,
                emailVerified: account.emailVerified,
              })
            )
            await target.goto(`${shop.origin}${options.path ?? "/products"}`)
          },
        },
        t
      )
    } finally {
      await context.close()
      await shop.stop()
    }
  })
}

/** Test tarafındaki bir koşul (ör. sunucuya bir isteğin ulaşması) sağlanana kadar bekler. */
export async function until(
  condition: () => boolean,
  what: string,
  timeout = 8000
): Promise<void> {
  const deadline = Date.now() + timeout

  while (!condition()) {
    if (Date.now() > deadline) {
      throw new Error(`Timed out waiting for ${what}.`)
    }

    await new Promise((resolve) => setTimeout(resolve, 5))
  }
}
