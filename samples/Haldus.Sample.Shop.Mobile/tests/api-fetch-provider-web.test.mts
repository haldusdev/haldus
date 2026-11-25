import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';

// Platformu `web` yapar; oturum modülünden önce yüklenmelidir.
import { storage } from './support/web-platform.ts';
import { calls, deferred, json, respondWith, settle } from './support/fetch.ts';
import type { Call } from './support/fetch.ts';
import { providerSession, stored, unauthorized } from './support/fixtures.ts';
import type { Session } from '@/lib/session';

import '@/lib/auth';
import { apiFetch } from '@/lib/api-fetch';
import { getSession, signIn, signOut } from '@/lib/session';

const TOKEN_ENDPOINT = 'https://id.test/connect/token';

function bearer(call: Call): string | null {
  return call.headers.get('Authorization');
}

function toProvider(call: Call): boolean {
  return call.url === TOKEN_ENDPOINT;
}

/** Başka bir sekmenin depoya yazması; `storage` olayı bu sekmeye henüz ulaşmamıştır. */
function writtenByAnotherTab(session: Session | null) {
  storage.clear();
  (session ? stored(session) : []).forEach(([key, value]) => storage.set(key, value));
}

// Expo'nun web hedefi: iki sekme aynı oturumu paylaşır. Backend'in parola oturumu için verdiği 409'un sağlayıcıda
// karşılığı yoktur; aynı yenileme token'ını ikinci kullanan sekme düz bir ret alır.
describe('apiFetch renewal of a provider session in the browser', () => {
  beforeEach(async () => {
    await signOut();
    storage.clear();
    await signIn(providerSession());
  });

  it('continues with the tokens another tab obtained with the same refresh token', async () => {
    const renewedElsewhere = providerSession({
      accessToken: 'idp-access-2',
      refreshToken: 'idp-refresh-2',
    });
    respondWith((call) => {
      if (toProvider(call)) {
        // Öteki sekme yenileme token'ını az önce kullandı ve yenilerini yazdı; sağlayıcı bu sekmeninkini reddeder.
        writtenByAnotherTab(renewedElsewhere);
        return json(400, { error: 'invalid_grant' });
      }

      return bearer(call) === 'Bearer idp-access-2'
        ? json(200, { ok: true })
        : json(401, unauthorized);
    });

    await apiFetch('/me/baskets', {});

    assert.deepEqual(getSession(), renewedElsewhere);
    assert.deepEqual([...storage].sort(), stored(renewedElsewhere).sort());
    assert.equal(bearer(calls[calls.length - 1]), 'Bearer idp-access-2');
    assert.equal(calls.filter(toProvider).length, 1);
  });

  it('signs out when the provider rejects and no other tab renewed', async () => {
    respondWith((call) =>
      toProvider(call) ? json(400, { error: 'invalid_grant' }) : json(401, unauthorized)
    );

    await assert.rejects(apiFetch('/me/baskets', {}));

    assert.equal(getSession(), null);
    assert.equal(storage.size, 0);
  });

  it('does not sign out the account another tab signed in while the provider rejected', async () => {
    const other = providerSession({
      accountId: 'account-2',
      email: 'grace@example.com',
      accessToken: 'idp-access-b',
      refreshToken: 'idp-refresh-b',
    });
    respondWith((call) => {
      if (toProvider(call)) {
        writtenByAnotherTab(other);
        return json(400, { error: 'invalid_grant' });
      }

      return json(401, unauthorized);
    });

    await assert.rejects(apiFetch('/me/baskets', {}));

    assert.deepEqual(getSession(), other);
    assert.deepEqual([...storage].sort(), stored(other).sort());
  });

  it("does not write renewed tokens over another tab's newer session", async () => {
    const token = deferred<Response>();
    const other = providerSession({
      accountId: 'account-2',
      email: 'grace@example.com',
      accessToken: 'idp-access-b',
      refreshToken: 'idp-refresh-b',
    });
    respondWith((call) => (toProvider(call) ? token.promise : json(401, unauthorized)));

    const request = apiFetch('/me/baskets', {});
    await settle();
    writtenByAnotherTab(other);
    token.resolve(json(200, { access_token: 'idp-access-2', refresh_token: 'idp-refresh-2' }));

    await assert.rejects(request);
    assert.deepEqual(getSession(), other);
    assert.deepEqual([...storage].sort(), stored(other).sort());
  });

  describe('without a refresh token', () => {
    beforeEach(async () => {
      await signIn(providerSession({ refreshToken: null }));
    });

    it('signs out when the backend rejects the access token', async () => {
      respondWith(() => json(401, unauthorized));

      await assert.rejects(apiFetch('/me/baskets', {}));

      assert.equal(getSession(), null);
      assert.equal(storage.size, 0);
    });

    it('does not sign out the session another tab opened in the meantime', async () => {
      // Öteki sekme de yenileme token'ı olmayan bir oturum açtı: iki oturumun yenileme token'ı "yok" olarak aynıdır,
      // onları hesap ve erişim token'ı ayırır.
      const other = providerSession({
        accountId: 'account-2',
        email: 'grace@example.com',
        accessToken: 'idp-access-b',
        refreshToken: null,
      });
      respondWith(() => {
        writtenByAnotherTab(other);
        return json(401, unauthorized);
      });

      await assert.rejects(apiFetch('/me/baskets', {}));

      assert.deepEqual(getSession(), other);
      assert.deepEqual([...storage].sort(), stored(other).sort());
    });
  });
});
