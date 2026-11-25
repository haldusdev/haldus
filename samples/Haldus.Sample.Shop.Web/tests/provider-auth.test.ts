import assert from "node:assert/strict"
import { beforeEach, describe, it } from "node:test"

import { storage } from "./support/browser.ts"
import { calls, callsTo, json, respondWith } from "./support/fetch.ts"
import type { Call } from "./support/fetch.ts"
import {
  providerSession,
  session,
  STORAGE_KEY,
  unauthorized,
} from "./support/fixtures.ts"
import { ErrorResponse, provider, User } from "./support/provider.ts"

const { closeSession, finishProviderSignIn } = await import("@/lib/auth")
const { isProviderError, startProviderSignIn } = await import("@/lib/provider")
const { getSession, signIn, signOut } = await import("@/lib/session")

const offered = {
  authority: "https://id.example.com",
  clientId: "shop-panel",
  scopes: "openid profile offline_access",
}
const account = {
  id: "account-1",
  email: "ada@example.com",
  tenantId: "tenant-1",
  emailVerified: true,
  isStaff: false,
}

let serial = 0

/** Her test kendi dönüş adresini kullanır: aynı adres ikinci kez işlenmez. */
function callbackUrl(): string {
  return `https://panel.example/login/callback?code=code-${++serial}&state=state-${serial}`
}

function signedIn(overrides: { refresh?: string; state?: unknown } = {}) {
  return new User({
    access_token: "idp-access-1",
    refresh_token: "refresh" in overrides ? overrides.refresh : "idp-refresh-1",
    token_type: "Bearer",
    id_token: "header.claims.signature",
    profile: { sub: "subject-1", email: "ada@id.example.com" },
    userState: "state" in overrides ? overrides.state : { returnTo: "/orders" },
  })
}

function bearer(call: Call): string | null {
  return call.headers.get("Authorization")
}

/** Örnek backend gibi: sağlayıcıyı tanıtır ve onun token'ı için hesabı söyler. */
function backend(call: Call): Response {
  if (call.url.endsWith("/auth/config")) {
    return json(200, {
      passwordSignIn: true,
      provider: offered,
      serverSignIn: false,
    })
  }

  return call.url.endsWith("/auth/me") && bearer(call) === "Bearer idp-access-1"
    ? json(200, account)
    : json(401, unauthorized)
}

