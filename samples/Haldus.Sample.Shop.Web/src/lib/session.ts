import { useSyncExternalStore } from "react"

/**
 * Oturum: backend'in verdiği token'lar ve hesabın kimliği. Erişim token'ı her istekte `Authorization` başlığında
 * gider; süresi dolunca yenileme token'ı ile yenisi alınır (api-fetch.ts).
 *
 * Token'lar `localStorage` içinde durur; bu, sayfaya sızan bir betiğin onları okuyabileceği anlamına gelir. Örnek
 * için kabul edilmiş bir ödünleşimdir; üretimde `HttpOnly` çerez veya bir kimlik sağlayıcının akışı tercih edilir.
 */
export type Session = {
  accessToken: string
  refreshToken: string
  accountId: string
  email: string
  tenantId: string
  emailVerified: boolean
}

const STORAGE_KEY = "haldus-shop.session"
const FIELDS = [
  "accessToken",
  "refreshToken",
  "accountId",
  "email",
  "tenantId",
] as const

function read(): Session | null {
  try {
    const parsed: unknown = JSON.parse(
      localStorage.getItem(STORAGE_KEY) ?? "null"
    )

    if (typeof parsed !== "object" || parsed === null) {
      return null
    }

    const record: Record<string, unknown> = { ...parsed }
    const values = FIELDS.map((field) => record[field])

    if (
      values.every(
        (value): value is string =>
          typeof value === "string" && value.length > 0
      )
    ) {
      const [accessToken, refreshToken, accountId, email, tenantId] = values
      return {
        accessToken,
        refreshToken,
        accountId,
        email,
        tenantId,
        emailVerified: record.emailVerified === true,
      }
    }
  } catch {
    // Bozuk kayıt oturum yok sayılır.
  }

  return null
}

let current = read()
const listeners = new Set<() => void>()

function publish(next: Session | null) {
  current = next

  if (next) {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(next))
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

// Başka bir sekme oturumu değiştirdiyse (giriş, çıkış, token yenileme) bu sekme de onu izler. Yenileme token'ı bir kez
// kullanılır: eskisini sunan sekme, backend'in oturumu çalınmış sayıp kapatmasına yol açardı.
window.addEventListener("storage", (event) => {
  if (event.key === STORAGE_KEY) {
    current = read()
    listeners.forEach((listener) => listener())
  }
})

export function useSession(): Session | null {
  return useSyncExternalStore(subscribeSession, getSession)
}
