import type { PasswordSession, ProviderSession } from "@/lib/session"

export const STORAGE_KEY = "haldus-shop.session"

export function session(
  overrides: Partial<PasswordSession> = {}
): PasswordSession {
  return {
    kind: "password",
    accessToken: "access-1",
    refreshToken: "refresh-1",
    accountId: "account-1",
    email: "ada@example.com",
    tenantId: "tenant-1",
    emailVerified: false,
    ...overrides,
  }
}

/** Kimlik sağlayıcıyla açılmış bir oturum; token'ları sağlayıcınındır. */
export function providerSession(
  overrides: Partial<ProviderSession> = {}
): ProviderSession {
  return {
    kind: "provider",
    accessToken: "idp-access-1",
    refreshToken: "idp-refresh-1",
    accountId: "account-1",
    email: "ada@example.com",
    tenantId: "tenant-1",
    emailVerified: true,
    authority: "https://id.example.com",
    clientId: "shop-panel",
    ...overrides,
  }
}

export const unauthorized = {
  type: "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  title: "Unauthorized",
  status: 401,
}
