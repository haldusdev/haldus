import assert from "node:assert/strict"

import { button, field, role, text } from "./support/chrome.ts"
import { en, scene } from "./support/harness.ts"

scene(
  "a category is created under a parent chosen from the list",
  async ({ shop, page, signIn }) => {
    shop.categories.push({
      id: 1,
      name: "Kitchen",
      parentId: null,
      position: 0,
    })
    await signIn("ada@example.com", { path: "/categories" })
    await page.see(text("Kitchen"))

    await page.click(button(en.categories.new))
    await page.see(role("dialog", en.categories.new))

    await page.click(button(en.common.create))
    await page.see(text("The Name field is required."))

    await page.fill(field(en.categories.name), "Kettles")
    await page.click(field(en.categories.parent))
    await page.click(role("option", "Kitchen"))
    await page.click(button(en.common.create))

    await page.see(text(en.categories.created))
    await page.gone(role("dialog", en.categories.new))
    await page.see(text("Kettles"))
    assert.deepEqual(shop.last("POST", "/categories").body, {
      name: "Kettles",
      parentId: 1,
    })
  }
)

scene(
  "a category in use cannot be deleted and says why",
  async ({ shop, page, signIn, fill }) => {
    shop.categories.push({
      id: 1,
      name: "Kitchen",
      parentId: null,
      position: 0,
    })
    shop.addProducts({ name: "Kettle", categoryId: 1 })
    await signIn("ada@example.com", { path: "/categories" })

    await page.click(
      button(fill(en.categories.deleteAction, { name: "Kitchen" }))
    )
    await page.click(button(en.common.delete))

    // 409: backend'in açıklaması pencerenin içinde gösterilir, pencere açık ve kayıt listede kalır.
    assert.equal(
      await page.textOf(role("alert")),
      "The record is referenced by other records."
    )
    await page.see(role("alertdialog", undefined))
    assert.equal(shop.categories.length, 1)

    await page.click(button(en.common.cancel))
    await page.gone(role("alertdialog", undefined))
    await page.see(text("Kitchen"))

    // Ürün başka kategoriye geçince silinebilir.
    shop.products[0].categoryId = null
    await page.click(
      button(fill(en.categories.deleteAction, { name: "Kitchen" }))
    )
    // Yeniden açılan pencere önceki hatayı taşımaz.
    await page.see(role("alertdialog", undefined))
    assert.equal(await page.exists(role("alert")), false)
    await page.click(button(en.common.delete))
    await page.see(text(en.categories.deleted))
    await page.see(text(en.categories.empty))
  }
)
