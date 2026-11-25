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

const { VerifyEmailBanner } = await import("@/components/verify-email-banner")

// Kodun gönderilmesi, yeniden istenmesi ve hata iletisi tıklamayla başlar; burada yalnızca şeridin ne zaman ve
// nasıl göründüğü sınanır.
describe("VerifyEmailBanner", () => {
  beforeEach(() => signOut())
  after(() => {
    signOut()
    return setLanguage("en")
  })

  it("renders nothing without a session", () => {
    assert.equal(render(<VerifyEmailBanner />).html, "")
  })

  it("renders nothing when the e-mail is already verified", () => {
    signIn(session({ emailVerified: true }))

    assert.equal(render(<VerifyEmailBanner />).html, "")
  })

  for (const language of languages) {
    describe(`unverified session (${language})`, () => {
      const copy = locales[language].verify

      before(() => setLanguage(language))
      beforeEach(() => signIn(session({ email: "ada@example.com" })))

      it("is a region named by its heading", () => {
        const { root } = render(<VerifyEmailBanner />)
        const region = one(root, "section")
        const heading = one(root, "h2")

        assert.equal(
          region.attributes["aria-labelledby"],
          heading.attributes.id
        )
        assert.equal(text(heading), copy.title)
      })

      it("tells which address the code was sent to", () => {
        const { root } = render(<VerifyEmailBanner />)

        assert.ok(
          text(root).includes(
            fill(copy.description, { email: "ada@example.com" })
          )
        )
      })

      it("has a labelled one-time code field", () => {
        const { root } = render(<VerifyEmailBanner />)
        const code = one(root, "input")

        assert.equal(accessibleName(root, code), copy.code)
        assert.equal(
          text(one(root, `label[for=${code.attributes.id}]`)),
          copy.code
        )
        assert.equal(code.attributes.autocomplete, "one-time-code")
        assert.equal(code.attributes.maxlength, "12")
        assert.ok("required" in code.attributes)
        assert.equal(code.attributes.value, "")
      })

      it("starts without an error", () => {
        const { root } = render(<VerifyEmailBanner />)
        const code = one(root, "input")

        assert.equal(all(root, "[role=alert]").length, 0)
        assert.equal(code.attributes["aria-invalid"], undefined)
        assert.equal(code.attributes["aria-describedby"], undefined)
      })

      it("offers to verify and to send the code again", () => {
        const { root } = render(<VerifyEmailBanner />)
        const form = one(root, "form")

        assert.equal(text(one(form, "button[type=submit]")), copy.submit)
        assert.equal(text(one(form, "button[type=button]")), copy.resend)
        assert.equal(all(root, "button[disabled]").length, 0)
      })

      it("is labelled and fully translated", () => {
        const { root } = render(<VerifyEmailBanner />)

        assert.deepEqual(accessibilityProblems(root), [])
        assert.deepEqual(untranslated(root), [])
      })
    })
  }

  it("shows the address as text, never as markup", () => {
    signIn(session({ email: '<img src="x">@example.com' }))

    const { root } = render(<VerifyEmailBanner />)

    assert.equal(all(root, "img").length, 0)
    assert.ok(text(root).includes('<img src="x">@example.com'))
  })
})
