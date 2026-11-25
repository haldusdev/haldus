import {
  getCurrentAccount,
  logout,
  refreshSession,
  verifyEmail,
} from "@/api/auth/auth"
import type { AccessTokenResponse } from "@/api/model"
import { renewSession, setSessionRefresher } from "@/lib/api-fetch"
import { getSession, markEmailVerified, signIn, signOut } from "@/lib/session"

// Süresi dolan erişim token'ı bu istekle yenilenir (api-fetch.ts).
setSessionRefresher(async (refreshToken) => {
  const renewed = await refreshSession({ refreshToken })
  return renewed.data
})

/** Giriş veya kayıt yanıtındaki token'larla oturumu açar; hesabın kimliği backend'e sorulur. */
export async function openSession(tokens: AccessTokenResponse): Promise<void> {
  const account = await getCurrentAccount({
    headers: { Authorization: `Bearer ${tokens.accessToken}` },
  })

  signIn({
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    accountId: account.data.id,
    email: account.data.email,
    tenantId: account.data.tenantId,
    emailVerified: account.data.emailVerified,
  })
}

/**
 * E-postayı gönderilen kodla doğrular ve oturumu doğrulanmış token'larla günceller. Hesap zaten doğrulanmışsa (ör.
 * başka bir cihazda) backend kod sınamaz ve token vermez (204); güncel bilgi oturum yenilenerek alınır.
 */
export async function confirmEmail(code: string): Promise<void> {
  const response = await verifyEmail({ code })

  if (response.status === 200) {
    markEmailVerified(response.data.accessToken, response.data.refreshToken)
    return
  }

  // Yenileme, 401 sonrası yenilemeyle aynı sıradan geçer; ikisi aynı token'ı ayrı ayrı sunmaz.
  const session = (await renewSession()) ? getSession() : null

  if (session) {
    markEmailVerified(session.accessToken, session.refreshToken)
  }
}
/**
 * Oturumu kapatır. Backend oturumu iptal eder (token'lar artık kabul edilmez); istek başarısız olsa da token'lar
 * tarayıcıdan silinir, çünkü kullanıcı çıkmak istemiştir.
 */
export async function closeSession(): Promise<void> {
  if (getSession()) {
    try {
      await logout()
    } catch {
      // Ağ hatası veya zaten kapanmış oturum: yerel çıkış yine yapılır.
    }
  }

  signOut()
}
