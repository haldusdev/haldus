import assert from "node:assert/strict"

import { button, css, field, role, text } from "./support/chrome.ts"
import { en, scene, SESSION_KEY, tr } from "./support/harness.ts"

scene(
  "an unauthenticated visitor is sent to the sign-in form",
  async ({ shop, page }) => {
    await page.goto(`${shop.origin}/orders`)

    await page.waitForPath("/login")
    await page.see(field(en.login.email))
    assert.equal(shop.sent("GET", "/orders").length, 0)
  }
)

scene(
  "a wrong password shows the server's message and keeps the form",
  async ({ shop, page }) => {
    shop.addAccount("ada@example.com", "correct-horse-battery")
    await page.goto(`${shop.origin}/login`)

    await page.fill(field(en.login.email), "ada@example.com")
    await page.fill(field(en.login.password), "wrong-password")
    await page.click(button(en.login.submit))

    assert.equal(
      await page.textOf(role("alert")),
      "The e-mail address or password is incorrect."
    )
    assert.equal(await page.path(), "/login")
    assert.equal(await page.storage(SESSION_KEY), null)

    // Giriş isteği token taşımaz; gövdede yazılanlar gider.
    const login = shop.last("POST", "/auth/login")
    assert.equal(login.headers.authorization, undefined)
    assert.deepEqual(login.body, {
      email: "ada@example.com",
      password: "wrong-password",
    })
  }
)

scene(
  "signing in opens the session and lands on the products page",
  async ({ shop, page }) => {
    const account = shop.addAccount("ada@example.com", "correct-horse-battery")
    shop.addProducts({ name: "Kettle", price: 42.5 })
    await page.goto(`${shop.origin}/login`)

    await page.fill(field(en.login.email), "  ada@example.com ")
    await page.fill(field(en.login.password), "correct-horse-battery")
    await page.press("Enter")

    await page.waitForPath("/products")
    await page.see(text("Kettle"))

    // Hesabın kimliği girişte alınan token'la sorulur; sonraki istekler oturumun token'ını taşır.
    const me = shop.last("GET", "/auth/me")
    const stored = JSON.parse((await page.storage(SESSION_KEY)) ?? "{}") as {
      accessToken: string
      accountId: string
      email: string
    }
    assert.equal(me.headers.authorization, `Bearer ${stored.accessToken}`)
    assert.equal(stored.accountId, account.id)
    assert.equal(stored.email, "ada@example.com")
    assert.equal(
      shop.last("GET", "/products").headers.authorization,
      `Bearer ${stored.accessToken}`
    )
    // Adresin başındaki ve sonundaki boşluk gönderilmez.
    assert.deepEqual(shop.last("POST", "/auth/login").body, {
      email: "ada@example.com",
      password: "correct-horse-battery",
    })
  }
)

scene(
  "registration shows field errors under the field, then signs in",
  async ({ shop, page }) => {
    await page.goto(`${shop.origin}/login`)
    await page.click(button(en.login.switchToRegister))
    await page.see(text(en.login.registerTitle))

    await page.fill(field(en.login.email), "grace@example.com")
    await page.fill(field(en.login.password), "short")
    await page.click(button(en.login.registerSubmit))

    await page.see(text("The password must be at least 8 characters."))
    assert.equal(
      await page.attribute(field(en.login.password), "aria-invalid"),
      "true"
    )

    await page.fill(field(en.login.password), "a-long-enough-password")
    await page.click(button(en.login.registerSubmit))

    await page.waitForPath("/products")
    // Yeni hesabın e-postası doğrulanmamıştır: doğrulama şeridi görünür.
    await page.see(text(en.verify.title))
    assert.equal(shop.accounts[0].email, "grace@example.com")
  }
)

scene(
  "a taken e-mail address is reported from the server",
  async ({ shop, page }) => {
    shop.addAccount("ada@example.com", "correct-horse-battery")
    await page.goto(`${shop.origin}/login`)
    await page.click(button(en.login.switchToRegister))

    await page.fill(field(en.login.email), "ada@example.com")
    await page.fill(field(en.login.password), "another-long-password")
    await page.click(button(en.login.registerSubmit))

    assert.equal(
      await page.textOf(role("alert")),
      "This e-mail address is already registered."
    )
  }
)

