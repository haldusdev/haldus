import type { Session } from "@/lib/session"

export const STORAGE_KEY = "haldus-shop.session"

export function session(overrides: Partial<Session> = {}): Session {
  return {
    accessToken: "access-1",
    refreshToken: "refresh-1",
    accountId: "account-1",
    email: "ada@example.com",
    tenantId: "tenant-1",
    emailVerified: false,
    ...overrides,
  }
}

export const unauthorized = {
  type: "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  title: "Unauthorized",
  status: 401,
}
