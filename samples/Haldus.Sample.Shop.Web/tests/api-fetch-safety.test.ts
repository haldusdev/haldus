import assert from "node:assert/strict"
import { beforeEach, describe, it } from "node:test"

import { setLocksAvailable, storage } from "./support/browser.ts"
import {
  callsTo,
  deferred,
  json,
  respondWith,
  settle,
  text,
} from "./support/fetch.ts"
import { session, STORAGE_KEY, unauthorized } from "./support/fixtures.ts"

// Yenileme işlevini kaydeder; yenileme isteği üretilen istemciden ve `fetch` taklidinden geçer.
import "@/lib/auth"
import { apiFetch } from "@/lib/api-fetch"
import { getSession, signIn } from "@/lib/session"

const renewedTokens = { accessToken: "access-2", refreshToken: "refresh-2" }
const expired = { ...unauthorized, detail: "The session has expired." }

// İstemci testleri yazılırken bulunan hatalar: yenileme isteğindeki her HTTP hatası oturumu kapatıyordu ve yenilenen
// token'lar o sırada değişmiş bir oturuma yazılabiliyordu.
describe("apiFetch renewal safety", () => {
  beforeEach(() => {
    setLocksAvailable(true)
    signIn(session())
  })

  for (const [name, failure] of [
    [
      "a server error",
      () => json(500, { status: 500, title: "Internal Server Error" }),
    ],
    [
      "a proxy error page",
      () =>
        text(503, "<html>Service Unavailable</html>", "Service Unavailable"),
    ],
    [
      "a rate limit",
      () => json(429, { status: 429, title: "Too Many Requests" }),
    ],
  ] as const) {
    it(`keeps the session when the refresh fails with ${name}`, async () => {
      respondWith((call) =>
        call.url.endsWith("/auth/refresh") ? failure() : json(401, expired)
      )

      // İstek kendi 401'iyle başarısız olur; geçici bir arıza kullanıcıyı oturumdan atmaz.
      await assert.rejects(apiFetch("/orders", {}), (error) => {
        assert.deepEqual(error, expired)
        return true
      })

      assert.deepEqual(getSession(), session())
    })
  }

  for (const status of [400, 401]) {
    it(`signs out when the backend rejects the refresh with ${status}`, async () => {
      respondWith((call) =>
        call.url.endsWith("/auth/refresh")
          ? json(status, { status, title: "Rejected" })
          : json(401, expired)
      )

      await assert.rejects(apiFetch("/orders", {}))
      assert.equal(getSession(), null)
    })
  }

  it("does not write renewed tokens into another account's session", async () => {
    const refresh = deferred<Response>()
    respondWith((call) =>
      call.url.endsWith("/auth/refresh") ? refresh.promise : json(401, expired)
    )

    const request = apiFetch("/orders", {})
    await settle()

    // Yenileme yanıtı beklenirken kullanıcı çıkıp başka bir hesapla girer.
    const other = session({
      accountId: "account-2",
      email: "grace@example.com",
      tenantId: "tenant-2",
      accessToken: "access-b",
      refreshToken: "refresh-b",
    })
    signIn(other)
    refresh.resolve(json(200, renewedTokens))

    await assert.rejects(request)
    assert.deepEqual(getSession(), other)
  })

  it("does not sign another account out when the old refresh is rejected", async () => {
    const refresh = deferred<Response>()
    respondWith((call) =>
      call.url.endsWith("/auth/refresh") ? refresh.promise : json(401, expired)
    )

    const request = apiFetch("/orders", {})
    await settle()

    const other = session({
      accountId: "account-2",
      accessToken: "access-b",
      refreshToken: "refresh-b",
    })
    signIn(other)
    refresh.resolve(json(401, expired))

    await assert.rejects(request)
    assert.deepEqual(getSession(), other)
  })

  it("keeps the session when another request has just renewed it", async () => {
    // 409: backend oturumu az önce başka bir istekle (başka bir sekme) yeniledi; bu isteğe token vermez.
    respondWith((call) =>
      call.url.endsWith("/auth/refresh")
        ? json(409, { status: 409, title: "Conflict" })
        : json(401, expired)
    )

    await assert.rejects(apiFetch("/orders", {}))
    assert.deepEqual(getSession(), session())
  })

  it("adopts the tokens the other tab stores after a 409", async () => {
    const renewed = session({
      accessToken: "access-2",
      refreshToken: "refresh-2",
    })

    respondWith((call) => {
      if (call.url.endsWith("/auth/refresh")) {
        // Öteki sekme yeni token'ları yanıttan biraz sonra depoya yazar.
        setTimeout(
          () => storage.setItem(STORAGE_KEY, JSON.stringify(renewed)),
          120
        )
        return json(409, { status: 409, title: "Conflict" })
      }

      return call.headers.get("Authorization") === "Bearer access-2"
        ? json(200, { ok: true })
        : json(401, expired)
    })

    const result = await apiFetch<{ data: unknown }>("/orders", {})

    assert.deepEqual(result.data, { ok: true })
    assert.deepEqual(getSession(), renewed)
  })
  it("does not renew the session for a request that carried its own token", async () => {
    respondWith(() => json(401, expired))

    // Çağıranın verdiği token reddedildi; oturumun token'ı değil. Oturum yenilenmez ve bir yenileme token'ı harcanmaz.
    await assert.rejects(
      apiFetch("/auth/me", {
        headers: { Authorization: "Bearer someone-else" },
      })
    )

    assert.equal(callsTo("/auth/refresh").length, 0)
    assert.equal(callsTo("/auth/me").length, 1)
    assert.deepEqual(getSession(), session())
  })
})
