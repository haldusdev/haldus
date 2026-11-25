import assert from "node:assert/strict"
import { beforeEach, describe, it } from "node:test"

import "./support/browser.ts"
import {
  calls,
  callsTo,
  json,
  respondWith,
  settle,
  text,
} from "./support/fetch.ts"
import type { Call } from "./support/fetch.ts"
import { providerSession } from "./support/fixtures.ts"

// Bu dosyada `oidc-client-ts` taklit EDİLMEZ: panelin yenileme yolu gerçek kitaplıkla, yalnızca `fetch` taklit
// edilerek çalışır. Öteki testler kitaplığın yerine bir taklit koyar ve onun nasıl davrandığını varsayar; o
// varsayımlar (yenileme isteğinin biçimi, hataların sınıfı, iframe'e düşmeme) burada kitaplığın kendisine sorulur.
// Yönlendirme ve kodun token'a çevrilmesi tarayıcı ister; onlar etkileşim testlerindedir (tests/e2e/provider.e2e.ts).
import { UserManager, WebStorageStateStore } from "oidc-client-ts"

import { RenewalRejected } from "@/lib/api-fetch"
import { renewProviderTokens } from "@/lib/provider"

const authority = "https://id.example.com"
const discovery = {
  issuer: authority,
  authorization_endpoint: `${authority}/authorize`,
  token_endpoint: `${authority}/token`,
}

function idToken(claims: Record<string, unknown>): string {
  const part = (value: unknown) =>
    Buffer.from(JSON.stringify(value)).toString("base64url")

  return `${part({ alg: "RS256" })}.${part(claims)}.signature`
}

/** Keşif belgesini verir; token ucunun yanıtını test belirler. */
function providerAnswering(token: (call: Call) => Response) {
  respondWith((call) =>
    call.url === `${authority}/.well-known/openid-configuration`
      ? json(200, discovery)
      : token(call)
  )
}

