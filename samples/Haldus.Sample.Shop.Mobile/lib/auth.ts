import {
  changePassword,
  getCurrentAccount,
  logout,
  refreshSession,
  verifyEmail,
} from '@/api/auth/auth';
import type { AccessTokenResponse, ExternalProviderConfig } from '@/api/model';
import { renewSession, setSessionRefresher } from '@/lib/api-fetch';
import { ProviderSignInError, promptProviderSignIn, redeemProviderCode } from '@/lib/provider';
import type { ProviderPrompt } from '@/lib/provider';
import { Platform } from 'react-native';

import {
  getSession,
  markEmailVerified,
  renewTokens,
  restoreSession,
  signIn,
  signOut,
} from '@/lib/session';

// Parola oturumunun süresi dolan erişim token'ı bu istekle yenilenir (api-fetch.ts).
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
    kind: 'password',
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    accountId: account.data.id,
    email: account.data.email,
    tenantId: account.data.tenantId,
    emailVerified: account.data.emailVerified,
  });
}

/**
 * Kimlik sağlayıcıyla giriş (yetkilendirme kodu + PKCE): sağlayıcının sayfası açılır, dönen kod token'lara çevrilir
 * ve sağlayıcının erişim token'ı ile backend'e hesabın kim olduğu sorulur. Backend bu giriş için kendi token'ını
 * vermez; oturumun token'ları sağlayıcınınkilerdir. Kullanıcı vazgeçerse `cancelled` döner ve oturum açılmaz.
 *
 * Bir dokunuşun içinden çağrılmalıdır: web'de sağlayıcı açılır pencerede açılır.
 */
export async function signInWithProvider(
  provider: ExternalProviderConfig,
  prompt: ProviderPrompt
): Promise<'signedIn' | 'cancelled'> {
  const result = await promptProviderSignIn(prompt);

  if (result.type !== 'success' && result.type !== 'error') {
    return 'cancelled';
  }

  // Sağlayıcının hatası (`error=access_denied`...) ve başka bir isteğe ait dönüş (durum değeri uymuyor) buradadır.
  if (result.type === 'error' || !result.params.code) {
    throw new ProviderSignInError(result.error?.code ?? 'the response carries no code');
  }

  const tokens = await redeemProviderCode(provider, prompt, result.params.code);
  const account = await getCurrentAccount({
    headers: { Authorization: `Bearer ${tokens.accessToken}` },
  });

  await signIn({
    kind: 'provider',
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    provider: tokens.link,
    accountId: account.data.id,
    email: account.data.email,
    tenantId: account.data.tenantId,
    emailVerified: account.data.emailVerified,
  });

  return 'signedIn';
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

  // E-posta doğrulama parola hesaplarına özgüdür (backend sağlayıcı hesabına bu ucu açmaz).
  if (session?.kind === 'password' && session.accountId === before.accountId) {
    await markEmailVerified(session.accessToken, session.refreshToken);
  }
}
/**
 * Oturumu kapatır. Backend oturumu iptal eder (token'lar artık kabul edilmez); istek başarısız olsa da token'lar
 * cihazdan silinir, çünkü kullanıcı çıkmak istemiştir.
 *
 * Sağlayıcı oturumunun backend'de karşılığı yoktur; yalnızca cihazdaki token'lar silinir. Sağlayıcıdaki oturum
 * (`end_session_endpoint`) kapatılmaz ve token'lar orada iptal edilmez: süreleri dolana kadar geçerli kalırlar.
 */
export async function closeSession(): Promise<void> {
  const session = getSession();

  if (session?.kind === 'password') {
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
