import assert from "node:assert/strict"
import { after, before, describe, it } from "node:test"

import { calls } from "../support/fetch.ts"
import {
  accessibilityProblems,
  accessibleName,
  all,
  one,
  text,
  untranslated,
} from "../support/html.ts"
import { languages, locales, render, setLanguage } from "../support/ui.tsx"

const { LoginForm } = await import("@/components/login-form")

// Yalnızca ilk çizim: giriş kipi. Kayıt, "parolamı unuttum" ve sıfırlama kiplerine tıklamayla geçilir; onlar ve
// gönderim (istek, hata iletileri, bekleme durumu) burada sınanamaz.
describe("LoginForm", () => {
  after(() => setLanguage("en"))

  for (const language of languages) {
    describe(language, () => {
      const copy = locales[language].login

      before(() => setLanguage(language))

      it("shows the sign-in title and description", () => {
        const { root } = render(<LoginForm />)

        assert.equal(text(one(root, "[data-slot=card-title]")), copy.title)
        assert.equal(
          text(one(root, "[data-slot=card-description]")),
          copy.description
        )
      })

      it("has a labelled e-mail field the browser can fill in", () => {
        const { root } = render(<LoginForm />)
        const email = one(root, "#email")

        assert.equal(email.tag, "input")
        assert.equal(email.attributes.type, "email")
        assert.equal(email.attributes.autocomplete, "email")
        assert.equal(email.attributes.inputmode, "email")
        assert.ok("required" in email.attributes)
        assert.equal(email.attributes.value, "")
        assert.equal(accessibleName(root, email), copy.email)
      })

      it("has a labelled password field for the current password", () => {
        const { root } = render(<LoginForm />)
        const password = one(root, "#password")

        assert.equal(password.tag, "input")
        assert.equal(password.attributes.type, "password")
        assert.equal(password.attributes.autocomplete, "current-password")
        assert.ok("required" in password.attributes)
        assert.equal(accessibleName(root, password), copy.password)
      })

      it("names every field with a visible label, not a placeholder", () => {
        const { root } = render(<LoginForm />)
        const inputs = all(root, "input")

        assert.deepEqual(
          inputs.map((input) => input.attributes.id),
          ["email", "password"]
        )

        for (const input of inputs) {
          const label = one(root, `label[for=${input.attributes.id}]`)

          assert.notEqual(text(label), "")
          assert.ok(!label.attributes.class?.split(" ").includes("sr-only"))
          assert.equal(input.attributes.placeholder, undefined)
        }
      })

      it("submits with a single enabled button inside the form", () => {
        const { root } = render(<LoginForm />)
        const submit = one(one(root, "form"), "button[type=submit]")

        assert.equal(text(submit), copy.submit)
        assert.ok(!("disabled" in submit.attributes))
        assert.equal(all(root, "button[type=submit]").length, 1)
      })

      it("offers password recovery and account creation", () => {
        const { root } = render(<LoginForm />)
        const controls = all(root, "button[type=button]").map(text)

        assert.deepEqual(controls, [copy.forgot, copy.switchToRegister])
        assert.ok(text(root).includes(copy.noAccount))
      })

      it("starts without messages or the fields of other modes", () => {
        const { root } = render(<LoginForm />)

        assert.equal(all(root, "[role=alert]").length, 0)
        assert.equal(all(root, "[role=status]").length, 0)
        assert.equal(all(root, "[aria-invalid]").length, 0)
        assert.equal(all(root, "#code").length, 0)
        assert.equal(all(root, "#newPassword").length, 0)
      })

      it("leaves validation to the backend", () => {
        const { root } = render(<LoginForm />)

        assert.ok("novalidate" in one(root, "form").attributes)
      })

      it("is labelled and fully translated", () => {
        const { root } = render(<LoginForm />)

        assert.deepEqual(accessibilityProblems(root), [])
        assert.deepEqual(untranslated(root), [])
      })
    })
  }

  it("reads differently in each language", async () => {
    await setLanguage("en")
    const english = text(render(<LoginForm />).root)
    await setLanguage("tr")
    const turkish = text(render(<LoginForm />).root)

    assert.notEqual(english, turkish)
    assert.ok(turkish.includes("Parola"))
  })

  it("does not call the backend while rendering", () => {
    render(<LoginForm />)

    assert.equal(calls.length, 0)
  })
})
