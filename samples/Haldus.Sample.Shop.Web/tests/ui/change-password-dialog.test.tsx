import assert from "node:assert/strict"
import { describe, it } from "node:test"

import { render } from "../support/ui.tsx"

const { ChangePasswordDialog } =
  await import("@/components/change-password-dialog")

// Gerçek pencere (Radix Dialog) ile. Açık pencerenin içeriği burada sınanamaz; formu
// change-password-form.test.tsx, pencerenin yerine içeriği yerinde çizen bir taklit koyarak sınar.
describe("ChangePasswordDialog", () => {
  it("renders nothing while closed", () => {
    const { html } = render(
      <ChangePasswordDialog open={false} onOpenChange={() => {}} />
    )

    assert.equal(html, "")
  })

  // Bir davranış değil, bu yöntemin sınırı: Radix içeriği bir portala çizer ve portal sunucuda çizilmez. Bu test
  // bir gün içerik çizilmeye başlarsa haber verir; o zaman taklide gerek kalmaz.
  it("cannot be rendered on the server while open (portal)", () => {
    const { html } = render(
      <ChangePasswordDialog open onOpenChange={() => {}} />
    )

    assert.equal(html, "")
  })
})
