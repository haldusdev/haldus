import assert from "node:assert/strict"

import { button, field, role, text } from "./support/chrome.ts"
import type { Page } from "./support/chrome.ts"
import {
  en,
  LANGUAGE_KEY,
  scene,
  SESSION_KEY,
  tr,
  until,
  type Scene,
} from "./support/harness.ts"
import {
  CALLBACK_PATH,
  CLIENT_ID,
  SCOPES,
} from "./support/identity-provider.ts"

// Panel gerçek `oidc-client-ts` ile çalışır; karşısındaki kimlik sağlayıcı sahte mağazanın `/idp` altında sunduğu
// taklittir (support/identity-provider.ts). Gerçek bir sağlayıcıya karşı sınanmış sayılmaz.

type Stored = {
  kind?: string
  accessToken: string
  refreshToken: string
  accountId: string
  authority?: string
  clientId?: string
}

async function stored(page: Page): Promise<Stored> {
  return JSON.parse((await page.storage(SESSION_KEY)) ?? "{}") as Stored
}

/** `oidc-client-ts`'in tarayıcı depolarına yazdığı anahtarlar (`oidc.` önekiyle). */
function libraryKeys(page: Page): Promise<string[]> {
  return page.evaluate<string[]>(
    `[...Object.keys(localStorage), ...Object.keys(sessionStorage)].filter((key) => key.startsWith("oidc."))`
  )
}

/** Giriş ekranından sağlayıcıya gidip döner; gerçek yönlendirmelerle. */
async function signInWithProvider(
  { shop, page }: Scene,
  options: { verified?: boolean; staff?: boolean } = {}
) {
  const account =
    shop.accounts[0] ??
    shop.addAccount("ada@example.com", "correct-horse-battery", {
      emailVerified: options.verified ?? true,
      isStaff: options.staff ?? true,
    })

  shop.offerProvider(account)
  await page.goto(`${shop.origin}/login`)
  await page.click(button(en.login.providerSignIn))
  await page.waitForPath("/products")
  await page.see(text(en.products.title))

  return account
}

scene(
  "the provider button is absent unless the backend offers a provider",
  async ({ shop, page }) => {
    await page.goto(`${shop.origin}/login`)
    await page.see(field(en.login.email))

    // Düğme yapılandırma yanıtından sonra çizilirdi; yanıt gelip sayfa yeniden çizilene kadar beklenir.
    await until(
      () => shop.sent("GET", "/auth/config").length === 1,
      "the configuration request"
    )
    await page.evaluate(
      "new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))"
    )

    assert.equal(await page.exists(button(en.login.providerSignIn)), false)
    assert.equal(await page.exists(text(en.login.providerOr)), false)
    // Yapılandırma isteği token taşımaz.
    assert.equal(
      shop.last("GET", "/auth/config").headers.authorization,
      undefined
    )
    assert.equal(shop.idp.requests.length, 0)
  }
)

scene(
  "the password form works when the configuration cannot be loaded",
  async ({ shop, page }) => {
    shop.addAccount("ada@example.com", "correct-horse-battery")
    shop.offerProvider(shop.accounts[0])
    shop.failing = /^\/auth\/config$/
    await page.goto(`${shop.origin}/login`)

    await page.fill(field(en.login.email), "ada@example.com")
    await page.fill(field(en.login.password), "correct-horse-battery")
    assert.equal(await page.exists(button(en.login.providerSignIn)), false)
    await page.click(button(en.login.submit))

    await page.waitForPath("/products")
    assert.equal((await stored(page)).kind, "password")
  }
)

