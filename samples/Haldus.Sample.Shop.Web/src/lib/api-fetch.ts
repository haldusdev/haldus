import i18n from "@/i18n"
import { getSession, reloadSession, renewTokens, signOut } from "@/lib/session"
import type { Session } from "@/lib/session"

/** Backend'in adresi. Geliştirmede Vite bu öneki örnek backend'e yönlendirir (vite.config.ts). */
const API_BASE: string = import.meta.env.VITE_API_BASE ?? "/api"

// Oturumun token'ını taşımayan uçlar: giriş, kayıt, yenileme ve giriş ekranının sorduğu yapılandırma.
const TOKENLESS_ROUTES = [
  "/auth/login",
  "/auth/register",
  "/auth/refresh",
  "/auth/config",
]

export type Tokens = { accessToken: string; refreshToken: string }

/**
 * Oturumun token'larını yenileyen işlev: parola oturumunda backend'e, kimlik sağlayıcı oturumunda sağlayıcıya sorar.
 * Üretilen istemci bu dosyayı içe aktardığı için burada doğrudan çağrılamaz (döngüsel bağımlılık olurdu);
 * `lib/auth.ts` onu açılışta buraya verir.
 */
type Refresher = (session: Session) => Promise<Tokens>

/**
 * Yenilemenin kesin reddi: token'ları veren taraf oturumu artık yenilemiyor (ör. sağlayıcının `invalid_grant`
 * yanıtı). Oturumu yalnızca bu ve backend'in 400 / 401 yanıtı bitirir; öteki her hata geçici sayılır.
 */
