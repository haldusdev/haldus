import assert from "node:assert/strict"
import { after, beforeEach, describe, it } from "node:test"
import { useTranslation } from "react-i18next"

import { Route, Routes } from "../stubs/react-router.ts"
import { session } from "../support/fixtures.ts"
import { parse, spoken, text, untranslated } from "../support/html.ts"
import {
  createQueryClient,
  languages,
  render,
  setLanguage,
  signIn,
  signOut,
  type Language,
} from "../support/ui.tsx"

const { getListCategoriesQueryKey } =
  await import("@/api/categories/categories")
const { getListProductsQueryKey } = await import("@/api/products/products")
const { DashboardLayout } = await import("@/components/dashboard-layout")
const { LoginForm } = await import("@/components/login-form")
const { ProductsPage } = await import("@/pages/products")

const EMAIL = "ada@example.com"
const PRODUCT = "Desk lamp"
const CATEGORY = "Lighting"

function products() {
  const client = createQueryClient()

  client.setQueryData(
    getListProductsQueryKey({ page: 1, pageSize: 10, sort: "name" }),
    {
      data: {
        items: [
          {
            id: "p-1",
            name: PRODUCT,
            price: 12,
            description: null,
            categoryId: 3,
            metadata: {},
          },
        ],
        page: 1,
        pageSize: 10,
        totalCount: 1,
      },
      status: 200,
      headers: new Headers(),
    }
  )
  client.setQueryData(getListCategoriesQueryKey({ pageSize: 100 }), {
    data: {
      items: [{ id: 3, name: CATEGORY, parentId: null, position: 0 }],
      page: 1,
      pageSize: 100,
      totalCount: 1,
    },
    status: 200,
    headers: new Headers(),
  })

  return client
}

// İki ekran: oturumsuz kullanıcının gördüğü ve oturumu olan (e-postası doğrulanmamış) kullanıcının gördüğü.
const screens = {
  login: () => render(<LoginForm />),
  dashboard: () =>
    render(
      <Routes>
        <Route element={<DashboardLayout />}>
          <Route path="products" element={<ProductsPage />} />
        </Route>
      </Routes>,
      { at: "/products", client: products() }
    ),
}

/** Ekranın bir dildeki, harf içeren bütün metinleri. */
async function texts(
  screen: keyof typeof screens,
  language: Language
): Promise<Set<string>> {
  await setLanguage(language)

  return new Set(
    spoken(screens[screen]().root)
      .map(text)
      .filter((value) => /\p{L}/u.test(value))
  )
}

describe("translations on screen", () => {
  beforeEach(() => signIn(session({ email: EMAIL, emailVerified: false })))
  after(() => {
    signOut()
    return setLanguage("en")
  })

  it("a key missing from the resources would be caught", async () => {
    function Broken() {
      const { t } = useTranslation()

      // Kaynaklarda olmayan bir anahtar; tip denetimi normalde buna izin vermez.
      return <p>{t("login.doesNotExist" as never)}</p>
    }

    for (const language of languages) {
      await setLanguage(language)

      assert.deepEqual(untranslated(render(<Broken />).root), [
        "login.doesNotExist",
      ])
    }
  })

  it("an unfilled placeholder would be caught", () => {
    assert.deepEqual(
      untranslated(parse("<p>We sent a code to {{email}}.</p>")),
      ["{{email}}"]
    )
  })

  for (const screen of ["login", "dashboard"] as const) {
    describe(screen, () => {
      it("shows no raw key in any language", async () => {
        for (const language of languages) {
          await setLanguage(language)

          assert.deepEqual(untranslated(screens[screen]().root), [])
        }
      })

      it("says in Turkish everything it says in English", async () => {
        const english = await texts(screen, "en")
        const turkish = await texts(screen, "tr")

        // Veri çevrilmez.
        const data = new Set([EMAIL, "AD", PRODUCT, CATEGORY])

        // Bilinen açık yok: çeviriden geçmeyen, koda gömülü bir metin bu listeye yazılmadan testi geçemez.
        const hardCoded = new Set<string>([])

        const same = [...english].filter(
          (value) =>
            turkish.has(value) && !data.has(value) && !hardCoded.has(value)
        )

        assert.deepEqual(same, [])
        assert.ok(english.size > 5)
      })
    })
  }
})
