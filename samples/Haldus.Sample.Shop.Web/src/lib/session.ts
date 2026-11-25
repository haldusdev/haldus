import { useSyncExternalStore } from "react"

type Account = {
  accessToken: string
  refreshToken: string
  accountId: string
  email: string
  tenantId: string
  emailVerified: boolean
}

/** Backend'in e-posta ve parola girişinden verdiği oturum; token'ları backend'indir ve `/auth/refresh` ile yenilenir. */
export type PasswordSession = Account & { kind: "password" }

/**
 * Kimlik sağlayıcıyla (OpenID Connect) açılan oturum. Erişim token'ı sağlayıcınındır ve backend onu doğrudan kabul
 * eder; yenileme de sağlayıcıya sorulur (lib/provider.ts), bu yüzden token'ları veren sağlayıcı oturumda yazılıdır.
 * Sağlayıcı yenileme token'ı vermediyse `refreshToken` boştur.
 */
export type ProviderSession = Account & {
  kind: "provider"
  authority: string
  clientId: string
}

/**
 * Oturum: token'lar ve hesabın kimliği. Erişim token'ı her istekte `Authorization` başlığında gider; süresi dolunca
 * yenileme token'ı ile yenisi alınır (api-fetch.ts).
 *
 * Token'lar `localStorage` içinde durur; bu, sayfaya sızan bir betiğin onları okuyabileceği anlamına gelir. Örnek
 * için kabul edilmiş bir ödünleşimdir; üretimde token'ları sayfanın betiğinden saklayan bir düzen (`HttpOnly` çerez)
 * tercih edilir. Kimlik sağlayıcıyla giriş bunu değiştirmez: sağlayıcının token'ları da burada, aynı kayıtta durur.
 */
export type Session = PasswordSession | ProviderSession

const STORAGE_KEY = "haldus-shop.session"

function filled(value: unknown): value is string {
  return typeof value === "string" && value.length > 0
}

/**
 * Oturumu alanları hep aynı sırada olan bir nesneye çevirir. Depodaki ve bellekteki hal metin olarak
 * karşılaştırılır (reloadSession); alan sırası farklı iki eşit oturum "değişmiş" sayılmasın diye.
 */
function canonical(session: Session): Session {
  const account = {
    accessToken: session.accessToken,
    refreshToken: session.refreshToken,
    accountId: session.accountId,
    email: session.email,
    tenantId: session.tenantId,
    emailVerified: session.emailVerified,
  }

  return session.kind === "provider"
    ? {
        kind: "provider",
        ...account,
        authority: session.authority,
        clientId: session.clientId,
      }
    : { kind: "password", ...account }
}

function read(): Session | null {
  try {
    const parsed: unknown = JSON.parse(
      localStorage.getItem(STORAGE_KEY) ?? "null"
    )

    if (typeof parsed !== "object" || parsed === null) {
      return null
    }

    const record: Record<string, unknown> = { ...parsed }
    const { accessToken, refreshToken, accountId, email, tenantId } = record

    if (
      !filled(accessToken) ||
      typeof refreshToken !== "string" ||
      !filled(accountId) ||
      !filled(email) ||
      !filled(tenantId)
    ) {
      return null
    }

    const account = {
      accessToken,
      refreshToken,
      accountId,
      email,
      tenantId,
      emailVerified: record.emailVerified === true,
    }

    // Tür alanı eklenmeden önce yazılmış oturumlar parola oturumudur. Parola oturumu yenileme token'ı olmadan olmaz.
    if (record.kind === undefined || record.kind === "password") {
      return filled(refreshToken) ? { kind: "password", ...account } : null
    }

    if (
      record.kind === "provider" &&
      filled(record.authority) &&
      filled(record.clientId)
    ) {
      return {
        kind: "provider",
        ...account,
        authority: record.authority,
        clientId: record.clientId,
      }
    }
  } catch {
    // Bozuk kayıt oturum yok sayılır.
  }

  // Tanınmayan türdeki bir kayıt da oturum sayılmaz: nasıl yenileneceği bilinmeyen token'lar kullanılmaz.
  return null
}

let current = read()
const listeners = new Set<() => void>()

function publish(next: Session | null) {
  current = next ? canonical(next) : null

  if (current) {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(current))
  } else {
    localStorage.removeItem(STORAGE_KEY)
  }

  listeners.forEach((listener) => listener())
}

export function getSession(): Session | null {
  return current
}

export function signIn(session: Session) {
  publish(session)
}

/** Yenilenen token'ları yazar; hesap aynı kalır. */
export function renewTokens(accessToken: string, refreshToken: string) {
  if (current) {
    publish({ ...current, accessToken, refreshToken })
  }
}

/** E-posta doğrulanınca backend'in verdiği yeni token'ları yazar. */
export function markEmailVerified(accessToken: string, refreshToken: string) {
  if (current) {
    publish({ ...current, accessToken, refreshToken, emailVerified: true })
  }
}

export function signOut() {
  publish(null)
}

export function subscribeSession(listener: () => void) {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** Oturumu depodan yeniden okur; başka bir sekmenin yazdığı değişikliği olayını beklemeden görmek için. */
export function reloadSession() {
  const stored = read()

  if (JSON.stringify(stored) !== JSON.stringify(current)) {
    current = stored
    listeners.forEach((listener) => listener())
  }
}

// Başka bir sekme oturumu değiştirdiyse (giriş, çıkış, token yenileme) bu sekme de onu izler. Yenileme token'ı bir kez
// kullanılır: eskisini sunan sekme, backend'in oturumu çalınmış sayıp kapatmasına yol açardı.
window.addEventListener("storage", (event) => {
  if (event.key === STORAGE_KEY) {
    current = read()
    listeners.forEach((listener) => listener())
  }
})

export function useSession(): Session | null {
  // Üçüncü argüman yalnızca tarayıcı dışında (sunucuda çizilen testlerde) okunur.
  return useSyncExternalStore(subscribeSession, getSession, getSession)
}
