import assert from "node:assert/strict"
import { beforeEach, describe, it } from "node:test"

import {
  lockRequests,
  ORIGIN,
  setLocksAvailable,
  storage,
  tabStorage,
} from "./support/browser.ts"
import {
  calls,
  callsTo,
  deferred,
  json,
  respondWith,
  settle,
} from "./support/fetch.ts"
import type { Call } from "./support/fetch.ts"
import {
  providerSession,
  session,
  STORAGE_KEY,
  unauthorized,
} from "./support/fixtures.ts"
import {
  ErrorResponse,
  ErrorTimeout,
  InMemoryWebStorage,
  provider,
  type Renewed,
} from "./support/provider.ts"

// Yenileme işlevini kaydeder. Sağlayıcıya giden yol taklit kitaplıktan, backend'e giden yol `fetch` taklidinden geçer.
await import("@/lib/auth")
const { apiFetch, renewSession } = await import("@/lib/api-fetch")
const { getSession, signIn, signOut, subscribeSession } =
  await import("@/lib/session")

type Result = { data: unknown; status: number }

const renewed: Renewed = {
  access_token: "idp-access-2",
  refresh_token: "idp-refresh-2",
}
const renewedSession = providerSession({
  accessToken: "idp-access-2",
  refreshToken: "idp-refresh-2",
})
const expired = { ...unauthorized, detail: "The token has expired." }

function bearer(call: Call): string | null {
  return call.headers.get("Authorization")
}

/** Örnek backend gibi: sağlayıcının eski erişim token'ını 401 ile reddeder, yenisini kabul eder. */
function backend(call: Call): Response {
  return bearer(call) === "Bearer idp-access-2"
    ? json(200, { path: call.url })
    : json(401, expired)
}

function storedSession(): unknown {
  return JSON.parse(storage.getItem(STORAGE_KEY) ?? "null")
}

