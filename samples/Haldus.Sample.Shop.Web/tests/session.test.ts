import assert from "node:assert/strict"
import { beforeEach, describe, it } from "node:test"

import { dispatchStorage, storage } from "./support/browser.ts"
import { session, STORAGE_KEY } from "./support/fixtures.ts"

// Modül yüklenirken depoyu okur; yüklenmeden önce yazılan oturum bu yüzden burada hazırlanır.
const initial = session({ accessToken: "stored-access", emailVerified: true })
storage.setItem(STORAGE_KEY, JSON.stringify(initial))

const {
  getSession,
  markEmailVerified,
  reloadSession,
  renewTokens,
  signIn,
  signOut,
  subscribeSession,
} = await import("@/lib/session")

function stored(): unknown {
  return JSON.parse(storage.getItem(STORAGE_KEY) ?? "null")
}

/** Dinleyicinin kaç kez çağrıldığını sayar. */
function watch() {
  const seen = { count: 0, unsubscribe: () => {} }

  seen.unsubscribe = subscribeSession(() => {
    seen.count++
  })
  return seen
}

describe("session", () => {
  it("starts with the session found in storage", () => {
    assert.deepEqual(getSession(), initial)
  })

  describe("after start", () => {
    beforeEach(() => {
      signOut()
    })

    it("signIn stores the session and tells subscribers", () => {
      const seen = watch()

      signIn(session())

      assert.deepEqual(getSession(), session())
      assert.deepEqual(stored(), session())
      assert.equal(seen.count, 1)
      seen.unsubscribe()
    })

    it("stops telling a subscriber that unsubscribed", () => {
      const seen = watch()
      seen.unsubscribe()

      signIn(session())

      assert.equal(seen.count, 0)
    })

    it("signOut removes the stored session and tells subscribers", () => {
      signIn(session())
      const seen = watch()

      signOut()

      assert.equal(getSession(), null)
      assert.equal(storage.getItem(STORAGE_KEY), null)
      assert.equal(seen.count, 1)
      seen.unsubscribe()
    })

    it("renewTokens replaces the tokens and keeps the account", () => {
      signIn(session({ emailVerified: true }))
      const seen = watch()

      renewTokens("access-2", "refresh-2")

      const expected = session({
        accessToken: "access-2",
        refreshToken: "refresh-2",
        emailVerified: true,
      })
      assert.deepEqual(getSession(), expected)
      assert.deepEqual(stored(), expected)
      assert.equal(seen.count, 1)
      seen.unsubscribe()
    })

    it("renewTokens does nothing when nobody is signed in", () => {
      const seen = watch()

      renewTokens("access-2", "refresh-2")

      assert.equal(getSession(), null)
      assert.equal(storage.getItem(STORAGE_KEY), null)
      assert.equal(seen.count, 0)
      seen.unsubscribe()
    })

    it("markEmailVerified stores the new tokens and the verified flag", () => {
      signIn(session())
      const seen = watch()

      markEmailVerified("access-2", "refresh-2")

      const expected = session({
        accessToken: "access-2",
        refreshToken: "refresh-2",
        emailVerified: true,
      })
      assert.deepEqual(getSession(), expected)
      assert.deepEqual(stored(), expected)
      assert.equal(seen.count, 1)
      seen.unsubscribe()
    })

    it("markEmailVerified does nothing when nobody is signed in", () => {
      markEmailVerified("access-2", "refresh-2")

      assert.equal(getSession(), null)
      assert.equal(storage.getItem(STORAGE_KEY), null)
    })

    it("reloadSession picks up a session written by another tab", () => {
      signIn(session())
      const seen = watch()
      const renewed = session({
        accessToken: "access-2",
        refreshToken: "refresh-2",
      })
      storage.setItem(STORAGE_KEY, JSON.stringify(renewed))

      reloadSession()

      assert.deepEqual(getSession(), renewed)
      assert.equal(seen.count, 1)
      seen.unsubscribe()
    })

    it("reloadSession picks up a sign-out made by another tab", () => {
      signIn(session())
      storage.removeItem(STORAGE_KEY)

      reloadSession()

      assert.equal(getSession(), null)
    })

    it("reloadSession stays quiet when storage did not change", () => {
      signIn(session())
      const seen = watch()
      const before = getSession()

      reloadSession()

      assert.equal(getSession(), before)
      assert.equal(seen.count, 0)
      seen.unsubscribe()
    })

    it("follows a storage event for the session key", () => {
      signIn(session())
      const seen = watch()
      const renewed = session({ accessToken: "access-2" })
      storage.setItem(STORAGE_KEY, JSON.stringify(renewed))

      dispatchStorage(STORAGE_KEY)

      assert.deepEqual(getSession(), renewed)
      assert.equal(seen.count, 1)
      seen.unsubscribe()
    })

    it("ignores a storage event for another key", () => {
      signIn(session())
      const seen = watch()
      storage.setItem(
        STORAGE_KEY,
        JSON.stringify(session({ accessToken: "access-2" }))
      )

      dispatchStorage("haldus-shop.language")

      assert.deepEqual(getSession(), session())
      assert.equal(seen.count, 0)
      seen.unsubscribe()
    })

    const malformed: [string, string][] = [
      ["text that is not JSON", "{not json"],
      ["a JSON null", "null"],
      ["a JSON string", '"access-1"'],
      ["an array", '["access-1","refresh-1"]'],
      ["an empty object", "{}"],
      [
        "a session without a refresh token",
        JSON.stringify({ ...session(), refreshToken: undefined }),
      ],
      [
        "a session with an empty access token",
        JSON.stringify(session({ accessToken: "" })),
      ],
      [
        "a session whose token is not text",
        JSON.stringify({ ...session(), accessToken: 42 }),
      ],
      [
        "a session without a tenant",
        JSON.stringify({ ...session(), tenantId: undefined }),
      ],
    ]

    for (const [name, value] of malformed) {
      it(`ignores stored ${name}`, () => {
        signIn(session())
        storage.setItem(STORAGE_KEY, value)

        reloadSession()

        assert.equal(getSession(), null)
      })
    }

    it("reads the verified flag only when it is exactly true", () => {
      storage.setItem(
        STORAGE_KEY,
        JSON.stringify({ ...session(), emailVerified: "true" })
      )

      reloadSession()

      assert.deepEqual(getSession(), session({ emailVerified: false }))
    })

    it("drops unknown fields of a stored session", () => {
      storage.setItem(
        STORAGE_KEY,
        JSON.stringify({ ...session(), role: "admin" })
      )

      reloadSession()

      assert.deepEqual(getSession(), session())
    })
  })
})
