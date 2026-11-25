import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';

import { items } from './stubs/expo-secure-store.ts';
import { calls, callsTo, deferred, json, respondWith, settle, text } from './support/fetch.ts';
import type { Call } from './support/fetch.ts';
import { KEYS, providerSession, session, stored, unauthorized } from './support/fixtures.ts';

// Parola oturumunun yenileme işlevini de kaydeder: sağlayıcı oturumunun ona hiç başvurmadığı görülsün diye.
import '@/lib/auth';
import { apiFetch, renewSession } from '@/lib/api-fetch';
import { getSession, signIn, signOut, subscribeSession } from '@/lib/session';

type Result = { data: unknown; status: number };

const TOKEN_ENDPOINT = 'https://id.test/connect/token';
const renewed = { accessToken: 'idp-access-2', refreshToken: 'idp-refresh-2' };
const granted = { access_token: 'idp-access-2', refresh_token: 'idp-refresh-2' };

function bearer(call: Call): string | null {
  return call.headers.get('Authorization');
}

function toProvider(call: Call): boolean {
  return call.url === TOKEN_ENDPOINT;
}

/** Backend sağlayıcının eski erişim token'ını 401 ile reddeder, yenisini kabul eder. */
function shop(call: Call): Response {
  return bearer(call) === 'Bearer idp-access-2'
    ? json(200, { path: call.url })
    : json(401, unauthorized);
}

/** Sağlayıcının token ucu yeni token'ları verir; öteki istekler backend'e gider. */
function world(call: Call): Response {
  return toProvider(call) ? json(200, granted) : shop(call);
}

