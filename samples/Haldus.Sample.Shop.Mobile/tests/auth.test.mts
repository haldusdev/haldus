import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';

import { items } from './stubs/expo-secure-store.ts';
import { calls, callsTo, empty, json, respondWith } from './support/fetch.ts';
import type { Call } from './support/fetch.ts';
import { session, stored, unauthorized } from './support/fixtures.ts';

// Üretilen istemci taklit edilmez; istekleri `fetch` taklidine kadar gerçek yoldan gider.
import { changeOwnPassword, closeSession, confirmEmail, openSession } from '@/lib/auth';
import { getSession, signIn, signOut } from '@/lib/session';

const account = {
  id: 'account-1',
  email: 'ada@example.com',
  tenantId: 'tenant-1',
  emailVerified: true,
};
const renewedTokens = { accessToken: 'access-2', refreshToken: 'refresh-2' };

function bearer(call: Call): string | null {
  return call.headers.get('Authorization');
}

describe('auth', () => {
  beforeEach(async () => {
    await signOut();
    respondWith(() => {
      throw new Error('Unexpected request.');
    });
  });

  describe('openSession', () => {
    const tokens = {
      accessToken: 'access-1',
      refreshToken: 'refresh-1',
      tokenType: 'Bearer',
      expiresIn: 900,
    };

    it('asks who the account is with the new access token', async () => {
      respondWith(() => json(200, account));

      await openSession(tokens);

      assert.equal(calls.length, 1);
      assert.equal(calls[0].url, 'http://shop.test/auth/me');
      assert.equal(calls[0].method, 'GET');
      assert.equal(bearer(calls[0]), 'Bearer access-1');
    });

    it('stores the tokens together with the account', async () => {
      respondWith(() => json(200, account));

      await openSession(tokens);

      const expected = session({ emailVerified: true });
      assert.deepEqual(getSession(), expected);
      assert.deepEqual([...items].sort(), stored(expected).sort());
    });

    it('uses the new token even when an older session exists', async () => {
      await signIn(session({ accessToken: 'old-access', accountId: 'account-0' }));
      respondWith(() => json(200, account));

      await openSession(tokens);

      assert.equal(bearer(calls[0]), 'Bearer access-1');
      assert.equal(getSession()?.accountId, 'account-1');
    });

    it('opens no session when the account cannot be read', async () => {
      respondWith(() => json(401, unauthorized));

      await assert.rejects(openSession(tokens), (error) => {
        assert.deepEqual(error, unauthorized);
        return true;
      });

      assert.equal(getSession(), null);
      assert.equal(items.size, 0);
      assert.equal(calls.length, 1);
    });
  });

  describe('closeSession', () => {
    it('revokes the session at the backend and signs out', async () => {
      await signIn(session());
      respondWith(() => empty(204));

      await closeSession();

      assert.equal(calls.length, 1);
      assert.equal(calls[0].url, 'http://shop.test/auth/logout');
      assert.equal(calls[0].method, 'POST');
      assert.equal(bearer(calls[0]), 'Bearer access-1');
      // Yenileme token'ı da gider: erişim token'ı geçmiyorsa oturumu sunucuda o tanıtır.
      assert.deepEqual(calls[0].body, { refreshToken: 'refresh-1' });
      assert.equal(getSession(), null);
      assert.equal(items.size, 0);
    });

    it('signs out even when the backend fails', async () => {
      await signIn(session());
      respondWith(() => json(500, { title: 'Server error', status: 500 }));

      await closeSession();

      assert.equal(getSession(), null);
      assert.equal(items.size, 0);
    });

    it('signs out even when the backend cannot be reached', async () => {
      await signIn(session());
      respondWith(() => {
        throw new TypeError('Network request failed');
      });

      await closeSession();

      assert.equal(getSession(), null);
      assert.equal(items.size, 0);
    });

    it('signs out even when the session was already revoked', async () => {
      await signIn(session());
      respondWith(() => json(401, unauthorized));

      await closeSession();

      assert.equal(getSession(), null);
      assert.equal(items.size, 0);
    });

    it('renews an expired access token so that the logout reaches the backend', async () => {
      await signIn(session());
      respondWith((call) => {
        if (call.url.endsWith('/auth/refresh')) {
          return json(200, renewedTokens);
        }

        return bearer(call) === 'Bearer access-2' ? empty(204) : json(401, unauthorized);
      });

      await closeSession();

      assert.deepEqual(
        calls.map((call) => [call.url, bearer(call)]),
        [
          ['http://shop.test/auth/logout', 'Bearer access-1'],
          ['http://shop.test/auth/refresh', null],
          ['http://shop.test/auth/logout', 'Bearer access-2'],
        ]
      );
      assert.equal(getSession(), null);
      assert.equal(items.size, 0);
    });

    it('sends nothing when nobody is signed in', async () => {
      await closeSession();

      assert.equal(calls.length, 0);
      assert.equal(getSession(), null);
    });
  });

  describe('changeOwnPassword', () => {
    it('stores the tokens of the new session the backend opens', async () => {
      await signIn(session({ emailVerified: true }));
      respondWith(() => json(200, renewedTokens));

      await changeOwnPassword('old password', 'new password');

      assert.equal(calls[0].url, 'http://shop.test/auth/password/change');
      assert.deepEqual(calls[0].body, {
        currentPassword: 'old password',
        newPassword: 'new password',
      });
      const expected = session({ ...renewedTokens, emailVerified: true });
      assert.deepEqual(getSession(), expected);
      assert.deepEqual([...items].sort(), stored(expected).sort());
    });

    it("does not write the new tokens into another account's session", async () => {
      const other = session({
        accountId: 'account-2',
        email: 'grace@example.com',
        accessToken: 'access-b',
        refreshToken: 'refresh-b',
      });
      await signIn(session());
      respondWith(async () => {
        await signIn(other);
        return json(200, renewedTokens);
      });

      await changeOwnPassword('old password', 'new password');

      assert.deepEqual(getSession(), other);
    });
  });

  describe('confirmEmail', () => {
    it('sends the code and stores the verified tokens', async () => {
      await signIn(session());
      respondWith(() => json(200, renewedTokens));

      await confirmEmail('123456');

      assert.equal(calls.length, 1);
      assert.equal(calls[0].url, 'http://shop.test/auth/email/verify');
      assert.equal(calls[0].method, 'POST');
      assert.deepEqual(calls[0].body, { code: '123456' });
      assert.equal(bearer(calls[0]), 'Bearer access-1');

      const expected = session({ ...renewedTokens, emailVerified: true });
      assert.deepEqual(getSession(), expected);
      assert.deepEqual([...items].sort(), stored(expected).sort());
    });

    it("does not write the verified tokens into another account's session", async () => {
      // Yanıt beklenirken başka bir hesapla giriş yapıldı.
      const other = session({
        accountId: 'account-2',
        email: 'grace@example.com',
        accessToken: 'access-b',
        refreshToken: 'refresh-b',
      });
      await signIn(session());
      respondWith(async () => {
        await signIn(other);
        return json(200, renewedTokens);
      });

      await confirmEmail('123456');

      assert.deepEqual(getSession(), other);
      assert.deepEqual([...items].sort(), stored(other).sort());
      assert.equal(callsTo('/auth/refresh').length, 0);
    });

    it('renews instead of storing tokens that went stale while verifying', async () => {
      const renewedElsewhere = session({ accessToken: 'access-9', refreshToken: 'refresh-9' });
      await signIn(session());
      respondWith(async (call) => {
        if (call.url.endsWith('/auth/refresh')) {
          return json(200, renewedTokens);
        }

        await signIn(renewedElsewhere);
        return json(200, { accessToken: 'stale-a', refreshToken: 'stale-r' });
      });

      await confirmEmail('123456');

      assert.deepEqual(callsTo('/auth/refresh')[0].body, { refreshToken: 'refresh-9' });
      assert.deepEqual(getSession(), session({ ...renewedTokens, emailVerified: true }));
    });

    it('writes nothing when the user signed out while verifying', async () => {
      await signIn(session());
      respondWith(async () => {
        await signOut();
        return json(200, renewedTokens);
      });

      await confirmEmail('123456');

      assert.equal(getSession(), null);
      assert.equal(items.size, 0);
    });

    it('renews the session when the account was already verified', async () => {
      await signIn(session());
      respondWith((call) =>
        call.url.endsWith('/auth/refresh') ? json(200, renewedTokens) : empty(204)
      );

      await confirmEmail('123456');

      assert.deepEqual(
        calls.map((call) => call.url),
        ['http://shop.test/auth/email/verify', 'http://shop.test/auth/refresh']
      );
      assert.deepEqual(calls[1].body, { refreshToken: 'refresh-1' });

      const expected = session({ ...renewedTokens, emailVerified: true });
      assert.deepEqual(getSession(), expected);
      assert.deepEqual([...items].sort(), stored(expected).sort());
    });

    it('leaves the flag alone when the renewal cannot reach the backend', async () => {
      await signIn(session());
      respondWith((call) => {
        if (call.url.endsWith('/auth/refresh')) {
          throw new TypeError('Network request failed');
        }

        return empty(204);
      });

      await confirmEmail('123456');

      assert.deepEqual(getSession(), session());
    });

    it('ends signed out when the renewal is rejected', async () => {
      await signIn(session());
      respondWith((call) =>
        call.url.endsWith('/auth/refresh') ? json(401, unauthorized) : empty(204)
      );

      await confirmEmail('123456');

      assert.equal(getSession(), null);
      assert.equal(items.size, 0);
    });

    it('throws the problem of a wrong code and keeps the session', async () => {
      const problem = {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { code: ['The code is not valid.'] },
      };
      await signIn(session());
      respondWith(() => json(400, problem));

      await assert.rejects(confirmEmail('000000'), (error) => {
        assert.deepEqual(error, problem);
        return true;
      });

      assert.deepEqual(getSession(), session());
      assert.equal(callsTo('/auth/refresh').length, 0);
    });
  });
});
