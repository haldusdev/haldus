import { useSyncExternalStore } from 'react';

import { Platform } from 'react-native';

import { readItem, removeItem, writeItem } from '@/lib/storage';

/**
 * Oturum: backend'in verdiği token'lar ve hesabın kimliği. Erişim token'ı her istekte `Authorization` başlığında
 * gider; süresi dolunca yenileme token'ı ile yenisi alınır (api-fetch.ts). Cihazda işletim sisteminin güvenli
 * deposunda saklanır (storage.ts).
 */
export type Session = {
  accessToken: string;
  refreshToken: string;
  accountId: string;
  email: string;
  tenantId: string;
  emailVerified: boolean;
};

// Güvenli depo büyük değerleri her platformda kabul etmez (yaklaşık 2 KB); token'lar bu yüzden ayrı anahtarlarda durur.
const KEYS = {
  accessToken: 'haldus-shop.session.access',
  refreshToken: 'haldus-shop.session.refresh',
  account: 'haldus-shop.session.account',
} as const;

function parseAccount(
  text: string | null
): Pick<Session, 'accountId' | 'email' | 'tenantId' | 'emailVerified'> | null {
  try {
    const parsed: unknown = JSON.parse(text ?? 'null');

    if (
      typeof parsed === 'object' &&
      parsed !== null &&
      'accountId' in parsed &&
      'email' in parsed &&
      'tenantId' in parsed &&
      typeof parsed.accountId === 'string' &&
      typeof parsed.email === 'string' &&
      typeof parsed.tenantId === 'string'
    ) {
      return {
        accountId: parsed.accountId,
        email: parsed.email,
        tenantId: parsed.tenantId,
        emailVerified: 'emailVerified' in parsed && parsed.emailVerified === true,
      };
    }
  } catch {
    // Bozuk kayıt oturum yok sayılır.
  }

  return null;
}

let current: Session | null = null;
const listeners = new Set<() => void>();

function publish(next: Session | null) {
  current = next;
  listeners.forEach((listener) => listener());
}

export function getSession(): Session | null {
  return current;
}

/** Saklanan oturumu okur; uygulama açılırken bir kez çağrılır. */
export async function restoreSession(): Promise<void> {
  const [accessToken, refreshToken, account] = await Promise.all([
    readItem(KEYS.accessToken),
    readItem(KEYS.refreshToken),
    readItem(KEYS.account).then(parseAccount),
  ]);

  publish(
    accessToken && refreshToken && account ? { accessToken, refreshToken, ...account } : null
  );
}

export async function signIn(session: Session): Promise<void> {
  const { accessToken, refreshToken, ...account } = session;

  await Promise.all([
    writeItem(KEYS.accessToken, accessToken),
    writeItem(KEYS.refreshToken, refreshToken),
    writeItem(KEYS.account, JSON.stringify(account)),
  ]);
  publish(session);
}

/** Yenilenen token'ları yazar; hesap aynı kalır. */
export async function renewTokens(accessToken: string, refreshToken: string): Promise<void> {
  if (current) {
    // Önce bellekteki oturum güncellenir: bekleyen istekler yeni token'ı hemen görür.
    publish({ ...current, accessToken, refreshToken });
    await Promise.all([
      writeItem(KEYS.accessToken, accessToken),
      writeItem(KEYS.refreshToken, refreshToken),
    ]);
  }
}

/** E-posta doğrulanınca backend'in verdiği yeni token'larla oturumu günceller. */
export async function markEmailVerified(accessToken: string, refreshToken: string): Promise<void> {
  if (current) {
    await signIn({ ...current, accessToken, refreshToken, emailVerified: true });
  }
}

export async function signOut(): Promise<void> {
  publish(null);
  await Promise.all(Object.values(KEYS).map(removeItem));
}

export function subscribeSession(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

// Tarayıcıda (Expo'nun web hedefi) başka bir sekme oturumu değiştirdiyse bu sekme de onu izler; yoksa eskimiş yenileme
// token'ını sunup oturumun kapanmasına yol açardı. Cihazda tek bir uygulama vardır ve buna gerek yoktur.
if (Platform.OS === 'web' && typeof window !== 'undefined') {
  window.addEventListener('storage', (event) => {
    if (event.key !== null && Object.values<string>(KEYS).includes(event.key)) {
      void restoreSession();
    }
  });
}

export function useSession(): Session | null {
  return useSyncExternalStore(subscribeSession, getSession, getSession);
}
