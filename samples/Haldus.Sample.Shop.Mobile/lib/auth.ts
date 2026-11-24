import { getCurrentAccount, refreshSession } from '@/api/auth/auth';
import type { AccessTokenResponse } from '@/api/model';
import { setSessionRefresher } from '@/lib/api-fetch';
import { signIn } from '@/lib/session';

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
  });
}