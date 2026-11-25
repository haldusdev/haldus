import assert from "node:assert/strict"

import { button, field, role, text } from "./support/chrome.ts"
import { en, scene, SESSION_KEY, until } from "./support/harness.ts"

type Stored = {
  accessToken: string
  refreshToken: string
  emailVerified: boolean
}

function parse(value: string | null): Stored {
  return JSON.parse(value ?? "{}") as Stored
}

scene(
  "an expired access token is renewed once and the requests are repeated",
  async ({ shop, page, signIn }) => {
    shop.addProducts({ name: "Kettle" })
    await signIn()
    await page.see(text("Kettle"))
    const before = parse(await page.storage(SESSION_KEY))

    shop.expireAccessTokens()
    // Sayfa yeniden açılınca ürünler ve kategoriler aynı anda istenir; ikisi de 401 alır.
    await page.goto(`${shop.origin}/products`)
    await page.see(text("Kettle"))

    assert.equal(shop.sent("POST", "/auth/refresh").length, 1)
    assert.deepEqual(shop.last("POST", "/auth/refresh").body, {
      refreshToken: before.refreshToken,
    })
    // Yenileme isteği token taşımaz.
    assert.equal(
      shop.last("POST", "/auth/refresh").headers.authorization,
      undefined
    )

    const after = parse(await page.storage(SESSION_KEY))
    assert.notEqual(after.accessToken, before.accessToken)
    assert.notEqual(after.refreshToken, before.refreshToken)
    assert.equal(
      shop.last("GET", "/products").headers.authorization,
      `Bearer ${after.accessToken}`
    )
    assert.equal(
      shop.last("GET", "/categories").headers.authorization,
      `Bearer ${after.accessToken}`
    )
  }
)

scene(
  "a rejected renewal signs the user out",
  async ({ shop, page, signIn }) => {
    await signIn()
    await page.see(text(en.products.empty))

    shop.revokeSessions()
    await page.click(button(en.nav.orders))

    await page.waitForPath("/login")
    await page.see(field(en.login.email))
    assert.equal(await page.storage(SESSION_KEY), null)
  }
)

scene(
  "a server error during renewal keeps the session",
  async ({ shop, page, signIn }) => {
    await signIn()
    await page.see(text(en.products.empty))
    const before = await page.storage(SESSION_KEY)

    shop.expireAccessTokens()
    shop.refreshBehaviour = "fail"
    await page.click(button(en.nav.orders))

    // İstek başarısız olur ama kullanıcı oturumdan atılmaz: geçici bir arıza herkesi çıkarmamalıdır.
    await page.see(text(en.common.loadFailed))
    assert.equal(await page.path(), "/orders")
    assert.equal(await page.storage(SESSION_KEY), before)

    shop.refreshBehaviour = "rotate"
    await page.click(button(en.common.retry))
    await page.see(text(en.orders.empty))
  }
)

scene(
  "a renewal answered with 409 adopts the tokens another tab stored",
  async ({ shop, page, signIn }) => {
    shop.addOrder("Ada Lovelace", "Draft")
    await signIn()
    await page.see(text(en.products.empty))
    const before = parse(await page.storage(SESSION_KEY))

    shop.expireAccessTokens()
    shop.refreshBehaviour = "renewedElsewhere"
    await page.click(button(en.nav.orders))
    await until(
      () => shop.sent("POST", "/auth/refresh").length === 1,
      "the renewal request"
    )

    // Öteki sekmenin yaptığı: yeni token'ları depoya yazar. Bu sekme onları bekleyip alır.
    const renewed = shop.issue(shop.accounts[0])
    await page.setStorage(
      SESSION_KEY,
      JSON.stringify({
        ...JSON.parse((await page.storage(SESSION_KEY)) ?? "{}"),
        accessToken: renewed.accessToken,
        refreshToken: renewed.refreshToken,
      })
    )

    await page.see(text("Ada Lovelace"))
    assert.equal(shop.sent("POST", "/auth/refresh").length, 1)
    assert.equal(
      shop.last("GET", "/orders").headers.authorization,
      `Bearer ${renewed.accessToken}`
    )
    assert.notEqual(renewed.accessToken, before.accessToken)
  }
)

scene(
  "a 409 with no new tokens fails the request but does not sign out",
  async ({ shop, page, signIn }) => {
    await signIn()
    await page.see(text(en.products.empty))
    const before = await page.storage(SESSION_KEY)

    shop.expireAccessTokens()
    shop.refreshBehaviour = "renewedElsewhere"
    await page.click(button(en.nav.orders))

    await page.see(text(en.common.loadFailed))
    assert.equal(await page.path(), "/orders")
    assert.equal(await page.storage(SESSION_KEY), before)
  }
)

scene("two tabs share one renewal", async ({ shop, context, page, signIn }) => {
  shop.addProducts({ name: "Kettle" })
  const second = await context.page()
  await signIn()
  await second.goto(`${shop.origin}/products`)
  await page.see(text("Kettle"))
  await second.see(text("Kettle"))

  shop.expireAccessTokens()
  await page.click(button(en.nav.orders))
  await page.see(text(en.orders.empty))

  // İkinci sekme yenilenen token'ları depodan alır; kendisi yenilemez (eskisini sunsaydı oturum kapanırdı).
  await second.click(button(en.nav.categories))
  await second.see(text(en.categories.empty))

  assert.equal(shop.sent("POST", "/auth/refresh").length, 1)
  assert.equal(
    await second.storage(SESSION_KEY),
    await page.storage(SESSION_KEY)
  )
})

scene(
  "signing out in one tab signs the other tab out",
  async ({ shop, context, page, signIn }) => {
    const second = await context.page()
    await signIn()
    await second.goto(`${shop.origin}/orders`)
    await second.see(text(en.orders.empty))

    await page.click(button(en.nav.userMenu))
    await page.click(role("menuitem", en.nav.signOut))
    await page.waitForPath("/login")

    await second.waitForPath("/login")
    await second.see(field(en.login.email))
  }
)

scene(
  "the verification banner verifies the address with the mailed code",
  async ({ shop, page, signIn, fill }) => {
    await signIn("grace@example.com", { verified: false, staff: false })
    await page.see(text(en.verify.title))
    await page.see(
      text(fill(en.verify.description, { email: "grace@example.com" }))
    )
    const before = parse(await page.storage(SESSION_KEY))

    await page.fill(field(en.verify.code), "WRONGCOD")
    await page.click(button(en.verify.submit))
    assert.equal(
      await page.textOf(role("alert")),
      "The code is invalid or has expired."
    )
    assert.equal(
      await page.attribute(field(en.verify.code), "aria-invalid"),
      "true"
    )

    await page.click(button(en.verify.resend))
    await page.see(text(en.verify.resent))
    assert.equal(shop.sent("POST", "/auth/email/resend").length, 1)

    await page.fill(field(en.verify.code), "k7m2-p9qx")
    await page.press("Enter")

    await page.see(text(en.verify.done))
    await page.gone(text(en.verify.title))

    // Doğrulama yeni token'lar verir (token'daki "doğrulandı" bilgisi değişir); oturum onlarla güncellenir.
    const after = parse(await page.storage(SESSION_KEY))
    assert.equal(after.emailVerified, true)
    assert.notEqual(after.accessToken, before.accessToken)
    assert.equal(shop.accounts[0].emailVerified, true)

    // Şerit sayfa yeniden açılınca da görünmez.
    await page.goto(`${shop.origin}/products`)
    await page.see(text(en.products.title))
    assert.equal(await page.exists(text(en.verify.title)), false)
  }
)