export class RenewalRejected extends Error {
  override readonly name = "RenewalRejected"
}

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
  mayRenew: boolean,
  renewed = false
): Promise<T> {
  const headers = new Headers(options.headers)
  headers.set("Accept-Language", i18n.language)

  const session = getSession()
  // Bu uçlar token istemez; süresi dolmuş bir token onlara eklenmez.
  const tokenless = TOKENLESS_ROUTES.some((route) => url.startsWith(route))

  // Çağıran kendi token'ını verdiyse (ör. girişten hemen sonra hesabı sormak) oturumunki eklenmez ve o isteğin 401'i
  // oturumu yenilemez: yinelenen istek yine çağıranın token'ını taşırdı ve bir yenileme boşa harcanırdı.
  const usesSession =
    session !== null && !tokenless && !headers.has("Authorization")

  if (session && usesSession) {
    headers.set("Authorization", `Bearer ${session.accessToken}`)
  }

  const response = await fetch(`${API_BASE}${url}`, { ...options, headers })

  // Erişim token'ının süresi dolmuş olabilir: bir kez yenilenir ve istek yinelenir. Giriş ve yenileme uçlarının kendi
  // 401 yanıtı (yanlış parola, geçersiz yenileme token'ı) yenileme nedeni değildir.
  if (
    response.status === 401 &&
    mayRenew &&
    session &&
    usesSession &&
    (await renew(session))
  ) {
    return request<T>(url, options, false, true)
  }

  // Sağlayıcı token'ı yeniledi ama backend yenisini de kabul etmiyor (hesap açılamıyor, audience değişti, sağlayıcıyla
  // giriş kapatıldı). Oturum kapatılmazsa her istek sağlayıcıda bir yenileme token'ı harcar ve arayüz "giriş yapılmış"
  // görünmeye devam eder. Parola oturumunda bu durum oluşmaz: token'ı veren de kabul eden de backend'dir.
  if (
    response.status === 401 &&
    renewed &&
    session?.kind === "provider" &&
    usesSession &&
    getSession()?.accessToken === session.accessToken
  ) {
    signOut()
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
 * da sıraya girer (Web Locks) ve kilidi alan sekme önce başka bir sekmenin yenileyip yenilemediğine bakar. Kimlik
 * sağlayıcılar da yenileme token'ını çoğunlukla bir kez kullandırır; sağlayıcı oturumu aynı sıradan geçer.
 */
function renew(sent: Session): Promise<boolean> {
  renewal ??= exclusively(async () => {
    reloadSession()
    const current = getSession()

    // Oturum bu arada kapanmış veya başka bir hesaba geçmiş olabilir; o zaman bu isteğin yenileyeceği bir şey yoktur.
    if (!current || current.accountId !== sent.accountId) {
      return false
    }

    // Başka bir istek veya başka bir sekme bu arada yeniledi.
    return current.accessToken !== sent.accessToken ? true : refresh(current)
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

async function refresh(sent: Session): Promise<boolean> {
  if (!refresher) {
    return false
  }

  // Yanıt beklenirken oturum değişebilir (çıkış, başka hesapla giriş, başka bir sekmenin yenilemesi); sonuç yalnızca
  // hâlâ aynı oturuma uygulanır. Başka bir sekmenin yazdığı değişiklik, olayı henüz gelmemiş olsa da görülsün diye
  // depo yeniden okunur. Yalnızca yenileme token'ına bakmak yetmez: yenileme token'ı vermeyen bir sağlayıcının iki
  // ayrı oturumunda o alan aynıdır (boş).
  const fate = (): "same" | "renewed" | "gone" => {
    reloadSession()
    const current = getSession()

    if (!current || current.accountId !== sent.accountId) {
      return "gone"
    }

    return current.kind === sent.kind &&
      current.accessToken === sent.accessToken &&
      current.refreshToken === sent.refreshToken
      ? "same"
      : "renewed"
  }

  try {
    const tokens = await refresher(sent)

    switch (fate()) {
      case "same":
        renewTokens(tokens.accessToken, tokens.refreshToken)
        return true
      case "renewed":
        // Aynı hesabın oturumu bu arada başka bir yoldan yenilendi; istek onun token'ıyla yinelenir. Bu yanıttakiler
        // yazılmaz: yenileme token'ını döndüren bir düzende daha yeni olanı ezerlerdi.
        return true
      default:
        // Yenilenen token'lar başka bir hesabın oturumuna yazılmaz.
        return false
    }
  } catch (error) {
    // 409: oturumu az önce başka bir istek (başka bir sekme) yeniledi. Backend bu isteğe token vermez ama oturumu da
    // kapatmaz; yeni token'lar o sekme depoya yazınca alınır. Oturum kapatılmaz: yazma henüz gerçekleşmemiş olabilir.
    if (isRenewedElsewhere(error)) {
      return adoptRenewal(sent)
    }

    const now = fate()

    if (now === "renewed") {
      return true
    }

    // Oturumu yalnızca token'ları verenin reddi bitirir (backend'in 400 / 401 yanıtı, sağlayıcının kesin reddi). Ağ
    // hatasında, sunucu hatasında veya bir vekilin hata sayfasında (5xx) oturum korunur ve istek hatasıyla döner;
    // yoksa geçici bir arıza herkesi oturumdan atardı.
    if (isRejection(error) && now === "same") {
      signOut()
    }

    return false
  }
}

function isRenewedElsewhere(error: unknown): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    "status" in error &&
    error.status === 409
  )
}

/** Öteki sekmenin yenilediği oturumu depodan alır; yazılması için kısa bir süre bekler. */
async function adoptRenewal(sent: Session): Promise<boolean> {
  for (let attempt = 0; attempt < 5; attempt++) {
    reloadSession()
    const current = getSession()

    if (
      current &&
      current.accountId === sent.accountId &&
      current.refreshToken !== sent.refreshToken
    ) {
      return true
    }

    await new Promise((resolve) => setTimeout(resolve, 100))
  }

  return false
}

function isRejection(error: unknown): boolean {
  return (
    error instanceof RenewalRejected ||
    (typeof error === "object" &&
      error !== null &&
      "status" in error &&
      (error.status === 400 || error.status === 401))
  )
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
