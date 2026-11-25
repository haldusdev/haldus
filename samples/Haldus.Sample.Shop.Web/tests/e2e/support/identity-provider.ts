// Sahte mağazanın aynı adresten sunduğu sahte kimlik sağlayıcı (OpenID Connect). Giriş sayfası yoktur: yetkilendirme
// ucu, seçilmiş kullanıcı adına hemen kodla geri yönlendirir. Geri kalanı gerçek bir sağlayıcının denetlediklerini
// denetler: istemci, kayıtlı yönlendirme adresi, PKCE, kodun ve yenileme token'ının bir kez kullanılması.
//
// Uçlar `oidc-client-ts` 3.5.0'ın kaynağından çıkarıldı: kitaplık keşif belgesinden yalnızca `authorization_endpoint`
// ve `token_endpoint` alanlarını okur, kimlik token'ının imzasını doğrulamaz (anahtarları istemez) ve `userinfo` ucunu
// ancak `loadUserInfo` açıksa çağırır. Burada olmayan bir uca gelen istek 404 alır ve o da kaydedilir; testler
// panelin yalnızca beklenen uçlara gittiğini böyle görür.
import { createHash } from "node:crypto"
import type { IncomingMessage } from "node:http"

export const PROVIDER_PATH = "/idp"
export const CLIENT_ID = "haldus-shop-panel"
export const CALLBACK_PATH = "/login/callback"
export const SCOPES = "openid profile email offline_access"

export type ProviderRequest = {
  method: string
  /** Sağlayıcının köküne göre yol (`/token`). */
  path: string
  query: URLSearchParams
  headers: IncomingMessage["headers"]
  /** Form gövdesinin alanları (`application/x-www-form-urlencoded`). */
  form: URLSearchParams
}

export type ProviderReply = {
  status: number
  headers?: Record<string, string>
  body?: unknown
}

type Code = { subject: string; challenge: string; redirectUri: string }

function oauthError(status: number, error: string): ProviderReply {
  return {
    status,
    body: { error, error_description: `Fake provider: ${error}` },
  }
}

function base64Url(value: string | Buffer): string {
  return Buffer.from(value).toString("base64url")
}

export class IdentityProvider {
  readonly requests: ProviderRequest[] = []

  /** Mağazanın adresi; sunucu başlayınca yazılır. */
  origin = ""

  /** Giriş yapan kullanıcının kimliği (`sub`). Mağaza onu bir hesaba eşler; eşleyemezse token'ı reddeder. */
  subject = "nobody"

  /** `deny`: kullanıcı izni reddetmiş gibi, hata ile geri yönlendirir. */
  authorizeBehaviour: "grant" | "deny" = "grant"

  /** Yenileme isteğine verilecek yanıt; `fail` geçici bir arızadır (503). */
  refreshBehaviour: "rotate" | "reject" | "fail" = "rotate"

  /** Kapalıysa sağlayıcı hiç yenileme token'ı vermez. */
  issuesRefreshTokens = true

  private readonly codes = new Map<string, Code>()
  private readonly access = new Map<string, string>()
  private readonly refresh = new Map<string, string>()
  private counter = 0

  get authority(): string {
    return `${this.origin}${PROVIDER_PATH}`
  }

  /** Erişim token'ının sahibi; token tanınmıyorsa (süresi dolmuşsa) `undefined`. */
  subjectOf(accessToken: string): string | undefined {
    return this.access.get(accessToken)
  }

  expireAccessTokens() {
    this.access.clear()
  }

  revokeRefreshTokens() {
    this.refresh.clear()
  }

  sent(method: string, route: string): ProviderRequest[] {
    return this.requests.filter(
      (request) => request.method === method && request.path === route
    )
  }

  /** Token ucuna gelen, verilen türdeki (`grant_type`) istekler. */
  grants(type: "authorization_code" | "refresh_token"): ProviderRequest[] {
    return this.sent("POST", "/token").filter(
      (request) => request.form.get("grant_type") === type
    )
  }

  handle(request: ProviderRequest): ProviderReply {
    this.requests.push(request)

    switch (`${request.method} ${request.path}`) {
      case "GET /.well-known/openid-configuration":
        return {
          status: 200,
          body: {
            issuer: this.authority,
            authorization_endpoint: `${this.authority}/authorize`,
            token_endpoint: `${this.authority}/token`,
            jwks_uri: `${this.authority}/jwks`,
            userinfo_endpoint: `${this.authority}/userinfo`,
            end_session_endpoint: `${this.authority}/logout`,
            response_types_supported: ["code"],
            grant_types_supported: ["authorization_code", "refresh_token"],
            subject_types_supported: ["public"],
            id_token_signing_alg_values_supported: ["RS256"],
            code_challenge_methods_supported: ["S256"],
            token_endpoint_auth_methods_supported: ["none"],
          },
        }

      case "GET /authorize":
        return this.authorize(request.query)

      case "POST /token":
        return this.token(request)

      default:
        return oauthError(404, "not_found")
    }
  }