scene(
  "a forgotten password is reset with the mailed code",
  async ({ shop, page }) => {
    const account = shop.addAccount("ada@example.com", "correct-horse-battery")
    await page.goto(`${shop.origin}/login`)

    await page.click(button(en.login.forgot))
    await page.see(text(en.login.forgotTitle))
    await page.gone(field(en.login.password))

    await page.fill(field(en.login.email), "ada@example.com")
    await page.click(button(en.login.forgotSubmit))

    await page.see(text(en.login.codeSent))
    await page.see(text(en.login.resetTitle))

    // Yanlış kod: backend'in açıklaması gösterilir, adım değişmez.
    await page.fill(field(en.login.code), "WRONGCOD")
    await page.fill(field(en.login.newPassword), "a-brand-new-password")
    await page.click(button(en.login.resetSubmit))
    assert.equal(
      await page.textOf(role("alert")),
      "The code is invalid or has expired."
    )

    // Kod küçük harfle ve tireyle yazılabilir.
    await page.fill(field(en.login.code), "k7m2-p9qx")
    await page.click(button(en.login.resetSubmit))

    await page.see(text(en.login.passwordReset))
    await page.see(text(en.login.title))
    assert.equal(account.password, "a-brand-new-password")
    assert.deepEqual(shop.last("POST", "/auth/password/reset").body, {
      email: "ada@example.com",
      code: "k7m2-p9qx",
      newPassword: "a-brand-new-password",
    })

    // Yeni parolayla giriş yapılır; e-posta alanı dolu kalmıştır.
    assert.equal(await page.valueOf(field(en.login.email)), "ada@example.com")
    await page.fill(field(en.login.password), "a-brand-new-password")
    await page.click(button(en.login.submit))
    await page.waitForPath("/products")
  }
)

scene(
  "signing out from the account menu revokes the session",
  async ({ shop, page, signIn }) => {
    await signIn()
    await page.see(text(en.products.title))

    await page.click(button(en.nav.userMenu))
    await page.click(role("menuitem", en.nav.signOut))

    await page.waitForPath("/login")
    assert.equal(await page.storage(SESSION_KEY), null)
    assert.equal(shop.sent("POST", "/auth/logout").length, 1)
    assert.match(
      shop.last("POST", "/auth/logout").headers.authorization ?? "",
      /^Bearer access-/
    )
  }
)

scene(
  "switching the language translates the page and the server's messages",
  async ({ shop, page, signIn }) => {
    await signIn("ada@example.com", { staff: false })
    await page.see(text(en.products.title))
    assert.equal(shop.last("GET", "/products").headers["accept-language"], "en")

    await page.click(button(en.nav.userMenu))
    await page.click(role("menuitemradio", en.languages.tr))

    await page.see(css("html[lang=tr]"))
    await page.press("Escape")
    await page.gone(role("menu"))
    await page.see(button(tr.products.new))

    // Personel olmayan hesap ortak kaynağı yazamaz; backend'in Türkçe açıklaması pencerede görünür.
    await page.click(button(tr.products.new))
    await page.fill(field(tr.products.name), "Çaydanlık")
    await page.fill(field(tr.products.price), "12")
    await page.click(button(tr.common.save))

    await page.see(text("Bu kaynağı yalnızca personel değiştirebilir."))
    assert.equal(
      shop.last("POST", "/products").headers["accept-language"],
      "tr"
    )
    assert.equal(shop.products.length, 0)
  }
)

scene(
  "the change-password dialog reports a wrong current password",
  async ({ shop, page, signIn }) => {
    await signIn()
    await page.click(button(en.nav.userMenu))
    await page.click(role("menuitem", en.nav.changePassword))
    await page.see(role("dialog", undefined))

    await page.fill(field(en.password.current), "not-my-password")
    await page.fill(field(en.password.new), "a-brand-new-password")
    await page.click(button(en.common.save))
    assert.equal(
      await page.textOf(role("alert")),
      "The e-mail address or password is incorrect."
    )

    await page.fill(field(en.password.current), "correct-horse-battery")
    await page.click(button(en.common.save))

    await page.see(text(en.password.changed))
    await page.gone(role("dialog", undefined))
    assert.equal(shop.accounts[0].password, "a-brand-new-password")
  }
)
