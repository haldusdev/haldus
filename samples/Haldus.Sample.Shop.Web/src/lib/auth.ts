import {
  changePassword,
  getAuthConfig,
  getCurrentAccount,
  logout,
  refreshSession,
  verifyEmail,
} from "@/api/auth/auth"
import type { AccessTokenResponse } from "@/api/model"
import { renewSession, setSessionRefresher } from "@/lib/api-fetch"
import {
  completeProviderSignIn,
  forgetProviderUser,
  renewProviderTokens,
} from "@/lib/provider"
import { returnPath } from "@/lib/return-path"
import {
  getSession,
  markEmailVerified,
  reloadSession,
  renewTokens,
  signIn,
  signOut,
} from "@/lib/session"

// Süresi dolan erişim token'ı buradan yenilenir (api-fetch.ts). Parola oturumunun token'larını backend verir; kimlik
// sağlayıcı oturumununkiler sağlayıcınındır ve backend'in `/auth/refresh` ucu onları tanımaz.
setSessionRefresher(async (session) => {
  if (session.kind === "provider") {
    return renewProviderTokens(session)
  }

  const renewed = await refreshSession({ refreshToken: session.refreshToken })
  return renewed.data
})

/** Giriş veya kayıt yanıtındaki token'larla oturumu açar; hesabın kimliği backend'e sorulur. */
export async function openSession(tokens: AccessTokenResponse): Promise<void> {
  const account = await getCurrentAccount({
    headers: { Authorization: `Bearer ${tokens.accessToken}` },
  })

  signIn({
    kind: "password",
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    accountId: account.data.id,
    email: account.data.email,
    tenantId: account.data.tenantId,
    emailVerified: account.data.emailVerified,
  })
}

let completion: { url: string; result: Promise<string> } | undefined

/**
 * Kimlik sağlayıcıdan dönen adresle girişi tamamlar ve girişten sonra açılacak yolu verir. Aynı adres için bir kez
 * çalışır: yetki kodu bir kez kullanılır, sayfa ise bunu birden çok kez isteyebilir (React geliştirme kipinde
 * effect'leri iki kez çalıştırır).
 */
export function finishProviderSignIn(url: string): Promise<string> {
  if (completion?.url !== url) {
    completion = { url, result: openProviderSession(url) }
  }

  return completion.result
}

async function openProviderSession(url: string): Promise<string> {
  // Sağlayıcıyı backend tanıtır; dönen adresteki hiçbir şey hangi sağlayıcıya gidileceğini belirlemez.
  const config = await getAuthConfig()
  const provider = config.data.provider

  if (!provider) {
    throw new Error("The backend offers no identity provider.")
  }

  const signedIn = await completeProviderSignIn(provider, url)

  // Backend sağlayıcının token'ını doğrudan kabul eder ve onun için kendi token'ını vermez. Hesabı tanımıyorsa
  // (ya da token'ı reddediyorsa) oturum açılmaz ve token'lar hiçbir yere yazılmaz.
  const account = await getCurrentAccount({
    headers: { Authorization: `Bearer ${signedIn.accessToken}` },
  })

  signIn({
    kind: "provider",
    authority: provider.authority,
    clientId: provider.clientId,
    accessToken: signedIn.accessToken,
    refreshToken: signedIn.refreshToken,
    accountId: account.data.id,
    email: account.data.email,
    tenantId: account.data.tenantId,
    emailVerified: account.data.emailVerified,
  })

  return returnPath(signedIn.returnTo)
}

/**
 * E-postayı gönderilen kodla doğrular ve oturumu doğrulanmış token'larla günceller. Hesap zaten doğrulanmışsa (ör.
 * başka bir cihazda) backend kod sınamaz ve token vermez (204); güncel bilgi oturum yenilenerek alınır.
 */
export async function confirmEmail(code: string): Promise<void> {
  const before = getSession()
  const response = await verifyEmail({ code })

  // Yanıt beklenirken oturum değişmiş olabilir (başka bir sekmede çıkış, başka hesapla giriş, token yenileme);
  // depodaki güncel hal okunur.
  reloadSession()
  const current = getSession()

  if (!before || !current || current.accountId !== before.accountId) {
    // Doğrulanan hesabın token'ları başka bir hesabın oturumuna yazılmaz.
    return
  }

  if (response.status === 200 && current.refreshToken === before.refreshToken) {
    markEmailVerified(response.data.accessToken, response.data.refreshToken)
    return
  }

  // Token'lar bu arada yenilendi (yanıttakiler eskidi; yazılsalar bir sonraki yenileme reddedilirdi) ya da hesap zaten
  // doğrulanmıştı. Güncel bilgi oturum yenilenerek alınır; yenileme, 401 sonrası yenilemeyle aynı sıradan geçer.
  const session = (await renewSession()) ? getSession() : null

  if (session && session.accountId === before.accountId) {
    markEmailVerified(session.accessToken, session.refreshToken)
  }
}

/**
 * Oturumu kapatır. Backend oturumu iptal eder (token'lar artık kabul edilmez); istek başarısız olsa da token'lar
 * tarayıcıdan silinir, çünkü kullanıcı çıkmak istemiştir.
 *
 * Kimlik sağlayıcı oturumunda backend'de kapatılacak bir oturum yoktur (`/auth/logout` parola oturumları içindir):
 * yalnızca bu tarayıcıdaki oturum silinir. Sağlayıcının çıkış ucu çağrılmaz ve token'lar sağlayıcıda iptal edilmez;
 * kullanıcının sağlayıcıdaki oturumu açık kalır ve erişim token'ı süresi dolana kadar geçerlidir.
 */
export async function closeSession(): Promise<void> {
  const session = getSession()

  if (session?.kind === "provider") {
    signOut()

    try {
      await forgetProviderUser(session)
    } catch {
      // Kitaplığın belleğindeki kayıt silinemese de oturum kapanmıştır.
    }

    return
  }

  if (session) {
    try {
      // Yenileme token'ı da gönderilir: erişim token'ı artık geçmiyorsa (süresi dolmuş, yenilenemiyor) oturumu
      // sunucuda o tanıtır ve kapatır.
      await logout({ refreshToken: session.refreshToken })
    } catch {
      // Ağ hatası veya zaten kapanmış oturum: yerel çıkış yine yapılır.
    }
  }

  signOut()
}

/**
 * Parolayı değiştirir. Backend hesabın bütün oturumlarını kapatır (bu sekmeninki dahil: token'lar kopyalanmışsa
 * kopyalanan odur) ve isteği yapana yeni bir oturum açar; onun token'ları yazılır.
 */
export async function changeOwnPassword(
  currentPassword: string,
  newPassword: string
): Promise<void> {
  const before = getSession()
  const response = await changePassword({ currentPassword, newPassword })

  reloadSession()
  const current = getSession()

  // Yanıt beklenirken oturum kapandıysa veya başka bir hesaba geçtiyse yeni token'lar ona yazılmaz.
  if (before && current && current.accountId === before.accountId) {
    renewTokens(response.data.accessToken, response.data.refreshToken)
  }
}