// Parola oturumunun yenileme kuralları (api-fetch-renewal.test.ts, api-fetch-safety.test.ts) kimlik sağlayıcı
// oturumu için de geçerlidir; yenilemeyi backend değil sağlayıcı yapar.
describe("apiFetch renewal of a provider session", () => {
  beforeEach(() => {
    setLocksAvailable(true)
    lockRequests.length = 0
    signIn(providerSession())
    respondWith(backend)
    provider.respondWith({ renew: () => renewed })
  })

  it("renews through the provider after a 401 and repeats the request", async () => {
    const result = await apiFetch<Result>("/orders", {})

    assert.deepEqual(
      calls.map((call) => [call.url, bearer(call)]),
      [
        ["/api/orders", "Bearer idp-access-1"],
        ["/api/orders", "Bearer idp-access-2"],
      ]
    )
    assert.deepEqual(result.data, { path: "/api/orders" })

    // Sağlayıcıya oturumun yenileme token'ı sunulur; backend'in yenileme ucuna gidilmez.
    assert.equal(provider.renewals.length, 1)
    assert.equal(provider.renewals[0].refresh_token, "idp-refresh-1")
    assert.equal(provider.renewals[0].access_token, "idp-access-1")
    assert.equal(callsTo("/auth/refresh").length, 0)
  })

  it("stores the renewed tokens, keeps the provider and tells subscribers once", async () => {
    let published = 0
    const unsubscribe = subscribeSession(() => {
      published++
    })

    await apiFetch("/orders", {})
    unsubscribe()

    assert.deepEqual(getSession(), renewedSession)
    assert.deepEqual(storedSession(), renewedSession)
    assert.equal(published, 1)
  })

  it("asks the provider that issued the session's tokens", async () => {
    signIn(
      providerSession({
        authority: "https://other-id.example.com",
        clientId: "another-client",
      })
    )

    await apiFetch("/orders", {})

    const settings = provider.managers.at(-1)?.settings
    assert.equal(settings?.authority, "https://other-id.example.com")
    assert.equal(settings?.client_id, "another-client")
  })

  it("keeps the refresh token when the provider does not rotate it", async () => {
    provider.respondWith({ renew: () => ({ access_token: "idp-access-2" }) })

    await apiFetch("/orders", {})

    assert.deepEqual(
      getSession(),
      providerSession({ accessToken: "idp-access-2" })
    )
  })

  it("leaves no user in the library's store after a renewal", async () => {
    await apiFetch("/orders", {})

    assert.deepEqual(provider.storedUsers(), [])
    assert.equal(provider.removals, 1)
  })

  it("leaves no user in the library's store after a failed renewal", async () => {
    provider.respondWith({
      renew: () => {
        throw new TypeError("Failed to fetch")
      },
    })

    await assert.rejects(apiFetch("/orders", {}))

    assert.deepEqual(provider.storedUsers(), [])
    assert.equal(provider.removals, 1)
  })

  it("shares one renewal between requests rejected at the same time", async () => {
    const renewal = deferred<Renewed>()
    provider.respondWith({ renew: () => renewal.promise })

    const pending = Promise.all([
      apiFetch<Result>("/orders", {}),
      apiFetch<Result>("/products", {}),
      apiFetch<Result>("/categories", {}),
    ])
    await settle()

    assert.equal(provider.renewals.length, 1)

    renewal.resolve(renewed)
    const results = await pending

    assert.equal(provider.renewals.length, 1)
    assert.deepEqual(
      results.map((result) => result.data),
      [
        { path: "/api/orders" },
        { path: "/api/products" },
        { path: "/api/categories" },
      ]
    )
    assert.deepEqual(
      calls.slice(-3).map(bearer),
      Array(3).fill("Bearer idp-access-2")
    )
  })

  it("signs out when the backend rejects the renewed token too", async () => {
    respondWith(() => json(401, expired))

    await assert.rejects(apiFetch("/orders", {}), (error) => {
      assert.deepEqual(error, expired)
      return true
    })

    assert.equal(provider.renewals.length, 1)
    assert.equal(callsTo("/orders").length, 2)
    // Sağlayıcı yeniliyor ama backend kabul etmiyor: oturum açık kalsaydı her istek bir yenileme token'ı harcardı.
    // Yenileme ikinci kez denenmez (yukarıda) ve oturum kapanır.
    assert.equal(getSession(), null)
    assert.equal(storage.getItem(STORAGE_KEY), null)
  })

  it("renews on demand", async () => {
    assert.equal(await renewSession(), true)

    assert.equal(calls.length, 0)
    assert.equal(provider.renewals.length, 1)
    assert.deepEqual(getSession(), renewedSession)
  })

  describe("when the provider refuses", () => {
    for (const code of [
      "invalid_grant",
      "invalid_client",
      "unauthorized_client",
    ]) {
      it(`signs out after ${code}`, async () => {
        provider.respondWith({
          renew: () => {
            throw new ErrorResponse({ error: code })
          },
        })

        await assert.rejects(apiFetch("/orders", {}), (error) => {
          // İsteğin kendi hatası döner, sağlayıcınınki değil.
          assert.deepEqual(error, expired)
          return true
        })

        assert.equal(getSession(), null)
        assert.equal(storage.getItem(STORAGE_KEY), null)
        assert.equal(provider.renewals.length, 1)
        assert.equal(callsTo("/orders").length, 1)
      })
    }

    it("ends a session that has no refresh token without asking the provider", async () => {
      signIn(providerSession({ refreshToken: "" }))

      await assert.rejects(apiFetch("/orders", {}), (error) => {
        assert.deepEqual(error, expired)
        return true
      })

      assert.equal(getSession(), null)
      assert.equal(storage.getItem(STORAGE_KEY), null)
      // Yenileme token'ı olmayan kullanıcıyla `signinSilent` çağrılsaydı kitaplık gizli bir iframe açmayı denerdi.
      assert.equal(provider.renewals.length, 0)
      assert.deepEqual(provider.storedUsers(), [])
    })
  })

  describe("when the provider cannot answer", () => {
    const failures: [string, () => Error][] = [
      ["a network error", () => new TypeError("Failed to fetch")],
      ["a timeout", () => new ErrorTimeout("Network timed out")],
      [
        "a server error it reports itself",
        () => new ErrorResponse({ error: "server_error" }),
      ],
      [
        "being temporarily unavailable",
        () => new ErrorResponse({ error: "temporarily_unavailable" }),
      ],
      [
        "an error page that is not an OAuth answer",
        () => new Error("Service Unavailable (503)"),
      ],
    ]

    for (const [name, failure] of failures) {
      it(`keeps the session after ${name}`, async () => {
        provider.respondWith({
          renew: () => {
            throw failure()
          },
        })

        // İstek kendi 401'iyle başarısız olur; geçici bir arıza kullanıcıyı oturumdan atmaz.
        await assert.rejects(apiFetch("/orders", {}), (error) => {
          assert.deepEqual(error, expired)
          return true
        })

        assert.deepEqual(getSession(), providerSession())
        assert.deepEqual(storedSession(), providerSession())
        assert.equal(callsTo("/orders").length, 1)
      })
    }

    it("can renew once the provider answers again", async () => {
      provider.respondWith({
        renew: () => {
          throw new TypeError("Failed to fetch")
        },
      })
      await assert.rejects(apiFetch("/orders", {}))

      provider.respondWith({ renew: () => renewed })
      respondWith(backend)
      await apiFetch("/orders", {})

      assert.deepEqual(getSession(), renewedSession)
    })
  })

  describe("when the session changes while the provider is asked", () => {
    it("does not write renewed tokens into another account's session", async () => {
      const renewal = deferred<Renewed>()
      provider.respondWith({ renew: () => renewal.promise })

      const request = apiFetch("/orders", {})
      await settle()

      // Yanıt beklenirken kullanıcı çıkıp başka bir hesapla (bu kez parolayla) girer.
      const other = session({
        accountId: "account-2",
        email: "grace@example.com",
        accessToken: "access-b",
        refreshToken: "refresh-b",
      })
      signIn(other)
      renewal.resolve(renewed)

      await assert.rejects(request)
      assert.deepEqual(getSession(), other)
      assert.deepEqual(storedSession(), other)
    })

    it("does not sign another account out when the old renewal is refused", async () => {
      const renewal = deferred<Renewed>()
      provider.respondWith({ renew: () => renewal.promise })

      const request = apiFetch("/orders", {})
      await settle()

      const other = providerSession({
        accountId: "account-2",
        accessToken: "idp-access-b",
        refreshToken: "idp-refresh-b",
      })
      signIn(other)
      renewal.reject(new ErrorResponse({ error: "invalid_grant" }))

      await assert.rejects(request)
      assert.deepEqual(getSession(), other)
    })

    it("does not sign another account out when neither session has a refresh token", async () => {
      signIn(providerSession({ refreshToken: "" }))
      const other = providerSession({
        accountId: "account-2",
        accessToken: "idp-access-b",
        refreshToken: "",
      })
      respondWith((call) => {
        // 401 yanıtı yoldayken başka bir sekmede başka bir hesapla giriş yapılır.
        storage.setItem(STORAGE_KEY, JSON.stringify(other))
        return backend(call)
      })

      await assert.rejects(apiFetch("/orders", {}))

      assert.deepEqual(getSession(), other)
      assert.deepEqual(storedSession(), other)
    })

    // Oturum yalnızca yenileme token'ıyla tanınmaz: o alan iki ayrı oturumda aynı olabilir (yenileme token'ı vermeyen
    // sağlayıcıda ikisi de boştur). Burada aynı değer bilerek iki hesaba verilir.
    for (const [name, answer] of [
      ["write renewed tokens into", () => renewed],
      [
        "end",
        () => {
          throw new ErrorResponse({ error: "invalid_grant" })
        },
      ],
    ] as const) {
      it(`does not ${name} another account's session that carries the same refresh token`, async () => {
        const renewal = deferred<Renewed>()
        provider.respondWith({ renew: () => renewal.promise })

        const request = apiFetch("/orders", {})
        await settle()

        const other = providerSession({
          accountId: "account-2",
          accessToken: "idp-access-b",
        })
        signIn(other)

        try {
          renewal.resolve(answer())
        } catch (error) {
          renewal.reject(error)
        }

        await assert.rejects(request)
        assert.deepEqual(getSession(), other)
        assert.deepEqual(storedSession(), other)
      })
    }

    it("does not end the same account's newer session when the old renewal is refused", async () => {
      const renewal = deferred<Renewed>()
      provider.respondWith({ renew: () => renewal.promise })

      const request = apiFetch<Result>("/orders", {})
      await settle()

      // Aynı hesap bu arada yeniden giriş yaptı: yeni token'lar, yeni bir yenileme token'ı.
      signIn(renewedSession)
      renewal.reject(new ErrorResponse({ error: "invalid_grant" }))

      assert.deepEqual((await request).data, { path: "/api/orders" })
      assert.deepEqual(getSession(), renewedSession)
    })

    it("does not overwrite tokens another tab stored while this renewal was answered", async () => {
      // Sağlayıcı yenileme token'ını döndürmüyorsa iki sekmenin yenilemesi de başarılı olabilir (kilit yokken).
      const fromOtherTab = providerSession({ accessToken: "idp-access-2" })
      provider.respondWith({
        renew: () => {
          storage.setItem(STORAGE_KEY, JSON.stringify(fromOtherTab))
          return { access_token: "idp-access-3" }
        },
      })

      const result = await apiFetch<Result>("/orders", {})

      assert.deepEqual(result.data, { path: "/api/orders" })
      assert.deepEqual(getSession(), fromOtherTab)
      assert.deepEqual(storedSession(), fromOtherTab)
    })

    it("writes nothing when the user signed out meanwhile", async () => {
      const renewal = deferred<Renewed>()
      provider.respondWith({ renew: () => renewal.promise })

      const request = apiFetch("/orders", {})
      await settle()
      signOut()
      renewal.resolve(renewed)

      await assert.rejects(request)
      assert.equal(getSession(), null)
      assert.equal(storage.getItem(STORAGE_KEY), null)
    })
  })

  describe("with another tab", () => {
    const fromOtherTab = providerSession({
      accessToken: "idp-access-2",
      refreshToken: "idp-refresh-other-tab",
    })

    it("uses the tokens another tab already stored instead of renewing", async () => {
      respondWith((call) => {
        if (bearer(call) === "Bearer idp-access-1") {
          storage.setItem(STORAGE_KEY, JSON.stringify(fromOtherTab))
        }

        return backend(call)
      })

      const result = await apiFetch<Result>("/orders", {})

      assert.deepEqual(result.data, { path: "/api/orders" })
      assert.equal(provider.renewals.length, 0)
      assert.deepEqual(getSession(), fromOtherTab)
    })

    it("adopts the session of a tab that renewed while this renewal was refused", async () => {
      provider.respondWith({
        renew: () => {
          // Öteki sekme aynı yenileme token'ını daha önce sundu; sağlayıcı bu isteği reddeder.
          storage.setItem(STORAGE_KEY, JSON.stringify(fromOtherTab))
          throw new ErrorResponse({ error: "invalid_grant" })
        },
      })

      const result = await apiFetch<Result>("/orders", {})

      assert.deepEqual(result.data, { path: "/api/orders" })
      assert.deepEqual(getSession(), fromOtherTab)
      assert.deepEqual(storedSession(), fromOtherTab)
    })

    it("does not renew when another tab signed out", async () => {
      respondWith((call) => {
        storage.removeItem(STORAGE_KEY)
        return backend(call)
      })

      await assert.rejects(apiFetch("/orders", {}))

      assert.equal(provider.renewals.length, 0)
      assert.equal(getSession(), null)
    })

    it("queues the renewal behind the cross-tab lock", async () => {
      await apiFetch("/orders", {})

      assert.deepEqual(lockRequests, ["haldus-shop.session-refresh"])
    })

    it("shares one renewal between requests where Web Locks is missing", async () => {
      setLocksAvailable(false)
      const renewal = deferred<Renewed>()
      provider.respondWith({ renew: () => renewal.promise })

      const pending = Promise.all([
        apiFetch<Result>("/orders", {}),
        apiFetch<Result>("/products", {}),
      ])
      await settle()
      renewal.resolve(renewed)
      await pending

      assert.equal(provider.renewals.length, 1)
      assert.deepEqual(lockRequests, [])
    })
  })

  // Kitaplığın ayarları bilerek seçildi (lib/provider.ts); yanlışlıkla değişmeleri burada görülür.
  describe("library settings", () => {
    it("keeps the library's user in memory and the redirect state in the tab", async () => {
      await apiFetch("/orders", {})

      const settings = provider.managers.at(-1)?.settings
      const userStore = settings?.userStore as { store: unknown }
      const stateStore = settings?.stateStore as { store: unknown }

      assert.ok(userStore.store instanceof InMemoryWebStorage)
      assert.equal(stateStore.store, tabStorage)
    })

    it("turns off the timer, the iframes and the userinfo request", async () => {
      await apiFetch("/orders", {})

      const settings = provider.managers.at(-1)?.settings
      assert.equal(settings?.automaticSilentRenew, false)
      assert.equal(settings?.silent_redirect_uri, "")
      assert.equal(settings?.monitorSession, false)
      assert.equal(settings?.loadUserInfo, false)
    })

    it("gives up on a provider that does not answer", async () => {
      await apiFetch("/orders", {})

      // Süresiz bekleyen bir yenileme sekmeler arası kilidi de süresiz tutardı. Yenileme isteğinin süresi çağrıda da
      // verilir: kitaplık onu ayarlardan almaz (provider-library.test.ts gerçek kitaplıkla gösterir).
      assert.deepEqual(provider.silentArgs, [
        { silentRequestTimeoutInSeconds: 10 },
      ])
      assert.equal(
        provider.managers.at(-1)?.settings.requestTimeoutInSeconds,
        10
      )
    })

    it("uses the code flow as a public client with the panel's callback address", async () => {
      await apiFetch("/orders", {})

      const settings = provider.managers.at(-1)?.settings
      assert.equal(settings?.response_type, "code")
      assert.equal(settings?.redirect_uri, `${ORIGIN}/login/callback`)
      assert.equal(settings?.client_secret, undefined)
      assert.equal(settings?.disablePKCE, undefined)
    })

    it("builds one manager per provider", async () => {
      await apiFetch("/orders", {})
      const built = provider.managers.length

      signIn(providerSession())
      await apiFetch("/orders", {})

      assert.equal(provider.renewals.length, 2)
      assert.equal(provider.managers.length, built)
    })
  })
})

describe("apiFetch renewal of a password session next to the provider", () => {
  beforeEach(() => {
    signIn(session())
    provider.respondWith()
  })

  it("still renews at the backend, never at the provider", async () => {
    respondWith((call) => {
      if (call.url.endsWith("/auth/refresh")) {
        return json(200, { accessToken: "access-2", refreshToken: "refresh-2" })
      }

      return bearer(call) === "Bearer access-2"
        ? json(200, {})
        : json(401, expired)
    })

    await apiFetch("/orders", {})

    assert.deepEqual(callsTo("/auth/refresh")[0].body, {
      refreshToken: "refresh-1",
    })
    assert.equal(provider.renewals.length, 0)
    assert.deepEqual(
      getSession(),
      session({ accessToken: "access-2", refreshToken: "refresh-2" })
    )
  })
})