scene(
  "signing in with the provider redirects there and back, then uses its token",
  async (scene) => {
    const { shop, page } = scene
    shop.addProducts({ name: "Kettle" })
    const account = await signInWithProvider(scene)
    await page.see(text("Kettle"))

    // Yetkilendirme isteği: kod akışı, PKCE (S256), backend'in tanıttığı istemci ve kapsam, panelin dönüş adresi.
    const authorize = shop.idp.sent("GET", "/authorize")
    assert.equal(authorize.length, 1)
    const query = authorize[0].query
    assert.equal(query.get("client_id"), CLIENT_ID)
    assert.equal(query.get("response_type"), "code")
    assert.equal(query.get("scope"), SCOPES)
    assert.equal(query.get("redirect_uri"), `${shop.origin}${CALLBACK_PATH}`)
    assert.equal(query.get("code_challenge_method"), "S256")
    assert.match(query.get("code_challenge") ?? "", /^[\w-]{43}$/)
    assert.match(query.get("state") ?? "", /^[0-9a-f]{32}$/)

    // Kod, doğrulayıcısıyla ve gizli anahtar olmadan token'a çevrilir (sağlayıcı doğrulayıcıyı denetledi).
    const exchange = shop.idp.grants("authorization_code")
    assert.equal(exchange.length, 1)
    assert.equal(exchange[0].form.get("client_id"), CLIENT_ID)
    assert.equal(exchange[0].form.get("client_secret"), null)
    assert.match(exchange[0].form.get("code_verifier") ?? "", /^[\w-]{43,128}$/)
    assert.equal(exchange[0].headers.authorization, undefined)

    // Oturum sağlayıcının token'larıyla ve backend'in tanıttığı hesapla yazılır.
    const session = await stored(page)
    assert.equal(session.kind, "provider")
    assert.match(session.accessToken, /^idp-access-\d+$/)
    assert.match(session.refreshToken, /^idp-refresh-\d+$/)
    assert.equal(session.accountId, account.id)
    assert.equal(session.authority, shop.idp.authority)
    assert.equal(session.clientId, CLIENT_ID)

    // Hesap sağlayıcının token'ıyla sorulur; sonraki istekler de onu taşır.
    assert.equal(
      shop.last("GET", "/auth/me").headers.authorization,
      `Bearer ${session.accessToken}`
    )
    assert.equal(
      shop.last("GET", "/products").headers.authorization,
      `Bearer ${session.accessToken}`
    )

    // Backend'in parola uçlarına gidilmez; kitaplık da keşif, yetkilendirme ve token dışında bir uca gitmez.
    assert.equal(shop.sent("POST", "/auth/login").length, 0)
    assert.equal(shop.sent("POST", "/auth/refresh").length, 0)
    assert.deepEqual(
      shop.idp.requests.map((request) => `${request.method} ${request.path}`),
      [
        "GET /.well-known/openid-configuration",
        "GET /authorize",
        // Dönüş sayfası yeni bir belgedir; keşif belgesi yeniden okunur.
        "GET /.well-known/openid-configuration",
        "POST /token",
      ]
    )

    // Yönlendirme için saklanan `state` ve doğrulayıcı kullanılınca silinir; kitaplık kullanıcıyı depoya yazmaz.
    assert.deepEqual(await libraryKeys(page), [])
    // Kod adreste kalmaz.
    assert.equal(await page.evaluate<string>("location.search"), "")
  }
)

scene(
  "the provider sign-in returns to the page the visitor asked for",
  async ({ shop, page }) => {
    const account = shop.addAccount("ada@example.com", "correct-horse-battery")
    shop.addOrder("Ada Lovelace", "Draft")
    shop.offerProvider(account)

    await page.goto(`${shop.origin}/orders`)
    await page.waitForPath("/login")
    await page.click(button(en.login.providerSignIn))

    await page.waitForPath("/orders")
    await page.see(text("Ada Lovelace"))
    // Dönüş adresi sağlayıcıya gönderilmez; `state` kaydının yanında, sekmenin deposunda bekler.
    assert.equal(
      shop.idp.sent("GET", "/authorize")[0].query.get("state")?.includes("/"),
      false
    )
  }
)

scene(
  "the provider button is translated and offered while registering",
  async ({ shop, page }) => {
    shop.offerProvider()
    await page.goto(`${shop.origin}/login`)
    await page.see(button(en.login.providerSignIn))
    await page.see(text(en.login.providerOr))

    await page.click(button(en.login.switchToRegister))
    await page.see(button(en.login.providerSignIn))

    // Parola sıfırlama adımlarında gösterilmez.
    await page.click(button(en.login.switchToSignIn))
    await page.click(button(en.login.forgot))
    await page.see(text(en.login.forgotTitle))
    assert.equal(await page.exists(button(en.login.providerSignIn)), false)

    await page.setStorage(LANGUAGE_KEY, "tr")
    await page.goto(`${shop.origin}/login`)
    await page.see(button(tr.login.providerSignIn))
    await page.see(text(tr.login.providerOr))
  }
)

