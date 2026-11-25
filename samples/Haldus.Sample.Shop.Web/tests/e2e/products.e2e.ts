import assert from "node:assert/strict"

import { button, css, field, role, text } from "./support/chrome.ts"
import { en, scene } from "./support/harness.ts"

const rows = `[...document.querySelectorAll("tbody tr")].map((row) => row.querySelector("td").innerText.split("\\n")[0].trim())`

scene(
  "the product list loads, searches, sorts and pages",
  async ({ shop, page, signIn, fill }) => {
    shop.categories.push({
      id: 1,
      name: "Kitchen",
      parentId: null,
      position: 0,
    })
    shop.addProducts(
      ...Array.from({ length: 12 }, (_, index) => ({
        name: `Item ${String(index + 1).padStart(2, "0")}`,
        price: 100 - index,
        categoryId: index === 0 ? 1 : null,
      }))
    )

    await signIn()
    await page.see(text("Item 01"))

    // İlk istek: ilk sayfa, ada göre.
    const first = shop.last("GET", "/products")
    assert.equal(first.query.get("page"), "1")
    assert.equal(first.query.get("pageSize"), "10")
    assert.equal(first.query.get("sort"), "name")
    assert.equal(first.query.get("search"), null)
    assert.equal((await page.evaluate<string[]>(rows)).length, 10)
    // Kategorinin adı ayrı bir istekle gelen listeden çözülür.
    await page.see(text("Kitchen"))
    await page.see(
      text(fill(en.pagination.summary_other, { page: 1, pages: 2, count: 12 }))
    )

    await page.click(button(en.pagination.next))
    await page.see(text("Item 11"))
    assert.equal(shop.last("GET", "/products").query.get("page"), "2")
    assert.deepEqual(await page.evaluate(rows), ["Item 11", "Item 12"])

    // Sıralama açılır listeden seçilir (portal içinde çizilir) ve ilk sayfaya dönülür.
    await page.click(field(en.products.sort))
    await page.click(role("option", en.products.sortPriceAsc))
    await page.see(text("Item 12"))
    const sorted = shop.last("GET", "/products")
    assert.equal(sorted.query.get("sort"), "price")
    assert.equal(sorted.query.get("page"), "1")
    assert.equal((await page.evaluate<string[]>(rows))[0], "Item 12")

    // Arama yazarken her tuşta istek gitmez; yazma durunca tek istek gider.
    const before = shop.sent("GET", "/products").length
    await page.fill(field(en.common.search), "it")
    await page.type("em")
    await page.type(" 07")
    await page.waitFor(`${rows}.length === 1`, "the search to narrow the list")
    assert.deepEqual(await page.evaluate(rows), ["Item 07"])
    assert.equal(shop.sent("GET", "/products").length, before + 1)
    assert.equal(shop.last("GET", "/products").query.get("search"), "item 07")

    await page.fill(field(en.common.search), "nothing like this")
    await page.see(text(en.products.emptySearch))
  }
)

scene(
  "an empty catalog offers to create the first product",
  async ({ page, signIn }) => {
    await signIn()

    await page.see(text(en.products.empty))
    await page.see(text(en.products.emptyDescription))
  }
)

scene(
  "creating a product shows the server's field errors, then the new row",
  async ({ shop, page, signIn }) => {
    shop.categories.push({
      id: 7,
      name: "Kitchen",
      parentId: null,
      position: 0,
    })
    await signIn()
    await page.see(text(en.products.empty))

    await page.click(button(en.products.new))
    await page.see(role("dialog", en.products.new))

    // Boş gönderim: doğrulamayı backend yapar, hatalar alanların altında görünür ve pencere açık kalır.
    await page.fill(field(en.products.price), "-5")
    await page.click(button(en.common.save))
    await page.see(text("The Name field is required."))
    await page.see(text("The field Price must be between 0 and 1000000."))
    assert.equal(
      await page.attribute(field(en.products.name), "aria-invalid"),
      "true"
    )
    assert.equal(shop.products.length, 0)

    await page.fill(field(en.products.name), "  Kettle ")
    await page.fill(field(en.products.price), "42.5")
    await page.fill(field(en.products.descriptionField), "Boils water.")
    await page.click(field(en.products.category))
    await page.click(role("option", "Kitchen"))
    await page.click(button(en.common.save))

    await page.see(text(en.products.created))
    await page.gone(role("dialog", en.products.new))
    await page.see(text("Kettle"))
    await page.see(text("Boils water."))

    assert.deepEqual(shop.last("POST", "/products").body, {
      name: "Kettle",
      price: 42.5,
      description: "Boils water.",
      categoryId: 7,
    })
    // Liste, oluşturmadan sonra yeniden istenir.
    assert.equal(await page.exists(text(en.products.empty)), false)
  }
)

