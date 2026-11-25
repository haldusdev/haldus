import type { PasswordSession, ProviderSession, Session } from '@/lib/session';

export const KEYS = {
  accessToken: 'haldus-shop.session.access',
  refreshToken: 'haldus-shop.session.refresh',
  account: 'haldus-shop.session.account',
};

/** E-posta ve parolayla açılmış bir oturum. */
export function session(overrides: Partial<PasswordSession> = {}): PasswordSession {
  return {
    kind: 'password',
    accessToken: 'access-1',
    refreshToken: 'refresh-1',
    accountId: 'account-1',
    email: 'ada@example.com',
    tenantId: 'tenant-1',
    emailVerified: false,
    ...overrides,
  };
}

/** Kimlik sağlayıcıyla açılmış bir oturum; token'ları sağlayıcınındır. */
export function providerSession(overrides: Partial<ProviderSession> = {}): ProviderSession {
  return {
    kind: 'provider',
    accessToken: 'idp-access-1',
    refreshToken: 'idp-refresh-1',
    provider: { clientId: 'shop-mobile', tokenEndpoint: 'https://id.test/connect/token' },
    accountId: 'account-1',
    email: 'ada@example.com',
    tenantId: 'tenant-1',
    emailVerified: true,
    ...overrides,
  };
}

/**
 * Bir oturumun depoda durduğu biçim: token'lar ayrı anahtarlardadır. Parola oturumunun hesap kaydı tür taşımaz
 * (tür bilgisinden önce yazılmış kayıtlarla aynıdır); yenileme token'ı olmayan oturumun o anahtarı yoktur.
 */
export function stored(value: Session): [string, string][] {
  const account = {
    accountId: value.accountId,
    email: value.email,
    tenantId: value.tenantId,
    emailVerified: value.emailVerified,
  };

  const entries: [string, string][] = [
    [KEYS.accessToken, value.accessToken],
    [
      KEYS.account,
      JSON.stringify(
        value.kind === 'provider'
          ? { ...account, kind: 'provider', provider: value.provider }
          : account
      ),
    ],
  ];

  if (value.refreshToken !== null) {
    entries.push([KEYS.refreshToken, value.refreshToken]);
  }

  return entries;
}

export const unauthorized = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'Unauthorized',
  status: 401,
};
