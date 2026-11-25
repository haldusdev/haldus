import type { Session } from '@/lib/session';

export const KEYS = {
  accessToken: 'haldus-shop.session.access',
  refreshToken: 'haldus-shop.session.refresh',
  account: 'haldus-shop.session.account',
};

export function session(overrides: Partial<Session> = {}): Session {
  return {
    accessToken: 'access-1',
    refreshToken: 'refresh-1',
    accountId: 'account-1',
    email: 'ada@example.com',
    tenantId: 'tenant-1',
    emailVerified: false,
    ...overrides,
  };
}

/** Bir oturumun depoda üç ayrı anahtarda durduğu biçim. */
export function stored(value: Session): [string, string][] {
  return [
    [KEYS.accessToken, value.accessToken],
    [KEYS.refreshToken, value.refreshToken],
    [
      KEYS.account,
      JSON.stringify({
        accountId: value.accountId,
        email: value.email,
        tenantId: value.tenantId,
        emailVerified: value.emailVerified,
      }),
    ],
  ];
}

export const unauthorized = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'Unauthorized',
  status: 401,
};
