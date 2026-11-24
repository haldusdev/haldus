import { useSyncExternalStore } from "react"

/**
 * Demo kimliği. Örnek backend kullanıcıyı ve kiracıyı istek başlıklarından okur (DemoIdentity); parola veya token
 * yoktur. Gerçek bir uygulamada burada kimlik sağlayıcının oturumu durur.
 */
export type Session = {
  userId: string
  tenantId: string
}

const STORAGE_KEY = "haldus-shop.session"
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function isGuid(value: string): boolean {
  return GUID.test(value)
}

function read(): Session | null {
  try {
    const parsed: unknown = JSON.parse(
      localStorage.getItem(STORAGE_KEY) ?? "null"
    )

    if (
      typeof parsed === "object" &&
      parsed !== null &&
      "userId" in parsed &&
      "tenantId" in parsed &&
      typeof parsed.userId === "string" &&
      typeof parsed.tenantId === "string" &&
      parsed.userId.length > 0 &&
      isGuid(parsed.tenantId)
    ) {
      return { userId: parsed.userId, tenantId: parsed.tenantId }
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
  listeners.forEach((listener) => listener())
}

export function getSession(): Session | null {
  return current
}

export function signIn(session: Session) {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(session))
  publish(session)
}

export function signOut() {
  localStorage.removeItem(STORAGE_KEY)
  publish(null)
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function useSession(): Session | null {
  return useSyncExternalStore(subscribe, getSession)
}