scene(
  "password sign-in still works when a provider is offered",
  async ({ shop, page }) => {
    const account = shop.addAccount("ada@example.com", "correct-horse-battery")
    shop.addProducts({ name: "Kettle" })
    shop.offerProvider(account)
    await page.goto(`${shop.origin}/login`)
    await page.see(button(en.login.providerSignIn))

    await page.fill(field(en.login.email), "ada@example.com")
    await page.fill(field(en.login.password), "correct-horse-battery")
    await page.click(button(en.login.submit))

    await page.waitForPath("/products")
    await page.see(text("Kettle"))

    const session = await stored(page)
    assert.equal(session.kind, "password")
    assert.match(session.accessToken, /^access-\d+$/)
    assert.equal(shop.sent("POST", "/auth/login").length, 1)
    assert.equal(shop.idp.requests.length, 0)

    // Parola oturumu backend'den yenilenir, sağlayıcıdan değil.
    shop.expireAccessTokens()
    await page.click(button(en.nav.orders))
    await page.see(text(en.orders.empty))
    assert.equal(shop.sent("POST", "/auth/refresh").length, 1)
    assert.equal(shop.idp.requests.length, 0)

    // Parola hesabının menüsünde parola değiştirme durur.
    await page.click(button(en.nav.userMenu))
    await page.see(role("menuitem", en.nav.changePassword))
  }
)

scene(
  "an expired provider token is renewed once through the provider and the requests are repeated",
  async (scene) => {
    const { shop, page } = scene
    shop.addProducts({ name: "Kettle" })
    await signInWithProvider(scene)
    const before = await stored(page)
    const earlier = shop.idp.requests.length

    shop.expireAccessTokens()
    // Sayfa yeniden açılınca ürünler ve kategoriler aynı anda istenir; ikisi de 401 alır.
    await page.goto(`${shop.origin}/products`)
    await page.see(text("Kettle"))

    // Tek bir yenileme, sağlayıcının token ucuna: yenileme token'ı ve istemci kimliği gider, başka bir şey gitmez.
    const renewals = shop.idp.grants("refresh_token")
    assert.equal(renewals.length, 1)
    assert.deepEqual(Object.fromEntries(renewals[0].form), {
      grant_type: "refresh_token",
      refresh_token: before.refreshToken,
      client_id: CLIENT_ID,
    })
    assert.equal(renewals[0].headers.authorization, undefined)
    assert.deepEqual(
      shop.idp.requests
        .slice(earlier)
        .map((request) => `${request.method} ${request.path}`),
      ["GET /.well-known/openid-configuration", "POST /token"]
    )
    // Backend'in yenileme ucu sağlayıcı oturumu için kullanılmaz.
    assert.equal(shop.sent("POST", "/auth/refresh").length, 0)

    const after = await stored(page)
    assert.equal(after.kind, "provider")
    assert.notEqual(after.accessToken, before.accessToken)
    assert.notEqual(after.refreshToken, before.refreshToken)
    assert.equal(after.accountId, before.accountId)
    assert.equal(
      shop.last("GET", "/products").headers.authorization,
      `Bearer ${after.accessToken}`
    )
    assert.equal(
      shop.last("GET", "/categories").headers.authorization,
      `Bearer ${after.accessToken}`
    )
    assert.deepEqual(await libraryKeys(page), [])

    // Yenilenen token'lar da yenilenebilir: ikinci yenileme, birincinin verdiği yenileme token'ını sunar.
    shop.expireAccessTokens()
    await page.click(button(en.nav.orders))
    await page.see(text(en.orders.empty))
    assert.equal(shop.idp.grants("refresh_token").length, 2)
    assert.equal(
      shop.idp.grants("refresh_token")[1].form.get("refresh_token"),
      after.refreshToken
    )
  }
)

scene(
  "a renewal the provider rejects returns to the sign-in screen",
  async (scene) => {
    const { shop, page } = scene
    await signInWithProvider(scene)

    shop.revokeSessions()
    await page.click(button(en.nav.orders))

    await page.waitForPath("/login")
    await page.see(button(en.login.providerSignIn))
    assert.equal(await page.storage(SESSION_KEY), null)
    assert.equal(shop.idp.grants("refresh_token").length, 1)
    assert.equal(shop.sent("POST", "/auth/refresh").length, 0)
    assert.equal(shop.sent("POST", "/auth/logout").length, 0)
    assert.deepEqual(await libraryKeys(page), [])
  }
)