describe("provider sign-in", () => {
  beforeEach(() => {
    signOut()
    respondWith(backend)
    provider.respondWith({ callback: () => signedIn() })
  })

  describe("startProviderSignIn", () => {
    it("redirects with the scopes the backend names and carries the return address as state", async () => {
      provider.respondWith({ redirect: () => undefined })

      await startProviderSignIn(offered, "/orders/42")

      assert.deepEqual(provider.redirects, [
        {
          scope: "openid profile offline_access",
          state: { returnTo: "/orders/42" },
        },
      ])
      const settings = provider.managers.at(-1)?.settings
      assert.equal(settings?.authority, "https://id.example.com")
      assert.equal(settings?.client_id, "shop-panel")
      // Yönlendirmeden önce backend'e veya depoya bir şey yazılmaz.
      assert.equal(calls.length, 0)
      assert.equal(storage.getItem(STORAGE_KEY), null)
    })

    it("asks for the openid scope when the backend names none", async () => {
      provider.respondWith({ redirect: () => undefined })

      await startProviderSignIn({ ...offered, scopes: "  " }, "/products")

      assert.deepEqual(provider.redirects, [
        { scope: "openid", state: { returnTo: "/products" } },
      ])
    })

    it("fails when the provider cannot be reached", async () => {
      provider.respondWith({
        redirect: () => {
          throw new TypeError("Failed to fetch")
        },
      })

      await assert.rejects(startProviderSignIn(offered, "/products"), TypeError)
    })
  })

  describe("finishProviderSignIn", () => {
    it("completes the callback at the provider the backend offers", async () => {
      const url = callbackUrl()

      await finishProviderSignIn(url)

      assert.deepEqual(provider.callbacks, [url])
      const settings = provider.managers.at(-1)?.settings
      assert.equal(settings?.authority, "https://id.example.com")
      assert.equal(settings?.client_id, "shop-panel")
    })

    it("asks the backend who the account is with the provider's access token", async () => {
      await finishProviderSignIn(callbackUrl())

      assert.deepEqual(
        calls.map((call) => [call.method, call.url, bearer(call)]),
        [
          ["GET", "/api/auth/config", null],
          ["GET", "/api/auth/me", "Bearer idp-access-1"],
        ]
      )
    })

    it("stores a provider session with the provider's tokens and the backend's account", async () => {
      await finishProviderSignIn(callbackUrl())

      const expected = providerSession()
      assert.deepEqual(getSession(), expected)
      assert.deepEqual(
        JSON.parse(storage.getItem(STORAGE_KEY) ?? "null"),
        expected
      )
    })

    it("stores neither the ID token nor the profile, and empties the library's store", async () => {
      await finishProviderSignIn(callbackUrl())

      const stored = storage.getItem(STORAGE_KEY) ?? ""
      assert.ok(!stored.includes("header.claims.signature"))
      assert.ok(!stored.includes("subject-1"))
      assert.ok(!stored.includes("ada@id.example.com"))
      assert.deepEqual(provider.storedUsers(), [])
      assert.deepEqual([...storage.items.keys()], [STORAGE_KEY])
    })

    it("stores an empty refresh token when the provider issues none", async () => {
      provider.respondWith({ callback: () => signedIn({ refresh: undefined }) })

      await finishProviderSignIn(callbackUrl())

      assert.deepEqual(getSession(), providerSession({ refreshToken: "" }))
    })

    it("replaces an older session of another kind", async () => {
      signIn(session({ accountId: "account-0", accessToken: "old-access" }))

      await finishProviderSignIn(callbackUrl())

      assert.deepEqual(getSession(), providerSession())
      // Hesap eski oturumun token'ıyla değil, sağlayıcınınkiyle sorulur; yapılandırma isteği token taşımaz.
      assert.equal(bearer(callsTo("/auth/config")[0]), null)
      assert.equal(bearer(callsTo("/auth/me")[0]), "Bearer idp-access-1")
    })

    it("returns the page the sign-in was started from", async () => {
      assert.equal(await finishProviderSignIn(callbackUrl()), "/orders")
    })

    for (const state of [
      undefined,
      null,
      "orders",
      { returnTo: "//evil.example" },
      { returnTo: "https://evil.example" },
      { returnTo: 7 },
      { other: "/orders" },
    ]) {
      it(`falls back to the products page for the state ${JSON.stringify(state)}`, async () => {
        provider.respondWith({ callback: () => signedIn({ state }) })

        assert.equal(await finishProviderSignIn(callbackUrl()), "/products")
      })
    }

    it("handles one address once, however often it is asked", async () => {
      const url = callbackUrl()

      const [first, second] = await Promise.all([
        finishProviderSignIn(url),
        finishProviderSignIn(url),
      ])
      const third = await finishProviderSignIn(url)

      // Yetki kodu bir kez kullanılır; ikinci bir deneme sağlayıcıda reddedilirdi.
      assert.deepEqual(
        [first, second, third],
        ["/orders", "/orders", "/orders"]
      )
      assert.equal(provider.callbacks.length, 1)
      assert.equal(callsTo("/auth/me").length, 1)
    })

    it("handles a new address again", async () => {
      await finishProviderSignIn(callbackUrl())
      await finishProviderSignIn(callbackUrl())

      assert.equal(provider.callbacks.length, 2)
    })

    it("opens no session when the provider reports an error", async () => {
      provider.respondWith({
        callback: () => {
          throw new ErrorResponse({ error: "access_denied" })
        },
      })

      await assert.rejects(finishProviderSignIn(callbackUrl()), (error) => {
        assert.ok(isProviderError(error))
        return true
      })

      assert.equal(getSession(), null)
      assert.equal(storage.getItem(STORAGE_KEY), null)
      assert.equal(callsTo("/auth/me").length, 0)
      assert.deepEqual(provider.storedUsers(), [])
    })

    it("opens no session when the state does not match", async () => {
      provider.respondWith({
        callback: () => {
          throw new Error("No matching state found in storage")
        },
      })

      await assert.rejects(finishProviderSignIn(callbackUrl()), (error) => {
        assert.ok(!isProviderError(error))
        return true
      })

      assert.equal(getSession(), null)
      assert.equal(callsTo("/auth/me").length, 0)
    })

    it("opens no session and keeps no token when the backend rejects the token", async () => {
      respondWith((call) =>
        call.url.endsWith("/auth/me") ? json(401, unauthorized) : backend(call)
      )

      await assert.rejects(finishProviderSignIn(callbackUrl()), (error) => {
        assert.deepEqual(error, unauthorized)
        return true
      })

      assert.equal(getSession(), null)
      assert.equal(storage.items.size, 0)
      assert.deepEqual(provider.storedUsers(), [])
      // Reddedilen token için yenileme denenmez.
      assert.equal(provider.renewals.length, 0)
      assert.equal(callsTo("/auth/me").length, 1)
    })

    it("keeps the existing session when the backend rejects the new token", async () => {
      const existing = session()
      signIn(existing)
      respondWith((call) =>
        call.url.endsWith("/auth/me") ? json(401, unauthorized) : backend(call)
      )

      await assert.rejects(finishProviderSignIn(callbackUrl()))

      assert.deepEqual(getSession(), existing)
      assert.equal(callsTo("/auth/refresh").length, 0)
    })

    it("does not touch the provider when the backend offers none", async () => {
      respondWith(() =>
        json(200, { passwordSignIn: true, provider: null, serverSignIn: false })
      )

      await assert.rejects(finishProviderSignIn(callbackUrl()))

      assert.equal(provider.callbacks.length, 0)
      assert.equal(getSession(), null)
    })

    it("does not use the code when the configuration cannot be read", async () => {
      respondWith(() => {
        throw new TypeError("fetch failed")
      })

      await assert.rejects(finishProviderSignIn(callbackUrl()), TypeError)

      // Kod harcanmadı: sayfa yenilenince giriş tamamlanabilir.
      assert.equal(provider.callbacks.length, 0)
      assert.equal(getSession(), null)
    })
  })

  describe("closeSession", () => {
    beforeEach(() => {
      signIn(providerSession())
      respondWith(() => {
        throw new Error("Unexpected request.")
      })
      provider.respondWith()
    })

    it("clears the session and the library's user without calling anybody", async () => {
      await closeSession()

      assert.equal(getSession(), null)
      assert.equal(storage.getItem(STORAGE_KEY), null)
      assert.equal(provider.removals, 1)
      assert.deepEqual(provider.storedUsers(), [])
      // Backend'de kapatılacak bir oturum yoktur; sağlayıcının çıkış ucu da çağrılmaz.
      assert.equal(calls.length, 0)
      assert.equal(provider.redirects.length, 0)
    })

    it("clears the user of the provider that issued the session", async () => {
      signIn(providerSession({ authority: "https://third-id.example.com" }))

      await closeSession()

      assert.equal(
        provider.managers.at(-1)?.settings.authority,
        "https://third-id.example.com"
      )
    })

    it("signs out before waiting for the library", async () => {
      const pending = closeSession()

      assert.equal(getSession(), null)
      await pending
    })

    it("signs out even when the library fails", async () => {
      const manager = provider.managers.find(
        (candidate) =>
          candidate.settings.authority === providerSession().authority
      )
      assert.ok(manager)
      const original = manager.removeUser
      manager.removeUser = () => Promise.reject(new Error("storage is broken"))

      try {
        await closeSession()
      } finally {
        manager.removeUser = original
      }

      assert.equal(getSession(), null)
      assert.equal(storage.getItem(STORAGE_KEY), null)
    })

    it("still revokes a password session at the backend", async () => {
      signIn(session())
      respondWith(() => new Response(null, { status: 204 }))

      await closeSession()

      assert.equal(calls.length, 1)
      assert.equal(calls[0].url, "/api/auth/logout")
      assert.deepEqual(calls[0].body, { refreshToken: "refresh-1" })
      assert.equal(provider.removals, 0)
      assert.equal(getSession(), null)
    })
  })
})
