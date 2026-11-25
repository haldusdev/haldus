import assert from "node:assert/strict"
import { after, before, beforeEach, describe, it } from "node:test"

import { session } from "../support/fixtures.ts"
import {
  accessibilityProblems,
  accessibleName,
  all,
  one,
  text,
  untranslated,
} from "../support/html.ts"
import {
  fill,
  languages,
  locales,
  render,
  setLanguage,
  signIn,
  signOut,
} from "../support/ui.tsx"

const { AppSidebar } = await import("@/components/app-sidebar")
const { NavUser } = await import("@/components/nav-user")
const { SidebarProvider } = await import("@/components/ui/sidebar")

function sidebar(at = "/products") {
  return render(
    <SidebarProvider>
      <AppSidebar />
    </SidebarProvider>,
    { at }
  )
}

// Hesap menüsünün içeriği (dil, tema, parola değiştirme, çıkış) tıklanınca bir portala çizilir; burada yalnızca
// menüyü açan düğme görülür. Yan menünün daraltılması ve mobil görünümü de sınanmaz.
describe("AppSidebar", () => {
  beforeEach(() =>
    signIn(session({ email: "ada@example.com", tenantId: "0123456789abcdef" }))
  )
  after(() => {
    signOut()
    return setLanguage("en")
  })

  it("shows the signed-in e-mail and its initials", () => {
    const { root } = sidebar()
    const account = one(root, "[data-slot=sidebar-footer]")

    assert.ok(text(account).includes("ada@example.com"))
    assert.equal(text(one(account, "[data-slot=avatar-fallback]")), "AD")
  })

  it("follows the session: another account, then none", () => {
    signIn(session({ email: "grace@example.com" }))
    assert.ok(text(sidebar().root).includes("grace@example.com"))

    signOut()

    const { root } = sidebar()

    assert.ok(!text(root).includes("@example.com"))
    assert.equal(text(one(root, "[data-slot=sidebar-footer]")), "")
  })

  it("NavUser renders nothing without a session", () => {
    signOut()

    const { root } = render(
      <SidebarProvider>
        <NavUser />
      </SidebarProvider>
    )

    assert.equal(text(root), "")
    assert.equal(all(root, "button").length, 0)
  })

  for (const language of languages) {
    describe(language, () => {
      const copy = locales[language]

      before(() => setLanguage(language))

      it("names the application", () => {
        const header = one(sidebar().root, "[data-slot=sidebar-header]")

        assert.equal(text(header), `${copy.app.name}${copy.app.tagline}`)
      })

      it("links to every section under its group", () => {
        const { root } = sidebar()
        const content = one(root, "[data-slot=sidebar-content]")

        assert.deepEqual(
          all(content, "[data-slot=sidebar-group-label]").map(text),
          [copy.nav.catalog, copy.nav.sales]
        )
        assert.deepEqual(
          all(content, "a").map((link) => [link.attributes.href, text(link)]),
          [
            ["/products", copy.nav.products],
            ["/categories", copy.nav.categories],
            ["/orders", copy.nav.orders],
          ]
        )
      })

      it("marks only the section being visited", () => {
        for (const [at, href] of [
          ["/products", "/products"],
          ["/orders", "/orders"],
          ["/orders/42", "/orders"],
        ]) {
          const current = all(sidebar(at).root, "a[aria-current=page]")

          assert.deepEqual(
            current.map((link) => link.attributes.href),
            [href]
          )
          assert.equal(current[0].attributes["data-active"], "true")
        }

        assert.equal(all(sidebar("/").root, "a[aria-current=page]").length, 0)
      })

      it("opens the account menu from a named button showing the tenant", () => {
        const { root } = sidebar()
        const trigger = one(root, "button[aria-haspopup=menu]")

        assert.equal(accessibleName(root, trigger), copy.nav.userMenu)
        assert.equal(trigger.attributes["aria-expanded"], "false")
        assert.ok(
          text(trigger).includes(fill(copy.nav.tenant, { tenant: "01234567" }))
        )
        assert.ok(!text(trigger).includes("0123456789"))
      })

      it("is labelled and fully translated", () => {
        const { root } = sidebar()

        assert.deepEqual(accessibilityProblems(root), [])
        assert.deepEqual(untranslated(root), [])
      })
    })
  }
})
