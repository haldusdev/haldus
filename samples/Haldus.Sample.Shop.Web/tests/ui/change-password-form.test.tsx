import assert from "node:assert/strict"
import { after, before, describe, it } from "node:test"

import {
  accessibilityProblems,
  accessibleName,
  all,
  one,
  text,
  untranslated,
} from "../support/html.ts"
import { replaceModule } from "../support/modules.ts"
import { languages, locales, render, setLanguage } from "../support/ui.tsx"

// Gerçek pencere içeriğini portala çizer ve sunucuda boş çıkar (change-password-dialog.test.tsx). Burada yerine,
// içeriği yerinde çizen bir taklit konur: sınanan, pencerenin içindeki formun ilk halidir. Radix'in davranışı
// (odak tuzağı, Esc, kapatma düğmesi) ve gönderim sınanmaz.
replaceModule("@/components/ui/dialog", "stubs/dialog.tsx")

const { ChangePasswordDialog } =
  await import("@/components/change-password-dialog")

function open() {
  return render(<ChangePasswordDialog open onOpenChange={() => {}} />)
}

describe("ChangePasswordDialog form (inline dialog double)", () => {
  after(() => setLanguage("en"))

  it("renders nothing while closed", () => {
    const { html } = render(
      <ChangePasswordDialog open={false} onOpenChange={() => {}} />
    )

    assert.equal(html, "")
  })

  for (const language of languages) {
    describe(language, () => {
      const copy = locales[language]

      before(() => setLanguage(language))

      it("has a title and says what happens to other sessions", () => {
        const { root } = open()

        assert.equal(text(one(root, "h2")), copy.password.title)
        assert.ok(text(root).includes(copy.password.description))
      })

      it("asks for the current password", () => {
        const { root } = open()
        const current = one(root, "#currentPassword")

        assert.equal(current.attributes.type, "password")
        assert.equal(current.attributes.autocomplete, "current-password")
        assert.ok("required" in current.attributes)
        assert.equal(accessibleName(root, current), copy.password.current)
      })

      it("asks for the new password and describes its rule", () => {
        const { root } = open()
        const next = one(root, "#changedPassword")
        const help = one(root, `#${next.attributes["aria-describedby"]}`)

        assert.equal(next.attributes.type, "password")
        assert.equal(next.attributes.autocomplete, "new-password")
        assert.ok("required" in next.attributes)
        assert.equal(accessibleName(root, next), copy.password.new)
        assert.equal(text(help), copy.login.passwordHelp)
      })

      it("has no other field and no placeholder standing in for a label", () => {
        const { root } = open()
        const inputs = all(root, "input")

        assert.equal(inputs.length, 2)

        for (const input of inputs) {
          assert.notEqual(
            text(one(root, `label[for=${input.attributes.id}]`)),
            ""
          )
          assert.equal(input.attributes.placeholder, undefined)
        }
      })

      it("can be cancelled or saved", () => {
        const { root } = open()
        const form = one(root, "form")

        assert.equal(text(one(form, "button[type=button]")), copy.common.cancel)
        assert.equal(text(one(form, "button[type=submit]")), copy.common.save)
        assert.equal(all(root, "button[disabled]").length, 0)
        assert.ok("novalidate" in form.attributes)
      })

      it("starts without an error", () => {
        const { root } = open()

        assert.equal(all(root, "[role=alert]").length, 0)
        assert.equal(all(root, "[aria-invalid]").length, 0)
      })

      it("is labelled and fully translated", () => {
        const { root } = open()

        assert.deepEqual(accessibilityProblems(root), [])
        assert.deepEqual(untranslated(root), [])
      })
    })
  }
})
