import assert from "node:assert/strict"
import { describe, it } from "node:test"

import { storage } from "./support/browser.ts"
import { session, STORAGE_KEY } from "./support/fixtures.ts"

// Panelin önceki sürümü oturumu tür alanı olmadan yazıyordu. Modül yüklenirken depoyu okur; o yüzden eski kayıt
// yüklemeden önce yazılır ve bu dosya yalnızca o açılışı sınar.
const legacy = JSON.stringify({
  accessToken: "access-1",
  refreshToken: "refresh-1",
  accountId: "account-1",
  email: "ada@example.com",
  tenantId: "tenant-1",
  emailVerified: true,
})
storage.setItem(STORAGE_KEY, legacy)

const { getSession, reloadSession, subscribeSession } =
  await import("@/lib/session")

describe("a session stored by the previous version", () => {
  it("is still signed in after the upgrade, as a password session", () => {
    assert.deepEqual(getSession(), session({ emailVerified: true }))
  })

  it("is not rewritten or announced as changed just by being read", () => {
    let published = 0
    const unsubscribe = subscribeSession(() => {
      published++
    })

    reloadSession()
    unsubscribe()

    assert.equal(storage.getItem(STORAGE_KEY), legacy)
    assert.equal(published, 0)
  })
})
