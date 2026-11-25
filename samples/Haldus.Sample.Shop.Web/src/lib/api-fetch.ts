import i18n from "@/i18n"
import { getSession, reloadSession, renewTokens, signOut } from "@/lib/session"
import type { Session } from "@/lib/session"

/** Backend'in adresi. Geliştirmede Vite bu öneki örnek backend'e yönlendirir (vite.config.ts). */
const API_BASE: string = import.meta.env.VITE_API_BASE ?? "/api"

const CREDENTIAL_ROUTES = ["/auth/login", "/auth/register", "/auth/refresh"]

type Tokens = { accessToken: string; refreshToken: string }

/**
 * Yenileme isteğini yapan işlev. Üretilen istemci bu dosyayı içe aktardığı için burada doğrudan çağrılamaz
 * (döngüsel bağımlılık olurdu); `lib/auth.ts` onu açılışta buraya verir.
 */
type Refresher = (refreshToken: string) => Promise<Tokens>

let refresher: Refresher | undefined
let renewal: Promise<boolean> | undefined

export function setSessionRefresher(next: Refresher) {
  refresher = next
}

/**
 * orval'ın ürettiği her istek buradan geçer (orval.config.ts: `override.mutator`). Üç şey ekler: oturumun
 * token'ı, arayüzün dili (backend mesajlarını o dilde döndürür) ve hata davranışı.
 *
 * Başarısız yanıtta gövde (ProblemDetails) fırlatılır; böylece TanStack Query isteği hatalı sayar ve
 * üretilen hook'ların hata tipi (`ProblemDetails | HttpValidationProblemDetails`) gerçekten gelen değerle eşleşir.
 */
export const apiFetch = <T>(url: string, options: RequestInit): Promise<T> =>
  request<T>(url, options, true)

async function request<T>(
  url: string,
  options: RequestInit,
  mayRenew: boolean
): Promise<T> {
  const headers = new Headers(options.headers)
  headers.set("Accept-Language", i18n.language)

  const session = getSession()
  // Giriş, kayıt ve yenileme token istemez; süresi dolmuş bir token onlara eklenmez.
  const credentialRoute = CREDENTIAL_ROUTES.some((route) =>
    url.startsWith(route)
  )

  if (session && !credentialRoute && !headers.has("Authorization")) {
    headers.set("Authorization", `Bearer ${session.accessToken}`)
  }

  const response = await fetch(`${API_BASE}${url}`, { ...options, headers })

  // Erişim token'ının süresi dolmuş olabilir: bir kez yenilenir ve istek yinelenir. Giriş ve yenileme uçlarının kendi
  // 401 yanıtı (yanlış parola, geçersiz yenileme token'ı) yenileme nedeni değildir.
  if (
    response.status === 401 &&
    mayRenew &&
    session &&
    !credentialRoute &&
    (await renew(session))
  ) {
    return request<T>(url, options, false)
  }

  const text = [204, 205, 304].includes(response.status)
    ? ""
    : await response.text()
  const data = parse(text)

  if (!response.ok) {
    // Bir Error değil, sözleşmedeki ProblemDetails gövdesi fırlatılır (üretilen hata tipiyle aynı biçim).
    throw typeof data === "object" && data !== null
      ? data
      : { status: response.status, title: response.statusText }
  }

  return { data, status: response.status, headers: response.headers } as T
}

/**
 * Aynı anda 401 alan istekler tek bir yenilemeyi bekler. Yenileme token'ı bir kez kullanılır: backend eskisini
 * (kısa bir toleransın dışında) yeniden görürse oturumu çalınmış sayıp kapatır. Bu yüzden yenileme sekmeler arasında
 * da sıraya girer (Web Locks) ve kilidi alan sekme önce başka bir sekmenin yenileyip yenilemediğine bakar.
 */
function renew(sent: Session): Promise<boolean> {
  renewal ??= exclusively(async () => {
    reloadSession()
    const current = getSession()

    if (!current) {
      return false
    }

    // Başka bir istek veya başka bir sekme bu arada yeniledi.
    return current.accessToken !== sent.accessToken
      ? true
      : refresh(current.refreshToken)
  }).finally(() => {
    renewal = undefined
  })

  return renewal
}

/** Oturumu şimdi yeniler (ör. hesabın bilgisi değiştiğinde); süregelen bir yenileme varsa onu bekler. */
export function renewSession(): Promise<boolean> {
  const session = getSession()
  return session ? renew(session) : Promise.resolve(false)
}

function exclusively<T>(work: () => Promise<T>): Promise<T> {
  return "locks" in navigator
    ? navigator.locks.request("haldus-shop.session-refresh", work)
    : work()
}
async function refresh(refreshToken: string): Promise<boolean> {
  if (!refresher) {
    return false
  }

  try {
    const tokens = await refresher(refreshToken)
    renewTokens(tokens.accessToken, tokens.refreshToken)
    return true
  } catch (error) {
    // Backend yenilemeyi reddettiyse oturum bitmiştir; ağ hatasında oturum korunur ve istek hatasıyla döner.
    if (typeof error === "object" && error !== null && "status" in error) {
      signOut()
    }

    return false
  }
}

function parse(text: string): unknown {
  if (!text) {
    return undefined
  }

  try {
    return JSON.parse(text)
  } catch {
    // JSON olmayan bir gövde (ör. bir vekil sunucunun hata sayfası) içerik olarak taşınmaz.
    return undefined
  }
}
