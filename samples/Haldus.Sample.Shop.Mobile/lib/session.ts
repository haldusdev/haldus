import { useSyncExternalStore } from 'react';

import { Platform } from 'react-native';

import { readItem, removeItem, writeItem } from '@/lib/storage';

type Account = {
  accountId: string;
  email: string;
  tenantId: string;
  emailVerified: boolean;
};

/** Sağlayıcı oturumunu yenilemek için gerekenler; keşif girişte bir kez yapılır ve sonucu oturumla saklanır. */
export type ProviderLink = {
  clientId: string;
  tokenEndpoint: string;
};

/**
 * E-posta ve parolayla açılan oturum: token'ları backend verir. Erişim token'ı her istekte `Authorization` başlığında
 * gider; süresi dolunca yenileme token'ı ile backend'den yenisi alınır (api-fetch.ts).
 */
export type PasswordSession = Account & {
  kind: 'password';
  accessToken: string;
  refreshToken: string;
};

/**
 * Kimlik sağlayıcıyla açılan oturum: token'lar sağlayıcınındır, backend onları yalnızca doğrular. Yenileme
 * sağlayıcının token ucundan yapılır; sağlayıcı yenileme token'ı vermediyse (`null`) oturum yenilenemez.
 */
export type ProviderSession = Account & {
  kind: 'provider';
  accessToken: string;
  refreshToken: string | null;
  provider: ProviderLink;
};

/** Oturum: token'lar ve hesabın kimliği. Cihazda işletim sisteminin güvenli deposunda saklanır (storage.ts). */
export type Session = PasswordSession | ProviderSession;

type StoredAccount =
  (Account & { kind: 'password' }) | (Account & Pick<ProviderSession, 'kind' | 'provider'>);

// Güvenli depo büyük değerleri her platformda kabul etmez (yaklaşık 2 KB); token'lar bu yüzden ayrı anahtarlarda durur.
const KEYS = {
  accessToken: 'haldus-shop.session.access',
  refreshToken: 'haldus-shop.session.refresh',
  account: 'haldus-shop.session.account',
} as const;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function filled(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0;
}

function parseAccount(text: string | null): StoredAccount | null {
  try {
    const parsed: unknown = JSON.parse(text ?? 'null');

    if (
      !isRecord(parsed) ||
      !filled(parsed.accountId) ||
      !filled(parsed.email) ||
      !filled(parsed.tenantId)
    ) {
      return null;
    }

    const account: Account = {
      accountId: parsed.accountId,
      email: parsed.email,
      tenantId: parsed.tenantId,
      emailVerified: parsed.emailVerified === true,
    };

    // Tür bilgisi sağlayıcı girişiyle birlikte eklendi; ondan önce saklanmış kayıtlarda yoktur ve hepsi parola
    // oturumudur.
    if (parsed.kind === undefined || parsed.kind === 'password') {
      return { ...account, kind: 'password' };
    }

    if (
      parsed.kind === 'provider' &&
      isRecord(parsed.provider) &&
      filled(parsed.provider.clientId) &&
      filled(parsed.provider.tokenEndpoint)
    ) {
      return {
        ...account,
        kind: 'provider',
        provider: {
          clientId: parsed.provider.clientId,
          tokenEndpoint: parsed.provider.tokenEndpoint,
        },
      };
    }
  } catch {
    // Bozuk kayıt oturum yok sayılır.
  }

  // Tanınmayan bir tür de oturum yok sayılır: token'ları yanlış yere (ör. backend'in yenileme ucuna) gönderilmez.
  return null;
}

function assemble(
  accessToken: string | null,
  refreshToken: string | null,
  account: StoredAccount | null
): Session | null {
  if (!accessToken || !account) {
    return null;
  }

  if (account.kind === 'provider') {
    return { ...account, accessToken, refreshToken: refreshToken || null };
  }

  return refreshToken ? { ...account, accessToken, refreshToken } : null;
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

  publish(assemble(accessToken, refreshToken, account));
}

function serializeAccount(session: Session): string {
  const account: Account = {
    accountId: session.accountId,
    email: session.email,
    tenantId: session.tenantId,
    emailVerified: session.emailVerified,
  };

  // Parola oturumunun kaydı tür taşımaz: biçimi, tür bilgisinden önceki sürümün yazdığıyla aynı kalır.
  return JSON.stringify(
    session.kind === 'provider'
      ? { ...account, kind: session.kind, provider: session.provider }
      : account
  );
}

export async function signIn(session: Session): Promise<void> {
  await Promise.all([
    writeItem(KEYS.accessToken, session.accessToken),
    // Yenileme token'ı olmayan oturum, önceki oturumdan kalanı da siler; yoksa yeniden açılışta ona ait sanılırdı.
    session.refreshToken === null
      ? removeItem(KEYS.refreshToken)
      : writeItem(KEYS.refreshToken, session.refreshToken),
    writeItem(KEYS.account, serializeAccount(session)),
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
