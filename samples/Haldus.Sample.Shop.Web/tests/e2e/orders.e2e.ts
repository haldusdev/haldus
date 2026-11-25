import assert from "node:assert/strict"

import { button, css, field, role, text } from "./support/chrome.ts"
import { en, scene } from "./support/harness.ts"

const customers = `[...document.querySelectorAll("tbody tr")].map((row) => row.querySelector("td").innerText.trim())`
const badge = css("h1 ~ * [data-slot=badge], [data-slot=badge]")

scene(
  "the order list filters by status and opens an order",
  async ({ shop, page, signIn, fill }) => {
    shop.addOrder("Ada Lovelace", "Draft", [["Kettle", 2, 10]])
    shop.addOrder("Grace Hopper", "Placed", [["Toaster", 1, 30]])
    shop.addOrder("Alan Turing", "Cancelled")
    await signIn("ada@example.com", { path: "/orders" })

    await page.see(text("Grace Hopper"))
    assert.equal((await page.evaluate<string[]>(customers)).length, 3)
    assert.equal(shop.last("GET", "/orders").query.get("status"), null)

    await page.click(field(en.orders.filterByStatus))
    await page.click(role("option", en.orders.status.Placed))
    await page.waitFor(`${customers}.length === 1`, "the filter to apply")
    assert.deepEqual(await page.evaluate(customers), ["Grace Hopper"])
    assert.equal(shop.last("GET", "/orders").query.get("status"), "Placed")

    await page.click(field(en.orders.filterByStatus))
    await page.click(role("option", en.orders.status.Shipped))
    await page.see(text(en.orders.emptyFiltered))

    await page.click(field(en.orders.filterByStatus))
    await page.click(role("option", en.orders.allStatuses))
    await page.click(button(fill(en.orders.open, { customer: "Ada Lovelace" })))

    await page.waitForPath(`/orders/${shop.orders[0].id}`)
    await page.see(text("Kettle"))
    await page.see(button(en.orders.detail.place))
    // Taslak gönderilemez: yalnızca durumunun izin verdiği işlemler sunulur.
    assert.equal(await page.exists(button(en.orders.detail.ship)), false)
  }
)

scene(
  "creating an order sends its lines once, with an idempotency key",
  async ({ shop, page, signIn, fill }) => {
    await signIn("ada@example.com", { path: "/orders" })
    await page.see(text(en.orders.empty))

    await page.click(button(en.orders.new))
    await page.see(role("dialog", en.orders.new))

    // Müşteri adı olmadan: backend'in alan hatası gösterilir.
    await page.click(button(en.common.create))
    await page.see(text("The CustomerName field is required."))

    await page.fill(field(en.orders.form.customerName), "Ada Lovelace")
    await page.fill(field(en.orders.form.product), "Kettle")
    await page.fill(field(en.orders.form.quantity), "2")
    await page.fill(field(en.orders.form.unitPrice), "12.5")

    // İkinci satır eklenir, doldurulur; üçüncü eklenip kaldırılır.
    await page.click(button(en.orders.form.addLine))
    await page.click(button(en.orders.form.addLine))
    await page.click(button(fill(en.orders.form.removeLine, { index: 3 })))
    // Sonraki satırların etiketleri yalnızca ekran okuyucuya görünür ve ilk satırınkilerle aynıdır; alanlar sırayla
    // bulunur: müşteri, sonra satır başına ürün, adet, fiyat.
    const inputs = `[...document.querySelectorAll("[role=dialog] input")]`
    assert.equal(await page.evaluate(`${inputs}.length`), 1 + 2 * 3)
    await page.fill(
      { expression: `${inputs}[4]`, description: "the second line's product" },
      "Toaster"
    )
    await page.fill(
      { expression: `${inputs}[6]`, description: "the second line's price" },
      "30"
    )

    await page.click(button(en.common.create))

    await page.see(text(en.orders.form.createdToast))
    await page.waitForPath(`/orders/${shop.orders[0].id}`)
    await page.see(text("Toaster"))

    const created = shop.sent("POST", "/orders")
    // İlk (reddedilen) ve ikinci gönderim aynı anahtarı taşır: aynı pencereden yinelenen istek tek sipariş oluşturur.
    assert.equal(created.length, 2)
    assert.match(
      String(created[0].headers["idempotency-key"]),
      /^[0-9a-f-]{36}$/
    )
    assert.equal(
      created[0].headers["idempotency-key"],
      created[1].headers["idempotency-key"]
    )
    assert.deepEqual(created[1].body, {
      customerName: "Ada Lovelace",
      orderItems: [
        { productName: "Kettle", quantity: 2, unitPrice: 12.5 },
        { productName: "Toaster", quantity: 1, unitPrice: 30 },
      ],
    })
    assert.equal(shop.orders.length, 1)
  }
)