describe("provider renewal with the real oidc-client-ts", () => {
  beforeEach(() => {
    providerAnswering(() =>
      json(200, {
        access_token: "idp-access-2",
        refresh_token: "idp-refresh-2",
        token_type: "Bearer",
        expires_in: 300,
      })
    )
  })

  it("sends the refresh token grant to the provider's token endpoint and nothing else", async () => {
    const tokens = await renewProviderTokens(providerSession())

    assert.deepEqual(tokens, {
      accessToken: "idp-access-2",
      refreshToken: "idp-refresh-2",
    })

    const requests = callsTo("/token")
    assert.equal(requests.length, 1)
    assert.equal(requests[0].url, `${authority}/token`)
    assert.equal(requests[0].method, "POST")
    // Yenileme token'ı ve istemci kimliği: gizli anahtar, kapsam, eski erişim token'ı gitmez.
    assert.deepEqual(requests[0].form, {
      grant_type: "refresh_token",
      refresh_token: "idp-refresh-1",
      client_id: "shop-panel",
    })
    assert.equal(requests[0].headers.has("Authorization"), false)
    // Keşif ve token dışında bir uca (userinfo, anahtarlar, çıkış) gidilmez.
    assert.deepEqual(
      calls.map((call) => call.url.replace(authority, "")),
      ["/.well-known/openid-configuration", "/token"]
    )
  })

  it("reads the discovery document once per provider", async () => {
    await renewProviderTokens(providerSession())
    const first = callsTo("/.well-known/openid-configuration").length
    await renewProviderTokens(providerSession())

    assert.equal(callsTo("/.well-known/openid-configuration").length, first)
    assert.equal(callsTo("/token").length, 2)
  })

  it("keeps the refresh token when the provider returns none", async () => {
    providerAnswering(() =>
      json(200, { access_token: "idp-access-2", token_type: "Bearer" })
    )

    assert.deepEqual(await renewProviderTokens(providerSession()), {
      accessToken: "idp-access-2",
      refreshToken: "idp-refresh-1",
    })
  })

  it("accepts an ID token in the answer without having kept the previous one", async () => {
    providerAnswering(() =>
      json(200, {
        access_token: "idp-access-2",
        refresh_token: "idp-refresh-2",
        token_type: "Bearer",
        scope: "openid",
        id_token: idToken({
          iss: authority,
          sub: "subject-1",
          aud: "shop-panel",
        }),
      })
    )

    assert.equal(
      (await renewProviderTokens(providerSession())).accessToken,
      "idp-access-2"
    )
  })

  for (const code of [
    "invalid_grant",
    "invalid_client",
    "unauthorized_client",
  ]) {
    it(`treats ${code} as the provider's definite refusal`, async () => {
      providerAnswering(() => json(400, { error: code }))

      await assert.rejects(
        renewProviderTokens(providerSession()),
        RenewalRejected
      )
    })
  }

  const transient: [string, () => Response][] = [
    ["a 503 that is not an OAuth answer", () => json(503, { message: "down" })],
    [
      "a proxy's HTML error page",
      () => text(502, "<html>Bad Gateway</html>", "Bad Gateway"),
    ],
    [
      "temporarily_unavailable",
      () => json(503, { error: "temporarily_unavailable" }),
    ],
    ["server_error", () => json(500, { error: "server_error" })],
    ["an answer without an access token", () => json(200, {})],
  ]

  for (const [name, answer] of transient) {
    it(`does not treat ${name} as a refusal`, async () => {
      providerAnswering(answer)

      await assert.rejects(renewProviderTokens(providerSession()), (error) => {
        assert.ok(!(error instanceof RenewalRejected))
        return true
      })
    })
  }

  it("does not treat a network error as a refusal", async () => {
    providerAnswering(() => {
      throw new TypeError("fetch failed")
    })

    await assert.rejects(renewProviderTokens(providerSession()), TypeError)
  })

  it("does not treat an unreachable discovery document as a refusal", async () => {
    respondWith(() => {
      throw new TypeError("fetch failed")
    })

    await assert.rejects(
      renewProviderTokens(
        providerSession({ authority: "https://unreachable.example.com" })
      ),
      TypeError
    )
  })

  for (const [name, hangs] of [
    ["discovery document", "/.well-known/openid-configuration"],
    ["token endpoint", "/token"],
  ] as const) {
    it(`gives up on a ${name} that never answers, without ending the session`, async (t) => {
      t.mock.timers.enable({ apis: ["setTimeout"] })
      const provider = providerSession({
        authority: `https://slow-${hangs.length}.example.com`,
      })
      let waiting = 0

      respondWith((call) => {
        if (!call.url.endsWith(hangs)) {
          return json(200, {
            token_endpoint: `${provider.authority}/token`,
            authorization_endpoint: `${provider.authority}/authorize`,
          })
        }

        waiting++
        // Gerçek `fetch` gibi: yanıt gelmez, istek ancak vazgeçilince sonuçlanır.
        return new Promise<Response>((_, reject) => {
          call.signal?.addEventListener("abort", () =>
            reject(call.signal?.reason)
          )
        })
      })

      const renewal = renewProviderTokens(provider)
      const outcome = renewal.then(
        () => "renewed",
        (error: unknown) => error
      )
      await settle()
      assert.equal(waiting, 1)

      t.mock.timers.tick(9_999)
      await settle()
      t.mock.timers.tick(1)

      const error = await outcome
      assert.ok(error instanceof Error)
      assert.equal(error.name, "ErrorTimeout")
      assert.ok(!(error instanceof RenewalRejected))
    })
  }

  it("refuses a session without a refresh token before asking anybody", async () => {
    await assert.rejects(
      renewProviderTokens(providerSession({ refreshToken: "" })),
      RenewalRejected
    )

    assert.equal(calls.length, 0)
  })

  // lib/provider.ts'teki `silent_redirect_uri: ""` ayarının dayandığı davranış: yenileme token'ı olmayan bir
  // kullanıcıyla çağrılan `signinSilent` gizli bir iframe açmayı denemez, hata verir. (Burada `document` yoktur;
  // iframe yoluna girilseydi hata başka olurdu.)
  it("the library fails instead of opening an iframe when the silent address is empty", async () => {
    const memory = new Map<string, string>()
    const store = {
      getItem: (key: string) => memory.get(key) ?? null,
      setItem: (key: string, value: string) => void memory.set(key, value),
      removeItem: (key: string) => void memory.delete(key),
      clear: () => memory.clear(),
      key: (index: number) => [...memory.keys()][index] ?? null,
      get length() {
        return memory.size
      },
    }
    const manager = new UserManager({
      authority,
      client_id: "shop-panel",
      redirect_uri: "https://panel.example/login/callback",
      silent_redirect_uri: "",
      automaticSilentRenew: false,
      userStore: new WebStorageStateStore({ store }),
      stateStore: new WebStorageStateStore({ store }),
    })

    await assert.rejects(manager.signinSilent(), {
      message: "No silent_redirect_uri configured",
    })
    assert.equal(calls.length, 0)
  })
})