scene(
  "editing sends the whole record back, metadata included",
  async ({ shop, page, signIn, fill }) => {
    shop.addProducts({
      name: "Kettle",
      price: 42.5,
      description: "Boils water.",
      metadata: { color: "steel" },
    })
    await signIn()

    await page.click(button(fill(en.products.editAction, { name: "Kettle" })))
    await page.see(role("dialog", en.products.edit))
    assert.equal(await page.valueOf(field(en.products.name)), "Kettle")
    assert.equal(await page.valueOf(field(en.products.price)), "42.5")

    await page.fill(field(en.products.name), "Electric kettle")
    await page.fill(field(en.products.descriptionField), "")
    await page.click(button(en.common.save))

    await page.see(text(en.products.updated))
    await page.see(text("Electric kettle"))

    const update = shop.last("PUT", /^\/products\//)
    assert.equal(update.path, `/products/${shop.products[0].id}`)
    assert.deepEqual(update.body, {
      name: "Electric kettle",
      price: 42.5,
      description: null,
      categoryId: null,
      // PUT kaydın tamamını değiştirir; çanta geri gönderilmeseydi boşalırdı.
      metadata: { color: "steel" },
    })
  }
)

scene("cancelling the dialog sends nothing", async ({ shop, page, signIn }) => {
  await signIn()
  await page.click(button(en.products.new))
  await page.fill(field(en.products.name), "Never saved")

  await page.click(button(en.common.cancel))
  await page.gone(role("dialog", en.products.new))

  // Escape de pencereyi kapatır.
  await page.click(button(en.products.new))
  await page.see(role("dialog", en.products.new))
  // Pencere her açılışta boş başlar.
  assert.equal(await page.valueOf(field(en.products.name)), "")
  await page.press("Escape")
  await page.gone(role("dialog", en.products.new))

  assert.equal(shop.sent("POST", "/products").length, 0)
})

scene(
  "deleting asks first and removes the row only after confirming",
  async ({ shop, page, signIn, fill }) => {
    shop.addProducts({ name: "Kettle" }, { name: "Toaster" })
    await signIn()

    await page.click(button(fill(en.products.deleteAction, { name: "Kettle" })))
    await page.see(role("alertdialog", undefined))
    await page.see(
      text(fill(en.products.deleteDescription, { name: "Kettle" }))
    )

    await page.click(button(en.common.cancel))
    await page.gone(role("alertdialog", undefined))
    assert.equal(shop.sent("DELETE", /^\/products\//).length, 0)

    await page.click(button(fill(en.products.deleteAction, { name: "Kettle" })))
    await page.click(button(en.common.delete))

    await page.see(text(en.products.deleted))
    await page.gone(text("Kettle"))
    await page.see(text("Toaster"))
    assert.deepEqual(
      shop.products.map((product) => product.name),
      ["Toaster"]
    )
  }
)

scene("a failed load offers a retry", async ({ shop, page, signIn }) => {
  shop.addProducts({ name: "Kettle" })
  shop.failing = /^\/products/
  await signIn()

  await page.see(text(en.common.loadFailed))
  await page.gone(css("tbody tr"))

  shop.failing = undefined
  await page.click(button(en.common.retry))
  await page.see(text("Kettle"))
})
