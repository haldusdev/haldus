import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';

import { items } from './stubs/expo-secure-store.ts';
import { dispatchStorage, storage } from './support/web-platform.ts';
import { KEYS, session, stored } from './support/fixtures.ts';
import { settle } from './support/fetch.ts';

import { getSession, signIn, signOut, subscribeSession } from '@/lib/session';

function store(entries: [string, string][]) {
  storage.clear();
  entries.forEach(([key, value]) => storage.set(key, value));
}

// Expo'nun web hedefi: depo `localStorage`'dır ve başka sekmelerin yazdıkları izlenir.
describe('session in the browser', () => {
  beforeEach(async () => {
    await signOut();
    storage.clear();
  });

  it('keeps the session in localStorage and not in the secure store', async () => {
    await signIn(session());

    assert.deepEqual([...storage].sort(), stored(session()).sort());
    assert.equal(items.size, 0);
  });

  it('signOut removes every key', async () => {
    await signIn(session());

    await signOut();

    assert.equal(storage.size, 0);
  });

  for (const [name, key] of Object.entries(KEYS)) {
    it(`follows another tab that wrote the ${name} entry`, async () => {
      await signIn(session());
      let published = 0;
      const unsubscribe = subscribeSession(() => {
        published++;
      });
      const renewed = session({ accessToken: 'access-2', refreshToken: 'refresh-2' });
      store(stored(renewed));

      dispatchStorage(key);
      await settle();
      unsubscribe();

      assert.deepEqual(getSession(), renewed);
      assert.equal(published, 1);
    });
  }

  it('follows another tab that signed out', async () => {
    await signIn(session());
    storage.clear();

    dispatchStorage(KEYS.refreshToken);
    await settle();

    assert.equal(getSession(), null);
  });

  it('ignores a storage event for another key', async () => {
    await signIn(session());
    store(stored(session({ accessToken: 'access-2' })));

    dispatchStorage('haldus-shop.language');
    await settle();

    assert.deepEqual(getSession(), session());
  });
});