  private authorize(query: URLSearchParams): ProviderReply {
    const redirectUri = query.get("redirect_uri") ?? ""

    // İstemci veya yönlendirme adresi tanınmıyorsa geri yönlendirilmez (kod başka bir adrese gönderilmez).
    if (
      query.get("client_id") !== CLIENT_ID ||
      redirectUri !== `${this.origin}${CALLBACK_PATH}`
    ) {
      return oauthError(400, "invalid_request")
    }

    const back = new URL(redirectUri)
    const state = query.get("state")

    if (state) {
      back.searchParams.set("state", state)
    }

    const challenge = query.get("code_challenge")
    const scopes = (query.get("scope") ?? "").split(" ")

    if (
      query.get("response_type") !== "code" ||
      !state ||
      !challenge ||
      query.get("code_challenge_method") !== "S256" ||
      !scopes.includes("openid")
    ) {
      back.searchParams.set("error", "invalid_request")
    } else if (this.authorizeBehaviour === "deny") {
      back.searchParams.set("error", "access_denied")
      back.searchParams.set("error_description", "The user denied access.")
    } else {
      const code = `code-${++this.counter}`
      this.codes.set(code, { subject: this.subject, challenge, redirectUri })
      back.searchParams.set("code", code)
    }

    return { status: 302, headers: { location: back.href } }
  }

  private token(request: ProviderRequest): ProviderReply {
    const { form } = request

    if (
      !request.headers["content-type"]?.startsWith(
        "application/x-www-form-urlencoded"
      )
    ) {
      return oauthError(400, "invalid_request")
    }

    // Herkese açık istemci: kimliğini yalnızca `client_id` ile bildirir.
    if (form.get("client_id") !== CLIENT_ID) {
      return oauthError(401, "invalid_client")
    }

    if (form.get("grant_type") === "authorization_code") {
      const value = form.get("code") ?? ""
      const code = this.codes.get(value)
      // Kod bir kez kullanılır; doğrulanamayan bir deneme de onu harcar.
      this.codes.delete(value)

      const challenge = createHash("sha256")
        .update(form.get("code_verifier") ?? "")
        .digest("base64url")

      return code &&
        code.redirectUri === form.get("redirect_uri") &&
        code.challenge === challenge
        ? { status: 200, body: this.issue(code.subject) }
        : oauthError(400, "invalid_grant")
    }

    if (form.get("grant_type") === "refresh_token") {
      if (this.refreshBehaviour === "fail") {
        return { status: 503, body: { message: "Service unavailable" } }
      }

      const value = form.get("refresh_token") ?? ""
      const subject = this.refresh.get(value)

      if (!subject || this.refreshBehaviour === "reject") {
        return oauthError(400, "invalid_grant")
      }

      // Yenileme token'ı bir kez kullanılır.
      this.refresh.delete(value)
      return { status: 200, body: this.issue(subject) }
    }

    return oauthError(400, "unsupported_grant_type")
  }

  /** Kullanıcı için geçerli token'lar üretir (token ucunun yanıtındaki gibi). */
  issue(subject: string): Record<string, unknown> {
    const serial = ++this.counter
    const now = Math.floor(Date.now() / 1000)
    const accessToken = `idp-access-${serial}`
    const claims = {
      iss: this.authority,
      sub: subject,
      aud: CLIENT_ID,
      iat: now,
      exp: now + 300,
    }

    this.access.set(accessToken, subject)

    const tokens: Record<string, unknown> = {
      token_type: "Bearer",
      access_token: accessToken,
      expires_in: 300,
      scope: SCOPES,
      // İmza sahtedir: kitaplık kimlik token'ını çözer ama imzasını doğrulamaz.
      id_token: [
        base64Url(JSON.stringify({ alg: "RS256", typ: "JWT" })),
        base64Url(JSON.stringify(claims)),
        base64Url("not-a-signature"),
      ].join("."),
    }

    if (this.issuesRefreshTokens) {
      const refreshToken = `idp-refresh-${serial}`
      this.refresh.set(refreshToken, subject)
      tokens.refresh_token = refreshToken
    }

    return tokens
  }
}