// Sağlayıcıyla açılan oturumun token'ları sağlayıcınındır: yenileme backend'in `/auth/refresh` ucundan değil,
// sağlayıcının token ucundan yapılır. Parola oturumunun güvenceleri (tek yenileme, başka hesabın oturumuna yazmama,
// yalnızca kesin rette çıkış) burada da geçerlidir.
describe('apiFetch renewal of a provider session', () => {
  beforeEach(async () => {
    await signIn(providerSession());
    respondWith(world);
  });

  it('sends the access token of the provider to the backend', async () => {
    respondWith(() => json(200, {}));

    await apiFetch('/me/baskets', {});

    assert.equal(bearer(calls[0]), 'Bearer idp-access-1');
  });

  it('renews at the token endpoint of the provider and repeats the request', async () => {
    const result = await apiFetch<Result>('/me/baskets', {});

    assert.deepEqual(
      calls.map((call) => [call.method, call.url, bearer(call)]),
      [
        ['GET', 'http://shop.test/me/baskets', 'Bearer idp-access-1'],
        ['POST', TOKEN_ENDPOINT, null],
        ['GET', 'http://shop.test/me/baskets', 'Bearer idp-access-2'],
      ]
    );
    assert.deepEqual(calls[1].body, {
      grant_type: 'refresh_token',
      client_id: 'shop-mobile',
      refresh_token: 'idp-refresh-1',
    });
    assert.deepEqual(result.data, { path: 'http://shop.test/me/baskets' });
  });

  it('never asks the backend to refresh a provider session', async () => {
    await apiFetch('/me/baskets', {});

    assert.equal(callsTo('/auth/refresh').length, 0);
  });

  it('sends the provider neither the language nor a token of the app', async () => {
    await apiFetch('/me/baskets', {});

    assert.equal(calls[1].headers.get('Authorization'), null);
    assert.equal(calls[1].headers.get('Accept-Language'), null);
  });

  it('stores the renewed tokens, keeps the kind and tells subscribers', async () => {
    let published = 0;
    const unsubscribe = subscribeSession(() => {
      published++;
    });

    await apiFetch('/me/baskets', {});
    unsubscribe();

    assert.deepEqual(getSession(), providerSession(renewed));
    assert.deepEqual([...items].sort(), stored(providerSession(renewed)).sort());
    assert.equal(published, 1);
  });

  it('keeps the refresh token when the provider does not rotate it', async () => {
    respondWith((call) =>
      toProvider(call) ? json(200, { access_token: 'idp-access-2' }) : shop(call)
    );

    await apiFetch('/me/baskets', {});

    const expected = providerSession({ accessToken: 'idp-access-2' });
    assert.deepEqual(getSession(), expected);
    assert.equal(items.get(KEYS.refreshToken), 'idp-refresh-1');
  });

  it('renews again with the same refresh token when the provider does not rotate it', async () => {
    respondWith((call) =>
      toProvider(call) ? json(200, { access_token: 'idp-access-2' }) : shop(call)
    );
    await apiFetch('/me/baskets', {});

    respondWith((call) => {
      if (toProvider(call)) {
        return json(200, { access_token: 'idp-access-3' });
      }

      return bearer(call) === 'Bearer idp-access-3' ? json(200, {}) : json(401, unauthorized);
    });
    await apiFetch('/me/baskets', {});

    assert.equal(calls.filter(toProvider).length, 1);
    assert.equal(getSession()?.accessToken, 'idp-access-3');
    assert.equal(getSession()?.refreshToken, 'idp-refresh-1');
  });

  it('shares one renewal between requests rejected at the same time', async () => {
    const token = deferred<Response>();
    respondWith((call) => (toProvider(call) ? token.promise : shop(call)));

    const pending = Promise.all([
      apiFetch<Result>('/me/baskets', {}),
      apiFetch<Result>('/me/favorites', {}),
      apiFetch<Result>('/products', {}),
    ]);
    await settle();

    assert.equal(calls.filter(toProvider).length, 1);

    token.resolve(json(200, granted));
    const results = await pending;

    assert.equal(calls.filter(toProvider).length, 1);
    assert.deepEqual(
      results.map((result) => result.data),
      [
        { path: 'http://shop.test/me/baskets' },
        { path: 'http://shop.test/me/favorites' },
        { path: 'http://shop.test/products' },
      ]
    );
    assert.deepEqual(calls.slice(-3).map(bearer), Array(3).fill('Bearer idp-access-2'));
  });

  it('does not renew again for a 401 that arrives after the renewal', async () => {
    const late = deferred<Response>();
    respondWith((call) =>
      call.url.endsWith('/slow') && bearer(call) === 'Bearer idp-access-1'
        ? late.promise
        : world(call)
    );

    const slow = apiFetch<Result>('/slow', {});
    await apiFetch('/me/baskets', {});
    late.resolve(json(401, unauthorized));
    const result = await slow;

    assert.equal(calls.filter(toProvider).length, 1);
    assert.deepEqual(result.data, { path: 'http://shop.test/slow' });
  });

  it('does not renew a second time when the backend rejects the new token too', async () => {
    respondWith((call) => (toProvider(call) ? json(200, granted) : json(401, unauthorized)));

    await assert.rejects(apiFetch('/me/baskets', {}), (error) => {
      assert.deepEqual(error, unauthorized);
      return true;
    });

    assert.equal(calls.filter(toProvider).length, 1);
    assert.equal(callsTo('/me/baskets').length, 2);
    // Yenileme başarılıydı; backend'in reddi oturumu kapatmaz (parola oturumundaki kuralın aynısı).
    assert.deepEqual(getSession(), providerSession(renewed));
  });

  describe('a definite rejection', () => {
    for (const code of ['invalid_grant', 'invalid_client', 'unauthorized_client']) {
      it(`signs out when the provider answers ${code}`, async () => {
        respondWith((call) =>
          toProvider(call) ? json(400, { error: code }) : json(401, unauthorized)
        );

        await assert.rejects(apiFetch('/me/baskets', {}), (error) => {
          // İsteğin kendi hatası döner, yenilemeninki değil.
          assert.deepEqual(error, unauthorized);
          return true;
        });

        assert.equal(getSession(), null);
        assert.equal(items.size, 0);
        assert.equal(calls.filter(toProvider).length, 1);
        assert.equal(callsTo('/me/baskets').length, 1);
      });
    }

    it('does not tell the backend, which holds no session for a provider sign-in', async () => {
      respondWith((call) =>
        toProvider(call) ? json(400, { error: 'invalid_grant' }) : json(401, unauthorized)
      );

      await assert.rejects(apiFetch('/me/baskets', {}));

      assert.equal(callsTo('/auth/logout').length, 0);
      assert.equal(callsTo('/auth/refresh').length, 0);
    });

    it('does not sign out the account that signed in while the provider was answering', async () => {
      const token = deferred<Response>();
      respondWith((call) => (toProvider(call) ? token.promise : json(401, unauthorized)));

      const request = apiFetch('/me/baskets', {});
      await settle();

      const other = providerSession({
        accountId: 'account-2',
        email: 'grace@example.com',
        accessToken: 'idp-access-b',
        refreshToken: 'idp-refresh-b',
      });
      await signIn(other);
      token.resolve(json(400, { error: 'invalid_grant' }));

      await assert.rejects(request);
      assert.deepEqual(getSession(), other);
      assert.deepEqual([...items].sort(), stored(other).sort());
    });
  });

  describe('a failure that is not a rejection', () => {
    for (const [name, failure] of [
      ['a server error code', () => json(500, { error: 'server_error' })],
      ['an overloaded provider', () => json(503, { error: 'temporarily_unavailable' })],
      ['a server error without a code', () => json(500, { message: 'Internal Server Error' })],
      ['a proxy error page', () => text(502, '<html>Bad Gateway</html>', 'Bad Gateway')],
      ['a rate limit', () => json(429, { error: 'slow_down' })],
      ['a response without an access token', () => json(200, { token_type: 'Bearer' })],
    ] as const) {
      it(`keeps the session when the provider answers with ${name}`, async () => {
        respondWith((call) => (toProvider(call) ? failure() : json(401, unauthorized)));

        // İstek kendi 401'iyle başarısız olur; geçici bir arıza kullanıcıyı oturumdan atmaz.
        await assert.rejects(apiFetch('/me/baskets', {}), (error) => {
          assert.deepEqual(error, unauthorized);
          return true;
        });

        assert.deepEqual(getSession(), providerSession());
        assert.deepEqual([...items].sort(), stored(providerSession()).sort());
        assert.equal(callsTo('/me/baskets').length, 1);
      });
    }

    it('keeps the session when the provider cannot be reached', async () => {
      respondWith((call) => {
        if (toProvider(call)) {
          throw new TypeError('Network request failed');
        }

        return json(401, unauthorized);
      });

      await assert.rejects(apiFetch('/me/baskets', {}));

      assert.deepEqual(getSession(), providerSession());
    });

    it('can renew after the provider recovers', async () => {
      respondWith((call) =>
        toProvider(call) ? json(503, { error: 'temporarily_unavailable' }) : json(401, unauthorized)
      );
      await assert.rejects(apiFetch('/me/baskets', {}));

      respondWith(world);
      await apiFetch('/me/baskets', {});

      assert.deepEqual(getSession(), providerSession(renewed));
    });
  });

  describe('a session that changed while the provider was answering', () => {
    it("does not write renewed tokens into another account's session", async () => {
      const token = deferred<Response>();
      respondWith((call) => (toProvider(call) ? token.promise : json(401, unauthorized)));

      const request = apiFetch('/me/baskets', {});
      await settle();

      // Yanıt beklenirken kullanıcı çıkıp başka bir hesapla (bu kez parolayla) girer.
      const other = session({
        accountId: 'account-2',
        email: 'grace@example.com',
        accessToken: 'access-b',
        refreshToken: 'refresh-b',
      });
      await signIn(other);
      token.resolve(json(200, granted));

      await assert.rejects(request);
      assert.deepEqual(getSession(), other);
      assert.deepEqual([...items].sort(), stored(other).sort());
    });

    it('does not write renewed tokens into a newer session of the same account', async () => {
      const token = deferred<Response>();
      respondWith((call) => (toProvider(call) ? token.promise : json(401, unauthorized)));

      const request = apiFetch('/me/baskets', {});
      await settle();

      const again = providerSession({ accessToken: 'idp-access-9', refreshToken: 'idp-refresh-9' });
      await signIn(again);
      token.resolve(json(200, granted));

      await assert.rejects(request);
      assert.deepEqual(getSession(), again);
    });

    it('writes nothing when the user signed out', async () => {
      const token = deferred<Response>();
      respondWith((call) => (toProvider(call) ? token.promise : json(401, unauthorized)));

      const request = apiFetch('/me/baskets', {});
      await settle();
      await signOut();
      token.resolve(json(200, granted));

      await assert.rejects(request);
      assert.equal(getSession(), null);
      assert.equal(items.size, 0);
    });

    it('does not ask the provider for a 401 that arrives after signing out', async () => {
      const late = deferred<Response>();
      respondWith(() => late.promise);

      const request = apiFetch('/me/baskets', {});
      await settle();
      await signOut();
      late.resolve(json(401, unauthorized));

      await assert.rejects(request);
      assert.equal(calls.filter(toProvider).length, 0);
    });

    it('renews a password session at the backend after a provider session', async () => {
      await signIn(session());
      respondWith((call) => {
        if (call.url.endsWith('/auth/refresh')) {
          return json(200, { accessToken: 'access-2', refreshToken: 'refresh-2' });
        }

        return bearer(call) === 'Bearer access-2' ? json(200, {}) : json(401, unauthorized);
      });

      await apiFetch('/me/baskets', {});

      assert.equal(calls.filter(toProvider).length, 0);
      assert.deepEqual(callsTo('/auth/refresh')[0].body, { refreshToken: 'refresh-1' });
      assert.deepEqual(
        getSession(),
        session({ accessToken: 'access-2', refreshToken: 'refresh-2' })
      );
    });
  });

  // Sağlayıcı yenileme token'ı vermediyse oturum yenilenemez; erişim token'ı reddedilince biter.
  describe('a provider that issued no refresh token', () => {
    beforeEach(async () => {
      await signIn(providerSession({ refreshToken: null }));
    });

    it('works until the backend rejects the access token', async () => {
      respondWith(() => json(200, { ok: true }));

      await apiFetch('/me/baskets', {});

      assert.deepEqual(getSession(), providerSession({ refreshToken: null }));
    });

    it('signs out when the backend rejects the access token', async () => {
      respondWith(() => json(401, unauthorized));

      await assert.rejects(apiFetch('/me/baskets', {}), (error) => {
        assert.deepEqual(error, unauthorized);
        return true;
      });

      assert.equal(getSession(), null);
      assert.equal(items.size, 0);
      // Ne sağlayıcıya ne backend'in yenileme ucuna gidilir, istek de yinelenmez.
      assert.equal(calls.length, 1);
    });

    it('keeps the session for failures other than a rejected token', async () => {
      for (const status of [403, 404, 500]) {
        respondWith(() => json(status, { status, title: 'Error' }));

        await assert.rejects(apiFetch('/me/baskets', {}));
      }

      respondWith(() => {
        throw new TypeError('Network request failed');
      });
      await assert.rejects(apiFetch('/me/baskets', {}));

      assert.deepEqual(getSession(), providerSession({ refreshToken: null }));
    });

    it('signs out once for requests rejected at the same time', async () => {
      let published = 0;
      respondWith(() => json(401, unauthorized));
      const unsubscribe = subscribeSession(() => {
        published++;
      });

      await Promise.allSettled([
        apiFetch('/me/baskets', {}),
        apiFetch('/me/favorites', {}),
        apiFetch('/products', {}),
      ]);
      unsubscribe();

      assert.equal(getSession(), null);
      assert.equal(published, 1);
      assert.equal(calls.length, 3);
    });

    it('does not sign out for a 401 of a request that carried its own token', async () => {
      respondWith(() => json(401, unauthorized));

      await assert.rejects(
        apiFetch('/auth/me', { headers: { Authorization: 'Bearer someone-else' } })
      );

      assert.deepEqual(getSession(), providerSession({ refreshToken: null }));
    });
  });

  describe('renewSession', () => {
    it('renews a provider session on demand', async () => {
      assert.equal(await renewSession(), true);

      assert.equal(calls.length, 1);
      assert.equal(calls[0].url, TOKEN_ENDPOINT);
      assert.deepEqual(getSession(), providerSession(renewed));
    });

    it('reports a rejected renewal and signs out', async () => {
      respondWith(() => json(400, { error: 'invalid_grant' }));

      assert.equal(await renewSession(), false);
      assert.equal(getSession(), null);
    });

    it('leaves a session without a refresh token alone', async () => {
      // Yenilenemeyen oturumu yalnızca reddedilen bir istek bitirir; istek üzerine yenileme bitirmez.
      await signIn(providerSession({ refreshToken: null }));

      assert.equal(await renewSession(), false);

      assert.equal(calls.length, 0);
      assert.deepEqual(getSession(), providerSession({ refreshToken: null }));
    });
  });
});