scene("a provider outage during renewal keeps the session", async (scene) => {
  const { shop, page } = scene
  await signInWithProvider(scene)
  const before = await page.storage(SESSION_KEY)

  shop.expireAccessTokens()
  shop.idp.refreshBehaviour = "fail"
  await page.click(button(en.nav.orders))

  // İstek başarısız olur ama kullanıcı oturumdan atılmaz.
  await page.see(text(en.common.loadFailed))
  assert.equal(await page.path(), "/orders")
  assert.equal(await page.storage(SESSION_KEY), before)

  shop.idp.refreshBehaviour = "rotate"
  await page.click(button(en.common.retry))
  await page.see(text(en.orders.empty))
  assert.notEqual(await page.storage(SESSION_KEY), before)
})

scene(
  "a provider session without a refresh token ends when the access token is rejected",
  async (scene) => {
    const { shop, page } = scene
    shop.idp.issuesRefreshTokens = false
    await signInWithProvider(scene)
    assert.equal((await stored(page)).refreshToken, "")

    shop.expireAccessTokens()
    await page.click(button(en.nav.orders))

    await page.waitForPath("/login")
    assert.equal(await page.storage(SESSION_KEY), null)
    // Yenilenecek bir şey yoktur: sağlayıcıya istek gitmez, gizli bir iframe de açılmaz.
    assert.equal(shop.idp.grants("refresh_token").length, 0)
    assert.equal(shop.idp.sent("GET", "/authorize").length, 1)
    assert.equal(
      await page.evaluate<number>("document.querySelectorAll('iframe').length"),
      0
    )
  }
)

scene("two tabs share one provider renewal", async (scene) => {
  const { shop, context, page } = scene
  shop.addProducts({ name: "Kettle" })
  await signInWithProvider(scene)
  const second = await context.page()
  await second.goto(`${shop.origin}/products`)
  await page.see(text("Kettle"))
  await second.see(text("Kettle"))

  shop.expireAccessTokens()
  await page.click(button(en.nav.orders))
  await page.see(text(en.orders.empty))

  // İkinci sekme yenilenen token'ları depodan alır; kendisi yenilemez (sağlayıcı eskisini reddederdi).
  await second.click(button(en.nav.categories))
  await second.see(text(en.categories.empty))

  assert.equal(shop.idp.grants("refresh_token").length, 1)
  assert.equal(
    await second.storage(SESSION_KEY),
    await page.storage(SESSION_KEY)
  )
})

scene(
  "a provider error on the way back is shown with a way back to the sign-in screen",
  async ({ shop, page }) => {
    shop.offerProvider(shop.addAccount("ada@example.com", "correct-horse"))
    shop.idp.authorizeBehaviour = "deny"
    await page.goto(`${shop.origin}/login`)
    await page.click(button(en.login.providerSignIn))

    await page.waitForPath(CALLBACK_PATH)
    assert.equal(await page.textOf(role("alert")), en.login.callbackDenied)
    await page.see(text(en.login.callbackFailedTitle))
    assert.equal(await page.storage(SESSION_KEY), null)
    // Kod yoktur: token ucuna gidilmez, backend'e hesap sorulmaz.
    assert.equal(shop.idp.sent("POST", "/token").length, 0)
    assert.equal(shop.sent("GET", "/auth/me").length, 0)

    await page.click(button(en.login.backToSignIn))
    await page.waitForPath("/login")
    await page.see(field(en.login.email))
    await page.see(button(en.login.providerSignIn))
    assert.deepEqual(await libraryKeys(page), [])
  }
)

scene(
  "a callback whose state this tab never issued is refused before the code is used",
  async ({ shop, page }) => {
    shop.offerProvider(shop.addAccount("ada@example.com", "correct-horse"))

    // Başkasının hazırladığı bir bağlantı: kod geçerli olsa bile bu sekmenin başlattığı bir girişe ait değildir.
    await page.goto(
      `${shop.origin}${CALLBACK_PATH}?code=code-1&state=0123456789abcdef0123456789abcdef`
    )

    assert.equal(await page.textOf(role("alert")), en.login.callbackFailed)
    assert.equal(await page.storage(SESSION_KEY), null)
    assert.equal(shop.idp.sent("POST", "/token").length, 0)
    assert.equal(shop.sent("GET", "/auth/me").length, 0)
  }
)

