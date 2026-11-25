import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';

import { items } from './stubs/expo-secure-store.ts';
import { KEYS, providerSession, session, stored } from './support/fixtures.ts';

import {
  getSession,
  markEmailVerified,
  renewTokens,
  restoreSession,
  signIn,
  signOut,
  subscribeSession,
} from '@/lib/session';

/** Dinleyicinin kaç kez çağrıldığını sayar. */
function watch() {
  const seen = { count: 0, unsubscribe: () => {} };

  seen.unsubscribe = subscribeSession(() => {
    seen.count++;
  });
  return seen;
}

function store(entries: [string, string][]) {
  items.clear();
  entries.forEach(([key, value]) => items.set(key, value));
}

// Platform `ios`'tur (stubs/react-native.ts): depo cihazın güvenli deposudur.
describe('session on a device', () => {
  beforeEach(async () => {
    await signOut();
    items.clear();
  });

  it('starts signed out until the stored session is restored', async () => {
    store(stored(session()));

    assert.equal(getSession(), null);

    await restoreSession();

    assert.deepEqual(getSession(), session());
  });

  it('signIn keeps the tokens under separate keys of the secure store', async () => {
    const seen = watch();

    await signIn(session({ emailVerified: true }));

    assert.deepEqual(getSession(), session({ emailVerified: true }));
    assert.deepEqual([...items.keys()].sort(), Object.values(KEYS).sort());
    assert.equal(items.get(KEYS.accessToken), 'access-1');
    assert.equal(items.get(KEYS.refreshToken), 'refresh-1');
    assert.deepEqual(JSON.parse(items.get(KEYS.account) ?? 'null'), {
      accountId: 'account-1',
      email: 'ada@example.com',
      tenantId: 'tenant-1',
      emailVerified: true,
    });
    assert.equal(seen.count, 1);
    seen.unsubscribe();
  });

  it('keeps the tokens out of the account entry', async () => {
    await signIn(session());

    const account = items.get(KEYS.account) ?? '';
    assert.equal(account.includes('access-1'), false);
    assert.equal(account.includes('refresh-1'), false);
  });

  it('restores what signIn stored', async () => {
    const signedIn = session({ emailVerified: true });
    await signIn(signedIn);
    const kept = [...items];
    await signOut();
    store(kept);

    await restoreSession();

    assert.deepEqual(getSession(), signedIn);
  });

  it('stops telling a subscriber that unsubscribed', async () => {
    const seen = watch();
    seen.unsubscribe();

    await signIn(session());

    assert.equal(seen.count, 0);
  });

  it('signOut removes every key and tells subscribers', async () => {
    await signIn(session());
    const seen = watch();

    await signOut();

    assert.equal(getSession(), null);
    assert.equal(items.size, 0);
    assert.equal(seen.count, 1);
    seen.unsubscribe();
  });

  it('signOut forgets the session before the store is cleared', async () => {
    await signIn(session());

    const pending = signOut();

    assert.equal(getSession(), null);
    await pending;
  });

  it('renewTokens replaces the tokens and keeps the account', async () => {
    await signIn(session({ emailVerified: true }));
    const account = items.get(KEYS.account);
    const seen = watch();

    await renewTokens('access-2', 'refresh-2');

    assert.deepEqual(
      getSession(),
      session({ accessToken: 'access-2', refreshToken: 'refresh-2', emailVerified: true })
    );
    assert.equal(items.get(KEYS.accessToken), 'access-2');
    assert.equal(items.get(KEYS.refreshToken), 'refresh-2');
    assert.equal(items.get(KEYS.account), account);
    assert.equal(seen.count, 1);
    seen.unsubscribe();
  });

  it('renewTokens shows the new tokens before they are stored', async () => {
    await signIn(session());

    const pending = renewTokens('access-2', 'refresh-2');

    assert.equal(getSession()?.accessToken, 'access-2');
    assert.equal(getSession()?.refreshToken, 'refresh-2');
    await pending;
  });

  it('renewTokens does nothing when nobody is signed in', async () => {
    const seen = watch();

    await renewTokens('access-2', 'refresh-2');

    assert.equal(getSession(), null);
    assert.equal(items.size, 0);
    assert.equal(seen.count, 0);
    seen.unsubscribe();
  });

  it('markEmailVerified stores the new tokens and the verified flag', async () => {
    await signIn(session());

    await markEmailVerified('access-2', 'refresh-2');

    const expected = session({
      accessToken: 'access-2',
      refreshToken: 'refresh-2',
      emailVerified: true,
    });
    assert.deepEqual(getSession(), expected);
    assert.deepEqual([...items].sort(), stored(expected).sort());
  });

  it('markEmailVerified does nothing when nobody is signed in', async () => {
    await markEmailVerified('access-2', 'refresh-2');

    assert.equal(getSession(), null);
    assert.equal(items.size, 0);
  });

  describe('restoreSession', () => {
    for (const [name, key] of Object.entries(KEYS)) {
      it(`finds no session when the ${name} entry is missing`, async () => {
        await signIn(session());
        store(stored(session()).filter(([entry]) => entry !== key));

        await restoreSession();

        assert.equal(getSession(), null);
      });
    }

    it('finds no session when a token is empty', async () => {
      store(stored(session({ refreshToken: '' })));

      await restoreSession();

      assert.equal(getSession(), null);
    });

    const malformed: [string, string][] = [
      ['text that is not JSON', '{not json'],
      ['a JSON null', 'null'],
      ['a JSON string', '"account-1"'],
      ['missing its tenant', JSON.stringify({ accountId: 'account-1', email: 'ada@example.com' })],
      [
        'carrying an id that is not text',
        JSON.stringify({ accountId: 7, email: 'ada@example.com', tenantId: 'tenant-1' }),
      ],
      [
        'carrying an email that is not text',
        JSON.stringify({ accountId: 'account-1', email: null, tenantId: 'tenant-1' }),
      ],
      [
        'carrying a tenant that is not text',
        JSON.stringify({ accountId: 'account-1', email: 'ada@example.com', tenantId: null }),
      ],
    ];

    for (const [name, account] of malformed) {
      it(`finds no session when the account entry is ${name}`, async () => {
        await signIn(session());
        store([
          [KEYS.accessToken, 'access-1'],
          [KEYS.refreshToken, 'refresh-1'],
          [KEYS.account, account],
        ]);

        await restoreSession();

        assert.equal(getSession(), null);
      });
    }

    it('reads the verified flag only when it is exactly true', async () => {
      store([
        [KEYS.accessToken, 'access-1'],
        [KEYS.refreshToken, 'refresh-1'],
        [
          KEYS.account,
          JSON.stringify({
            accountId: 'account-1',
            email: 'ada@example.com',
            tenantId: 'tenant-1',
            emailVerified: 'true',
          }),
        ],
      ]);

      await restoreSession();

      assert.deepEqual(getSession(), session({ emailVerified: false }));
    });

    it('tells subscribers', async () => {
      store(stored(session()));
      const seen = watch();

      await restoreSession();

      assert.equal(seen.count, 1);
      seen.unsubscribe();
    });
  });
});

