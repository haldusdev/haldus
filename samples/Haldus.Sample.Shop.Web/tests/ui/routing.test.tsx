import assert from "node:assert/strict"
import { after, before, beforeEach, describe, it } from "node:test"

import { Route, Routes, type InitialEntry } from "../stubs/react-router.ts"
import { session } from "../support/fixtures.ts"
import {
  accessibilityProblems,
  all,
  one,
  text,
  untranslated,
} from "../support/html.ts"
import {
  languages,
  locales,
  render,
  setLanguage,
  signIn,
  signOut,
  type Rendered,
} from "../support/ui.tsx"

const { DashboardLayout } = await import("@/components/dashboard-layout")
const { LoginPage } = await import("@/pages/login")

// App.tsx'teki yerleşim; sayfaların yerinde işaretler durur.
function visit(options: { at: InitialEntry }): Rendered {
  return render(
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<DashboardLayout />}>
        <Route path="products" element={<p id="page">products page</p>} />
        <Route path="orders/:id" element={<p id="page">order page</p>} />
        <Route path="*" element={<p id="page">not found page</p>} />
      </Route>
    </Routes>,
    options
  )
}

// react-router'ın `<Navigate>` bileşeni hiçbir şey çizmez ve yönlendirmeyi bir effect içinde yapar; sunucuda
// çalışmaz. Bu testler bileşenin HANGİ yönlendirmeyi istediğini (hedef, `replace`, `state`) kaydeden bir taklitle
// okur (stubs/react-router.ts); adresin gerçekten değiştiğini ve hedef sayfanın çizildiğini görmez.
describe("routing", () => {
  beforeEach(() => signOut())
  after(() => {
    signOut()
    return setLanguage("en")
  })

  describe("without a session", () => {
    it("a dashboard page asks for a redirect to /login and draws nothing", () => {
      const { html, redirects } = visit({ at: "/products" })

      assert.equal(html, "")
      assert.deepEqual(redirects, [
        { to: "/login", replace: true, state: { from: "/products" } },
      ])
    })

    it("remembers the page that was asked for", () => {
      const { redirects } = visit({ at: "/orders/42" })

      assert.deepEqual(redirects, [
        { to: "/login", replace: true, state: { from: "/orders/42" } },
      ])
    })

    it("an unknown address is redirected too, not answered", () => {
      const { html, redirects } = visit({ at: "/nope" })

      assert.equal(html, "")
      assert.equal(redirects.length, 1)
      assert.equal(redirects[0].to, "/login")
    })

    it("/login draws the sign-in form and stays there", () => {
      const { root, redirects } = visit({ at: "/login" })

      assert.deepEqual(redirects, [])
      assert.equal(all(root, "form").length, 1)
      assert.equal(one(root, "#email").attributes.type, "email")
      assert.equal(all(root, "[data-slot=sidebar]").length, 0)
    })
  })

  describe("with a session", () => {
    beforeEach(() => signIn(session({ emailVerified: true })))

    it("/login asks for a redirect to the products page", () => {
      const { html, redirects } = visit({ at: "/login" })

      assert.equal(html, "")
      assert.deepEqual(redirects, [{ to: "/products", replace: true }])
    })

    it("/login returns to the page the visitor came from", () => {
      const { redirects } = visit({
        at: { pathname: "/login", state: { from: "/orders/42" } },
      })

      assert.deepEqual(redirects, [{ to: "/orders/42", replace: true }])
    })

    for (const from of [
      "//evil.example",
      "https://evil.example",
      "orders",
      7,
    ]) {
      it(`/login ignores an unsafe return address (${String(from)})`, () => {
        const { redirects } = visit({
          at: { pathname: "/login", state: { from } },
        })

        assert.deepEqual(redirects, [{ to: "/products", replace: true }])
      })
    }

    it("a dashboard page is drawn inside the layout", () => {
      const { root, redirects } = visit({ at: "/products" })
      const main = one(root, "main")

      assert.deepEqual(redirects, [])
      assert.equal(text(one(main, "#page")), "products page")
      assert.equal(all(main, "header").length, 1)
      assert.equal(all(root, "[data-slot=sidebar]").length, 1)
    })

    it("shows the verification banner only to an unverified account", () => {
      assert.equal(all(visit({ at: "/products" }).root, "section").length, 0)

      signIn(session({ emailVerified: false }))

      const { root } = visit({ at: "/products" })

      assert.equal(all(one(root, "main"), "section").length, 1)
    })

    for (const language of languages) {
      describe(language, () => {
        const copy = locales[language]

        before(() => setLanguage(language))

        it("names the current section in the breadcrumb", () => {
          const breadcrumb = (at: string) =>
            text(one(visit({ at }).root, "[data-slot=breadcrumb]"))

          assert.equal(
            breadcrumb("/products"),
            `${copy.app.name}${copy.nav.products}`
          )
          assert.equal(
            breadcrumb("/orders/42"),
            `${copy.app.name}${copy.nav.orders}`
          )
          assert.equal(breadcrumb("/nope"), copy.app.name)
        })

        it("marks the current section for assistive technology", () => {
          const { root } = visit({ at: "/orders/42" })
          const sidebar = one(root, "[data-slot=sidebar]")

          assert.equal(
            text(one(sidebar, "a[aria-current=page]")),
            copy.nav.orders
          )
          assert.equal(all(sidebar, "a").length, 3)
        })

        it("is labelled and fully translated", () => {
          signIn(session({ emailVerified: false }))

          const { root } = visit({ at: "/products" })

          assert.deepEqual(accessibilityProblems(root), [])
          assert.deepEqual(untranslated(root), [])
        })
      })
    }
  })
})
