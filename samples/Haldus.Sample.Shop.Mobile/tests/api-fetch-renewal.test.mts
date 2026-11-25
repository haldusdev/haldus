import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';

import { items } from './stubs/expo-secure-store.ts';
import { calls, callsTo, deferred, json, respondWith, settle, text } from './support/fetch.ts';
import type { Call } from './support/fetch.ts';
import { KEYS, session, stored, unauthorized } from './support/fixtures.ts';

// Yenileme işlevini kaydeder; yenileme isteği üretilen istemciden ve `fetch` taklidinden geçer.
import '@/lib/auth';
import { apiFetch, renewSession } from '@/lib/api-fetch';
import { getSession, signIn, signOut, subscribeSession } from '@/lib/session';

type Result = { data: unknown; status: number };

const renewedTokens = { accessToken: 'access-2', refreshToken: 'refresh-2' };
const expired = { ...unauthorized, detail: 'The session has expired.' };

function bearer(call: Call): string | null {
  return call.headers.get('Authorization');
}

/**
 * Örnek backend gibi davranır: yenileme ucu yeni token'ları verir, öteki uçlar eski erişim token'ını 401 ile
 * reddeder ve yenisini kabul eder.
 */
function backend(call: Call): Response {
  if (call.url.endsWith('/auth/refresh')) {
    return json(200, renewedTokens);
  }

  return bearer(call) === 'Bearer access-2' ? json(200, { path: call.url }) : json(401, expired);
}

