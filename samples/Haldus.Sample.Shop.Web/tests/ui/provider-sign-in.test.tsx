import assert from "node:assert/strict"
import { after, before, describe, it } from "node:test"

import type { AuthConfig } from "@/api/model"

import { calls } from "../support/fetch.ts"
import {
  accessibilityProblems,
  all,
  one,
  text,
  untranslated,
} from "../support/html.ts"
import {
  createQueryClient,
  languages,
  locales,
  render,
  setLanguage,
} from "../support/ui.tsx"

const { getGetAuthConfigQueryKey } = await import("@/api/auth/auth")
const { LoginForm } = await import("@/components/login-form")
const { LoginCallbackPage } = await import("@/pages/login-callback")

const offered: AuthConfig = {
  passwordSignIn: true,
  provider: {
    authority: "https://id.example.com",
    clientId: "shop-panel",
    scopes: "openid profile",
  },
  serverSignIn: false,
}

/** Önbelleği backend'in giriş yapılandırmasıyla doldurur; form onu ilk çizimde oradan okur. */
function configured(config: AuthConfig) {
  const client = createQueryClient()

  client.setQueryData(getGetAuthConfigQueryKey(), {
    data: config,
    status: 200,
    headers: new Headers(),
  })
  return client
}

// Yalnızca ilk çizim: düğmenin yönlendirmesi, dönüş sayfasının girişi tamamlaması ve hata halleri tıklamayla veya
// effect ile başlar; onlar etkileşim testlerindedir (tests/e2e/provider.e2e.ts).
describe("provider sign-in", () => {
  after(() => setLanguage("en"))

  for (const language of languages) {
    describe(language, () => {
      const copy = locales[language].login

      before(() => setLanguage(language))

      it("offers the provider next to the password form when the backend names one", () => {
        const { root } = render(<LoginForm />, { client: configured(offered) })
        const form = one(root, "form")
        const controls = all(form, "button[type=button]").map(text)

        assert.deepEqual(controls, [
          copy.forgot,
          copy.switchToRegister,
          copy.providerSignIn,
        ])
        assert.equal(
          text(one(root, "[data-slot=field-separator-content]")),
          copy.providerOr
        )
        // Parola formu olduğu gibidir: alanları ve tek gönderme düğmesi.
        assert.deepEqual(
          all(root, "input").map((input) => input.attributes.id),
          ["email", "password"]
        )
        assert.equal(text(one(form, "button[type=submit]")), copy.submit)
      })

      it("keeps the provider button out of the form's submission and enabled", () => {
        const { root } = render(<LoginForm />, { client: configured(offered) })
        const button = all(root, "button[type=button]").at(-1)

        assert.ok(button)
        assert.equal(text(button), copy.providerSignIn)
        assert.ok(!("disabled" in button.attributes))
        assert.equal(all(root, "[role=alert]").length, 0)
      })

      it("shows no provider button when the backend names none", () => {
        const { root } = render(<LoginForm />, {
          client: configured({ ...offered, provider: null }),
        })

        assert.deepEqual(all(root, "button[type=button]").map(text), [
          copy.forgot,
          copy.switchToRegister,
        ])
        assert.equal(all(root, "[data-slot=field-separator]").length, 0)
      })

      it("shows no provider button while the configuration is unknown", () => {
        const { root } = render(<LoginForm />)

        assert.ok(!text(root).includes(copy.providerSignIn))
        assert.equal(all(root, "[data-slot=field-separator]").length, 0)
      })

      it("the form with a provider is labelled and fully translated", () => {
        const { root } = render(<LoginForm />, { client: configured(offered) })

        assert.deepEqual(accessibilityProblems(root), [])
        assert.deepEqual(untranslated(root), [])
      })

      it("the callback page says that the sign-in is being completed", () => {
        const { root, redirects } = render(<LoginCallbackPage />, {
          at: "/login/callback?code=abc&state=def",
        })

        assert.equal(
          text(one(root, "[data-slot=card-title]")),
          copy.callbackTitle
        )
        assert.equal(text(one(root, "[role=status]")), copy.callbackPending)
        assert.equal(all(root, "[role=alert]").length, 0)
        assert.equal(all(root, "a").length, 0)
        assert.deepEqual(redirects, [])
        assert.deepEqual(accessibilityProblems(root), [])
        assert.deepEqual(untranslated(root), [])
      })
    })
  }

  it("does not call the backend or the provider while rendering", () => {
    render(<LoginForm />, { client: configured(offered) })
    render(<LoginCallbackPage />)

    assert.equal(calls.length, 0)
  })
})