scene(
  "a provider sign-in the backend does not accept opens no session",
  async ({ shop, page }) => {
    // Sağlayıcı girişi tamamlar ama mağaza o kullanıcıyı tanımaz: token'ı 401 ile reddeder.
    shop.offerProvider()
    await page.goto(`${shop.origin}/login`)
    await page.click(button(en.login.providerSignIn))

    await page.waitForPath(CALLBACK_PATH)
    assert.equal(await page.textOf(role("alert")), en.login.callbackNotAccepted)
    assert.equal(await page.storage(SESSION_KEY), null)
    assert.equal(shop.sent("GET", "/auth/me").length, 1)
    assert.match(
      shop.last("GET", "/auth/me").headers.authorization ?? "",
      /^Bearer idp-access-\d+$/
    )
    // Reddedilen token'lar hiçbir depoda kalmaz.
    assert.deepEqual(await libraryKeys(page), [])
    assert.equal(
      await page.evaluate<boolean>(
        `JSON.stringify([{...localStorage}, {...sessionStorage}]).includes("idp-")`
      ),
      false
    )
  }
)

scene(
  "an unreachable provider is reported on the sign-in screen",
  async ({ shop, page }) => {
    shop.offerProvider()
    // Keşif belgesi okunamaz: backend'in tanıttığı adres yanıt vermiyor.
    shop.config = {
      ...shop.config,
      provider: {
        authority: `${shop.origin}/idp/missing`,
        clientId: CLIENT_ID,
        scopes: SCOPES,
      },
    }
    await page.goto(`${shop.origin}/login`)
    await page.click(button(en.login.providerSignIn))

    assert.equal(await page.textOf(role("alert")), en.login.providerUnavailable)
    assert.equal(await page.path(), "/login")
    // Düğme yeniden kullanılabilir; parola formu da yerindedir.
    await page.see(button(en.login.providerSignIn))
    await page.see(field(en.login.password))
  }
)

scene(
  "signing out of a provider session clears it locally and calls nobody",
  async (scene) => {
    const { shop, page } = scene
    await signInWithProvider(scene)
    const session = await stored(page)
    const earlier = shop.idp.requests.length

    await page.click(button(en.nav.userMenu))
    await page.click(role("menuitem", en.nav.signOut))

    await page.waitForPath("/login")
    assert.equal(await page.storage(SESSION_KEY), null)
    assert.deepEqual(await libraryKeys(page), [])
    assert.equal(
      await page.evaluate<boolean>(
        `JSON.stringify([{...localStorage}, {...sessionStorage}]).includes("idp-")`
      ),
      false
    )
    // Backend'de kapatılacak bir oturum yoktur; sağlayıcının çıkış ucu da çağrılmaz (bilinen sınır).
    assert.equal(shop.sent("POST", "/auth/logout").length, 0)
    assert.equal(shop.idp.requests.length, earlier)
    // Sınırın sonucu: token sağlayıcıda geçerli kalır.
    assert.equal(shop.idp.subjectOf(session.accessToken), session.accountId)

    // Çıkıştan sonra korunan bir sayfa yeniden giriş ister.
    await page.goto(`${shop.origin}/orders`)
    await page.waitForPath("/login")
  }
)

scene(
  "a provider account is not offered the password change or the e-mail verification",
  async (scene) => {
    const { shop, page } = scene
    // Hesabın e-postası backend'de doğrulanmamış görünse de şerit çıkmaz: kod uçları parola hesapları içindir.
    await signInWithProvider(scene, { verified: false, staff: false })
    await page.see(text(en.products.empty))
    assert.equal(await page.exists(text(en.verify.title)), false)

    await page.click(button(en.nav.userMenu))
    await page.see(role("menuitem", en.nav.signOut))
    assert.equal(
      await page.exists(role("menuitem", en.nav.changePassword)),
      false
    )
    assert.equal(shop.sent("POST", "/auth/email/resend").length, 0)
  }
)