describe('apiFetch renewal', () => {
  beforeEach(async () => {
    await signIn(session());
    respondWith(backend);
  });

  it('renews once after a 401 and repeats the request with the new token', async () => {
    const result = await apiFetch<Result>('/me/baskets', {});

    assert.deepEqual(
      calls.map((call) => [call.url, bearer(call)]),
      [
        ['http://shop.test/me/baskets', 'Bearer access-1'],
        ['http://shop.test/auth/refresh', null],
        ['http://shop.test/me/baskets', 'Bearer access-2'],
      ]
    );
    assert.deepEqual(calls[1].body, { refreshToken: 'refresh-1' });
    assert.equal(calls[1].method, 'POST');
    assert.deepEqual(result.data, { path: 'http://shop.test/me/baskets' });
    assert.equal(result.status, 200);
  });

  it('stores the renewed tokens and tells subscribers', async () => {
    let published = 0;
    const unsubscribe = subscribeSession(() => {
      published++;
    });

    await apiFetch('/me/baskets', {});
    unsubscribe();

    assert.deepEqual(getSession(), session(renewedTokens));
    assert.deepEqual([...items].sort(), stored(session(renewedTokens)).sort());
    assert.equal(published, 1);
  });

  it('repeats the request with its method and body', async () => {
    await apiFetch('/me/baskets', {
      method: 'POST',
      body: JSON.stringify({ note: 'hello' }),
      headers: { 'Content-Type': 'application/json' },
    });

    const [first, , repeated] = calls;
    assert.equal(repeated.method, 'POST');
    assert.deepEqual(repeated.body, first.body);
    assert.equal(repeated.headers.get('Content-Type'), 'application/json');
  });

  it('does not renew a second time when the repeated request is rejected too', async () => {
    respondWith((call) =>
      call.url.endsWith('/auth/refresh') ? json(200, renewedTokens) : json(401, expired)
    );

    await assert.rejects(apiFetch('/me/baskets', {}), (error) => {
      assert.deepEqual(error, expired);
      return true;
    });

    assert.equal(callsTo('/auth/refresh').length, 1);
    assert.equal(callsTo('/me/baskets').length, 2);
    // Yenileme başarılıydı; reddedilen istek oturumu kapatmaz.
    assert.deepEqual(getSession(), session(renewedTokens));
  });

  it('shares one refresh between requests rejected at the same time', async () => {
    const refresh = deferred<Response>();
    respondWith((call) => (call.url.endsWith('/auth/refresh') ? refresh.promise : backend(call)));

    const pending = Promise.all([
      apiFetch<Result>('/me/baskets', {}),
      apiFetch<Result>('/me/favorites', {}),
      apiFetch<Result>('/products', {}),
    ]);
    await settle();

    assert.equal(callsTo('/auth/refresh').length, 1);

    refresh.resolve(json(200, renewedTokens));
    const results = await pending;

    assert.equal(callsTo('/auth/refresh').length, 1);
    assert.deepEqual(
      results.map((result) => result.data),
      [
        { path: 'http://shop.test/me/baskets' },
        { path: 'http://shop.test/me/favorites' },
        { path: 'http://shop.test/products' },
      ]
    );
    assert.deepEqual(calls.slice(-3).map(bearer), Array(3).fill('Bearer access-2'));
  });

  it('does not renew again for a 401 that arrives after the renewal', async () => {
    const late = deferred<Response>();
    respondWith((call) =>
      call.url.endsWith('/slow') && bearer(call) === 'Bearer access-1'
        ? late.promise
        : backend(call)
    );

    const slow = apiFetch<Result>('/slow', {});
    await apiFetch('/me/baskets', {});
    assert.equal(callsTo('/auth/refresh').length, 1);

    // Eski token'la gönderilmiş istek, yenileme bittikten sonra reddedilir.
    late.resolve(json(401, expired));
    const result = await slow;

    assert.equal(callsTo('/auth/refresh').length, 1);
    assert.deepEqual(result.data, { path: 'http://shop.test/slow' });
    assert.equal(bearer(calls[calls.length - 1]), 'Bearer access-2');
  });

  it('renews again when the renewed token expires later', async () => {
    await apiFetch('/me/baskets', {});

    respondWith((call) => {
      if (call.url.endsWith('/auth/refresh')) {
        return json(200, { accessToken: 'access-3', refreshToken: 'refresh-3' });
      }

      return bearer(call) === 'Bearer access-3' ? json(200, {}) : json(401, expired);
    });
    await apiFetch('/me/baskets', {});

    assert.deepEqual(callsTo('/auth/refresh')[0].body, { refreshToken: 'refresh-2' });
    assert.equal(getSession()?.refreshToken, 'refresh-3');
  });

  it('signs out when the backend rejects the refresh', async () => {
    respondWith((call) =>
      call.url.endsWith('/auth/refresh') ? json(401, unauthorized) : json(401, expired)
    );

    await assert.rejects(apiFetch('/me/baskets', {}), (error) => {
      // İsteğin kendi hatası döner, yenilemeninki değil.
      assert.deepEqual(error, expired);
      return true;
    });

    assert.equal(getSession(), null);
    assert.equal(items.size, 0);
    assert.equal(callsTo('/auth/refresh').length, 1);
    assert.equal(callsTo('/me/baskets').length, 1);
  });

  it('signs out when the refresh is rejected with a body that is not JSON', async () => {
    respondWith((call) =>
      call.url.endsWith('/auth/refresh') ? text(401, '', 'Unauthorized') : json(401, expired)
    );

    await assert.rejects(apiFetch('/me/baskets', {}));

    assert.equal(getSession(), null);
    assert.equal(items.size, 0);
  });

  it('keeps the session when the refresh fails with a network error', async () => {
    respondWith((call) => {
      if (call.url.endsWith('/auth/refresh')) {
        throw new TypeError('Network request failed');
      }

      return json(401, expired);
    });

    await assert.rejects(apiFetch('/me/baskets', {}), (error) => {
      assert.deepEqual(error, expired);
      return true;
    });

    assert.deepEqual(getSession(), session());
    assert.equal(items.get(KEYS.refreshToken), 'refresh-1');
    assert.equal(callsTo('/me/baskets').length, 1);
  });

  it('can renew after a network error', async () => {
    respondWith((call) => {
      if (call.url.endsWith('/auth/refresh')) {
        throw new TypeError('Network request failed');
      }

      return json(401, expired);
    });
    await assert.rejects(apiFetch('/me/baskets', {}));

    respondWith(backend);
    await apiFetch('/me/baskets', {});

    assert.deepEqual(getSession(), session(renewedTokens));
  });

  for (const route of ['/auth/login', '/auth/register']) {
    it(`does not renew after a 401 from ${route}`, async () => {
      respondWith(() => json(401, unauthorized));

      await assert.rejects(apiFetch(route, { method: 'POST' }), (error) => {
        assert.deepEqual(error, unauthorized);
        return true;
      });

      assert.equal(calls.length, 1);
      assert.deepEqual(getSession(), session());
    });
  }

  it('does not renew for other failures', async () => {
    respondWith(() => json(403, { title: 'Forbidden', status: 403 }));

    await assert.rejects(apiFetch('/me/baskets', {}));

    assert.equal(calls.length, 1);
    assert.deepEqual(getSession(), session());
  });

  it('does not refresh for a 401 that arrives after signing out', async () => {
    const late = deferred<Response>();
    respondWith(() => late.promise);

    const request = apiFetch('/me/baskets', {});
    await settle();
    await signOut();
    late.resolve(json(401, expired));

    await assert.rejects(request);
    assert.equal(callsTo('/auth/refresh').length, 0);
    assert.equal(getSession(), null);
  });

  describe('renewSession', () => {
    it('renews the tokens on demand', async () => {
      assert.equal(await renewSession(), true);

      assert.equal(calls.length, 1);
      assert.deepEqual(calls[0].body, { refreshToken: 'refresh-1' });
      assert.deepEqual(getSession(), session(renewedTokens));
    });

    it('does nothing when nobody is signed in', async () => {
      await signOut();

      assert.equal(await renewSession(), false);
      assert.equal(calls.length, 0);
    });

    it('reports a rejected refresh and signs out', async () => {
      respondWith(() => json(401, unauthorized));

      assert.equal(await renewSession(), false);
      assert.equal(getSession(), null);
    });

    it('shares the refresh with a request rejected at the same time', async () => {
      const refresh = deferred<Response>();
      respondWith((call) => (call.url.endsWith('/auth/refresh') ? refresh.promise : backend(call)));

      const request = apiFetch<Result>('/me/baskets', {});
      await settle();
      const renewed = renewSession();
      await settle();
      refresh.resolve(json(200, renewedTokens));

      assert.equal(await renewed, true);
      assert.deepEqual((await request).data, { path: 'http://shop.test/me/baskets' });
      assert.equal(callsTo('/auth/refresh').length, 1);
    });
  });
});
