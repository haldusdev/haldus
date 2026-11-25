import assert from "node:assert/strict"
import { beforeEach, describe, it } from "node:test"

import "./support/browser.ts"
import { calls, empty, json, respondWith, text } from "./support/fetch.ts"
import { session, unauthorized } from "./support/fixtures.ts"
import i18n from "./stubs/i18n.ts"

import { apiFetch, renewSession } from "@/lib/api-fetch"
import { getSession, signIn, signOut } from "@/lib/session"

type Result = { data: unknown; status: number; headers: Headers }

// Bu dosyada `lib/auth.ts` yüklenmez: yenileme işlevi kayıtlı değildir. Yenileme api-fetch-renewal.test.ts'tedir.
describe("apiFetch", () => {
  beforeEach(() => {
    signOut()
    i18n.language = "en"
    respondWith(() => json(200, { ok: true }))
  })

  describe("request", () => {
    it("prefixes the API base and passes the options through", async () => {
      await apiFetch("/products", {
        method: "POST",
        body: JSON.stringify({ name: "Lamp" }),
      })

      assert.equal(calls.length, 1)
      assert.equal(calls[0].url, "/api/products")
      assert.equal(calls[0].method, "POST")
      assert.deepEqual(calls[0].body, { name: "Lamp" })
    })

    it("sends the interface language", async () => {
      await apiFetch("/products", {})
      i18n.language = "tr"
      await apiFetch("/products", {})

      assert.equal(calls[0].headers.get("Accept-Language"), "en")
      assert.equal(calls[1].headers.get("Accept-Language"), "tr")
    })

    it("keeps the headers given by the caller", async () => {
      await apiFetch("/products", {
        headers: { "Content-Type": "application/json", "If-Match": '"7"' },
      })

      assert.equal(calls[0].headers.get("Content-Type"), "application/json")
      assert.equal(calls[0].headers.get("If-Match"), '"7"')
    })

    it("attaches the access token of the session", async () => {
      signIn(session())

      await apiFetch("/products", {})

      assert.equal(calls[0].headers.get("Authorization"), "Bearer access-1")
    })

    it("sends no Authorization header without a session", async () => {
      await apiFetch("/products", {})

      assert.equal(calls[0].headers.has("Authorization"), false)
    })

    for (const route of ["/auth/login", "/auth/register", "/auth/refresh"]) {
      it(`does not attach the access token to ${route}`, async () => {
        signIn(session())

        await apiFetch(route, { method: "POST" })

        assert.equal(calls[0].headers.has("Authorization"), false)
      })
    }

    it("attaches the access token to the other auth routes", async () => {
      signIn(session())

      await apiFetch("/auth/logout", { method: "POST" })

      assert.equal(calls[0].headers.get("Authorization"), "Bearer access-1")
    })

    it("does not overwrite an Authorization header given by the caller", async () => {
      signIn(session())

      await apiFetch("/auth/me", {
        headers: { Authorization: "Bearer fresh-token" },
      })

      assert.equal(calls[0].headers.get("Authorization"), "Bearer fresh-token")
    })
  })

  describe("response", () => {
    it("returns the parsed body, the status and the headers", async () => {
      respondWith(() => json(201, { id: "p1" }))

      const result = await apiFetch<Result>("/products", { method: "POST" })

      assert.deepEqual(result.data, { id: "p1" })
      assert.equal(result.status, 201)
      assert.equal(result.headers.get("Content-Type"), "application/json")
    })

    it("yields no data for 204", async () => {
      respondWith(() => empty(204))

      const result = await apiFetch<Result>("/products/p1", {
        method: "DELETE",
      })

      assert.equal(result.data, undefined)
      assert.equal(result.status, 204)
    })

    it("yields no data for a successful body that is not JSON", async () => {
      respondWith(() => text(200, "<html>ok</html>", "OK"))

      const result = await apiFetch<Result>("/products", {})

      assert.equal(result.data, undefined)
    })

    it("throws the ProblemDetails body of a failed response", async () => {
      const problem = {
        title: "One or more validation errors occurred.",
        status: 400,
        errors: { name: ["The Name field is required."] },
      }
      respondWith(() => json(400, problem))

      await assert.rejects(
        apiFetch("/products", { method: "POST" }),
        (error) => {
          assert.deepEqual(error, problem)
          assert.equal(error instanceof Error, false)
          return true
        }
      )
    })

    it("throws the status and its text when the failed body is not JSON", async () => {
      respondWith(() => text(502, "<html>Bad Gateway</html>", "Bad Gateway"))

      await assert.rejects(apiFetch("/products", {}), (error) => {
        assert.deepEqual(error, { status: 502, title: "Bad Gateway" })
        return true
      })
    })

    it("throws the status and its text when the failed body is empty", async () => {
      respondWith(() => new Response(null, { status: 404, statusText: "Nope" }))

      await assert.rejects(apiFetch("/products/p9", {}), (error) => {
        assert.deepEqual(error, { status: 404, title: "Nope" })
        return true
      })
    })

    it("throws the status and its text when the failed body is a JSON scalar", async () => {
      respondWith(() => text(500, '"boom"', "Internal Server Error"))

      await assert.rejects(apiFetch("/products", {}), (error) => {
        assert.deepEqual(error, {
          status: 500,
          title: "Internal Server Error",
        })
        return true
      })
    })

    it("lets a network error through", async () => {
      const failure = new TypeError("fetch failed")
      respondWith(() => {
        throw failure
      })

      await assert.rejects(apiFetch("/products", {}), failure)
    })
  })

  describe("401 without a way to renew", () => {
    it("is thrown without a retry when nobody is signed in", async () => {
      respondWith(() => json(401, unauthorized))

      await assert.rejects(apiFetch("/orders", {}), (error) => {
        assert.deepEqual(error, unauthorized)
        return true
      })
      assert.equal(calls.length, 1)
    })

    it("is thrown and the session is kept when no refresher is registered", async () => {
      signIn(session())
      respondWith(() => json(401, unauthorized))

      await assert.rejects(apiFetch("/orders", {}), (error) => {
        assert.deepEqual(error, unauthorized)
        return true
      })
      assert.equal(calls.length, 1)
      assert.deepEqual(getSession(), session())
    })

    it("renewSession reports failure", async () => {
      assert.equal(await renewSession(), false)

      signIn(session())

      assert.equal(await renewSession(), false)
      assert.equal(calls.length, 0)
    })
  })
})
