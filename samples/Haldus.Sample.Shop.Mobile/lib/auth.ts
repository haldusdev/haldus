import {
  changePassword,
  getCurrentAccount,
  logout,
  refreshSession,
  verifyEmail,
} from '@/api/auth/auth';
import type { AccessTokenResponse } from '@/api/model';
import { renewSession, setSessionRefresher } from '@/lib/api-fetch';
import { Platform } from 'react-native';

import {
  getSession,
  markEmailVerified,
  renewTokens,
  restoreSession,
  signIn,
  signOut,
} from '@/lib/session';

// Süresi dolan erişim token'ı bu istekle yenilenir (api-fetch.ts).
setSessionRefresher(async (refreshToken) => {
  const renewed = await refreshSession({ refreshToken });
  return renewed.data;
});

/** Giriş veya kayıt yanıtındaki token'larla oturumu açar; hesabın kimliği backend'e sorulur. */
export async function openSession(tokens: AccessTokenResponse): Promise<void> {
  const account = await getCurrentAccount({
    headers: { Authorization: `Bearer ${tokens.accessToken}` },
  });

  await signIn({
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    accountId: account.data.id,
    email: account.data.email,
    tenantId: account.data.tenantId,
    emailVerified: account.data.emailVerified,
  });
}

/**
 * E-postayı gönderilen kodla doğrular ve oturumu doğrulanmış token'larla günceller. Hesap zaten doğrulanmışsa (ör.
 * başka bir cihazda) backend kod sınamaz ve token vermez (204); güncel bilgi oturum yenilenerek alınır.
 */
export async function confirmEmail(code: string): Promise<void> {
  const before = getSession();
  const response = await verifyEmail({ code });

  // Yanıt beklenirken oturum değişmiş olabilir (çıkış, başka hesapla giriş, token yenileme). Tarayıcıda bunu başka
  // bir sekme de yapabilir; depodaki güncel hal okunur.
  if (Platform.OS === 'web') {
    await restoreSession();
  }

  const current = getSession();

  if (!before || !current || current.accountId !== before.accountId) {
    // Doğrulanan hesabın token'ları başka bir hesabın oturumuna yazılmaz.
    return;
  }

  if (response.status === 200 && current.refreshToken === before.refreshToken) {
    await markEmailVerified(response.data.accessToken, response.data.refreshToken);
    return;
  }

  // Token'lar bu arada yenilendi (yanıttakiler eskidi; yazılsalar bir sonraki yenileme reddedilirdi) ya da hesap zaten
  // doğrulanmıştı. Güncel bilgi oturum yenilenerek alınır; yenileme, 401 sonrası yenilemeyle aynı sıradan geçer.
  const session = (await renewSession()) ? getSession() : null;

  if (session && session.accountId === before.accountId) {
    await markEmailVerified(session.accessToken, session.refreshToken);
  }
}
/**
 * Oturumu kapatır. Backend oturumu iptal eder (token'lar artık kabul edilmez); istek başarısız olsa da token'lar
 * cihazdan silinir, çünkü kullanıcı çıkmak istemiştir.
 */
export async function closeSession(): Promise<void> {
  const session = getSession();

  if (session) {
    try {
      // Yenileme token'ı da gönderilir: erişim token'ı artık geçmiyorsa (süresi dolmuş, yenilenemiyor) oturumu
      // sunucuda o tanıtır ve kapatır.
      await logout({ refreshToken: session.refreshToken });
    } catch {
      // Ağ hatası veya zaten kapanmış oturum: yerel çıkış yine yapılır.
    }
  }

  await signOut();
}

/**
 * Parolayı değiştirir. Backend hesabın bütün oturumlarını kapatır (bu cihazınki dahil: token'lar kopyalanmışsa
 * kopyalanan odur) ve isteği yapana yeni bir oturum açar; onun token'ları yazılır.
 */
export async function changeOwnPassword(
  currentPassword: string,
  newPassword: string
): Promise<void> {
  const before = getSession();
  const response = await changePassword({ currentPassword, newPassword });

  if (Platform.OS === 'web') {
    await restoreSession();
  }

  const current = getSession();

  // Yanıt beklenirken oturum kapandıysa veya başka bir hesaba geçtiyse yeni token'lar ona yazılmaz.
  if (before && current && current.accountId === before.accountId) {
    await renewTokens(response.data.accessToken, response.data.refreshToken);
  }
}