// Oturumun türü hesap kaydında durur. Parola oturumunun kaydı tür taşımaz; böylece tür bilgisinden önce yazılmış
// kayıtlar da, bu sürümün yazdıkları da aynı biçimdedir.
describe('session kinds', () => {
  const account = { accountId: 'account-1', email: 'ada@example.com', tenantId: 'tenant-1' };
  const link = { clientId: 'shop-mobile', tokenEndpoint: 'https://id.test/connect/token' };

  beforeEach(async () => {
    await signOut();
    items.clear();
  });

  it('reads a session stored before the kind existed as a password session', async () => {
    store([
      [KEYS.accessToken, 'access-1'],
      [KEYS.refreshToken, 'refresh-1'],
      [KEYS.account, JSON.stringify({ ...account, emailVerified: true })],
    ]);

    await restoreSession();

    assert.deepEqual(getSession(), session({ emailVerified: true }));
    assert.equal(getSession()?.kind, 'password');
  });

  it('writes a password session in the format used before the kind existed', async () => {
    await signIn(session());

    assert.deepEqual(Object.keys(JSON.parse(items.get(KEYS.account) ?? '{}')), [
      'accountId',
      'email',
      'tenantId',
      'emailVerified',
    ]);
  });

  it('reads an explicit password kind', async () => {
    store([
      [KEYS.accessToken, 'access-1'],
      [KEYS.refreshToken, 'refresh-1'],
      [KEYS.account, JSON.stringify({ ...account, kind: 'password' })],
    ]);

    await restoreSession();

    assert.deepEqual(getSession(), session());
  });

  it('stores a provider session with its kind and what its renewal needs', async () => {
    await signIn(providerSession());

    assert.deepEqual([...items.keys()].sort(), Object.values(KEYS).sort());
    assert.equal(items.get(KEYS.accessToken), 'idp-access-1');
    assert.equal(items.get(KEYS.refreshToken), 'idp-refresh-1');
    assert.deepEqual(JSON.parse(items.get(KEYS.account) ?? 'null'), {
      ...account,
      emailVerified: true,
      kind: 'provider',
      provider: link,
    });
  });

  it('restores the provider session it stored', async () => {
    await signIn(providerSession());
    const kept = [...items];
    await signOut();
    store(kept);

    await restoreSession();

    assert.deepEqual(getSession(), providerSession());
  });

  it('stores a provider session without a refresh token under two keys', async () => {
    const signedIn = providerSession({ refreshToken: null });

    await signIn(signedIn);

    assert.deepEqual([...items].sort(), stored(signedIn).sort());
    assert.equal(items.has(KEYS.refreshToken), false);
  });

  it('restores a provider session without a refresh token', async () => {
    store(stored(providerSession({ refreshToken: null })));

    await restoreSession();

    assert.deepEqual(getSession(), providerSession({ refreshToken: null }));
  });

  it('removes the refresh token of the previous session when the new one has none', async () => {
    await signIn(session());

    await signIn(providerSession({ refreshToken: null }));
    await restoreSession();

    assert.equal(items.has(KEYS.refreshToken), false);
    assert.equal(getSession()?.refreshToken, null);
  });

  it('replaces a provider session with a password session completely', async () => {
    await signIn(providerSession());

    await signIn(session());
    await restoreSession();

    assert.deepEqual(getSession(), session());
    assert.deepEqual([...items].sort(), stored(session()).sort());
  });

  it('renewTokens keeps the kind and the provider of the session', async () => {
    await signIn(providerSession());
    const entry = items.get(KEYS.account);

    await renewTokens('idp-access-2', 'idp-refresh-2');

    assert.deepEqual(
      getSession(),
      providerSession({ accessToken: 'idp-access-2', refreshToken: 'idp-refresh-2' })
    );
    assert.equal(items.get(KEYS.account), entry);
    assert.equal(items.get(KEYS.accessToken), 'idp-access-2');
    assert.equal(items.get(KEYS.refreshToken), 'idp-refresh-2');
  });

  it('signOut removes every key of a provider session', async () => {
    await signIn(providerSession());

    await signOut();

    assert.equal(getSession(), null);
    assert.equal(items.size, 0);
  });

  it('finds no password session without a refresh token', async () => {
    // Yenileme token'ının yokluğu yalnızca sağlayıcı oturumunda geçerlidir.
    store(stored(session()).filter(([key]) => key !== KEYS.refreshToken));

    await restoreSession();

    assert.equal(getSession(), null);
  });

  const unusable: [string, unknown][] = [
    ['a kind this version does not know', { ...account, kind: 'passkey' }],
    ['a kind that is not text', { ...account, kind: 1 }],
    ['a provider kind without its provider', { ...account, kind: 'provider' }],
    [
      'a provider without a token endpoint',
      { ...account, kind: 'provider', provider: { clientId: 'shop-mobile' } },
    ],
    [
      'a provider without a client',
      { ...account, kind: 'provider', provider: { ...link, clientId: '' } },
    ],
    ['a provider that is not an object', { ...account, kind: 'provider', provider: 'id.test' }],
  ];

  for (const [name, entry] of unusable) {
    it(`finds no session when the account entry carries ${name}`, async () => {
      // Tanınmayan bir kayıt parola oturumu sayılmaz; yoksa token'ları backend'in yenileme ucuna gönderilirdi.
      store([
        [KEYS.accessToken, 'access-1'],
        [KEYS.refreshToken, 'refresh-1'],
        [KEYS.account, JSON.stringify(entry)],
      ]);

      await restoreSession();

      assert.equal(getSession(), null);
    });
  }

  it('ignores provider details on a password session', async () => {
    store([
      [KEYS.accessToken, 'access-1'],
      [KEYS.refreshToken, 'refresh-1'],
      [KEYS.account, JSON.stringify({ ...account, kind: 'password', provider: link })],
    ]);

    await restoreSession();

    assert.deepEqual(getSession(), session());
  });
});
