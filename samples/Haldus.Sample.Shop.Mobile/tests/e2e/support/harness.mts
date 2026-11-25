// Ekran testlerinin ortak kurulumu: dosya başına bir tarayıcı, test başına yeni bir sahte mağaza ve yalıtılmış bir
// tarayıcı oturumu. Uygulamanın web dışa aktarımı sürülür (Expo'nun web hedefi): ekranlar, gezinme ve istekler
// cihazdakiyle aynı koddur; cihaza özgü olanlar (güvenli depo, klavye, güvenli alanlar) burada sınanmaz.
import { existsSync } from 'node:fs';
import path from 'node:path';
import { after, before, test, type TestContext } from 'node:test';

import en from '../../../i18n/locales/en.json' with { type: 'json' };
import tr from '../../../i18n/locales/tr.json' with { type: 'json' };

import { Browser, findBrowser, type Context, type Page } from './chrome.mts';
import { dist, Shop } from './shop.mts';

export { en, tr };

export const KEYS = {
  accessToken: 'haldus-shop.session.access',
  refreshToken: 'haldus-shop.session.refresh',
  account: 'haldus-shop.session.account',
} as const;

const executable = findBrowser();
const built = existsSync(path.join(dist, 'index.html'));

const skip = !executable
  ? 'no Chromium browser found (set HALDUS_TEST_BROWSER)'
  : !built
    ? 'the app is not exported for the web (run `pnpm test:e2e`, which exports first)'
    : false;

let browser: Browser | undefined;

before(async () => {
  if (executable && built) {
    browser = await Browser.launch(executable);
  }
});

after(async () => {
  await browser?.close();
});

export type Scene = {
  shop: Shop;
  context: Context;
  page: Page;
  /** Hesabın oturumunu depoya yazar ve verilen ekranı açar; giriş ekranından geçmeden başlamak için. */
  signIn: (email?: string, options?: { verified?: boolean; path?: string }) => Promise<void>;
  /** Çevirideki `{{ad}}` yer tutucularını doldurur. */
  fill: (template: string, values: Record<string, string | number>) => string;
};

/** Bir senaryo: sahte mağaza ve boş bir sekmeyle başlar, sonunda ikisi de kapatılır. */
export function scene(name: string, body: (scene: Scene, t: TestContext) => Promise<void>) {
  void test(name, { skip, timeout: 60_000 }, async (t) => {
    if (!browser) {
      throw new Error('The browser was not started.');
    }

    const shop = new Shop();
    await shop.start();
    const context = await browser.context();

    try {
      const page = await context.page();

      await body(
        {
          shop,
          context,
          page,
          fill: (template, values) =>
            template.replace(/\{\{(\w+)\}\}/g, (_, key: string) => String(values[key])),
          signIn: async (email = 'ada@example.com', options = {}) => {
            const account =
              shop.accounts.find((candidate) => candidate.email === email) ??
              shop.addAccount(email, 'correct-horse-battery', {
                emailVerified: options.verified ?? true,
              });
            const tokens = shop.issue(account);

            // Depo kaynağa bağlıdır; önce o kaynaktan bir sayfa açılır.
            await page.goto(`${shop.origin}/sign-in`);
            await page.setStorage(KEYS.accessToken, tokens.accessToken);
            await page.setStorage(KEYS.refreshToken, tokens.refreshToken);
            await page.setStorage(
              KEYS.account,
              JSON.stringify({
                accountId: account.id,
                email: account.email,
                tenantId: account.tenantId,
                emailVerified: account.emailVerified,
              })
            );
            await page.goto(`${shop.origin}${options.path ?? '/'}`);
          },
        },
        t
      );
    } finally {
      await context.close();
      await shop.stop();
    }
  });
}

/** Test tarafındaki bir koşul (ör. sunucuya bir isteğin ulaşması) sağlanana kadar bekler. */
export async function until(condition: () => boolean, what: string, timeout = 8000): Promise<void> {
  const deadline = Date.now() + timeout;

  while (!condition()) {
    if (Date.now() > deadline) {
      throw new Error(`Timed out waiting for ${what}.`);
    }

    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}