scene(
  "an order walks through its workflow with the version it read",
  async ({ shop, page, signIn }) => {
    const order = shop.addOrder("Ada Lovelace", "Draft", [["Kettle", 2, 10]])
    await signIn("ada@example.com", { path: `/orders/${order.id}` })
    await page.see(text("Ada Lovelace"))
    assert.equal(await page.textOf(badge), en.orders.status.Draft)

    // Vermek: isteğe bağlı notla.
    await page.click(button(en.orders.detail.place))
    await page.see(role("dialog", en.orders.detail.place))
    await page.fill(field(en.orders.detail.note), "Leave at the door")
    await page.click(css("[role=dialog] button[type=submit]"))

    await page.see(text(en.orders.detail.placed))
    await page.waitFor(
      `document.querySelector("[data-slot=badge]").textContent.trim() === ${JSON.stringify(en.orders.status.Placed)}`,
      "the status to become Placed"
    )
    await page.see(text("Leave at the door"))
    const place = shop.last("POST", `/orders/${order.id}/place`)
    assert.deepEqual(place.body, { note: "Leave at the door" })
    assert.equal(place.headers["if-match"], '"1"')

    // Göndermek: penceresizdir ve az önce okunan yeni sürümü taşır.
    await page.click(button(en.orders.detail.ship))
    await page.see(text(en.orders.detail.shipped))
    assert.equal(
      shop.last("POST", `/orders/${order.id}/ship`).headers["if-match"],
      '"2"'
    )
    await page.gone(button(en.orders.detail.ship))
    // Gönderilmiş sipariş iptal edilemez ve silinemez.
    assert.equal(await page.exists(button(en.orders.detail.cancel)), false)
    assert.equal(await page.exists(button(en.orders.detail.delete)), false)
    assert.equal(order.status, "Shipped")
  }
)

scene(
  "cancelling needs a reason; a cancelled order can be reopened or deleted",
  async ({ shop, page, signIn }) => {
    const order = shop.addOrder("Ada Lovelace", "Placed", [["Kettle", 1, 10]])
    await signIn("ada@example.com", { path: `/orders/${order.id}` })

    await page.click(button(en.orders.detail.cancel))
    await page.see(role("dialog", en.orders.detail.cancel))
    await page.click(css("[role=dialog] button[type=submit]"))
    await page.see(text("The Reason field is required."))
    assert.equal(order.status, "Placed")

    await page.fill(field(en.orders.detail.reason), "Out of stock")
    await page.click(css("[role=dialog] button[type=submit]"))

    await page.see(text(en.orders.detail.cancelled))
    await page.see(text("Out of stock"))
    assert.deepEqual(shop.last("POST", `/orders/${order.id}/cancel`).body, {
      reason: "Out of stock",
    })

    await page.click(button(en.orders.detail.reopen))
    await page.see(text(en.orders.detail.reopened))
    await page.see(button(en.orders.detail.place))
    assert.deepEqual(shop.last("POST", `/orders/${order.id}/reopen`).body, {})

    await page.click(button(en.orders.detail.delete))
    await page.see(role("alertdialog", undefined))
    await page.click(button(en.common.delete))

    await page.see(text(en.orders.detail.deleted))
    await page.waitForPath("/orders")
    await page.see(text(en.orders.empty))
    assert.equal(shop.orders.length, 0)
  }
)

scene(
  "a change made elsewhere reloads the order instead of overwriting it",
  async ({ shop, page, signIn }) => {
    const order = shop.addOrder("Ada Lovelace", "Placed", [["Kettle", 1, 10]])
    await signIn("ada@example.com", { path: `/orders/${order.id}` })
    await page.see(button(en.orders.detail.ship))

    // Başka biri kaydı değiştirdi: panelin elindeki sürüm eskidi.
    order.placementNote = "Changed by someone else"
    shop.touch(order.id)
    await page.click(button(en.orders.detail.ship))

    await page.see(text(en.common.changedElsewhere))
    assert.equal(order.status, "Placed")

    // Kayıt yeniden okunur (ötekinin değişikliği görünür); ikinci deneme güncel sürümle geçer.
    await page.see(text("Changed by someone else"))
    await page.click(button(en.orders.detail.ship))
    await page.see(text(en.orders.detail.shipped))
    assert.equal(
      shop.last("POST", `/orders/${order.id}/ship`).headers["if-match"],
      '"2"'
    )
  }
)

scene(
  "an order from another tenant reads as not found",
  async ({ shop, page, signIn }) => {
    await signIn("ada@example.com", { path: "/orders/does-not-exist" })

    await page.see(text(en.orders.detail.notFound))
    await page.click(button(en.orders.detail.back))
    await page.waitForPath("/orders")
    // 404 yinelenince düzelmez; istek bir kez gönderilir.
    assert.equal(shop.sent("GET", "/orders/does-not-exist").length, 1)
  }
)
