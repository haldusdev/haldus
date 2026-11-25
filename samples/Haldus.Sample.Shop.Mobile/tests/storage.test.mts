import assert from 'node:assert/strict';
import { afterEach, beforeEach, describe, it } from 'node:test';

import { items } from './stubs/expo-secure-store.ts';
import { Platform } from './stubs/react-native.ts';

import { readItem, removeItem, writeItem } from '@/lib/storage';

function setLocalStorage(value: unknown) {
  Object.defineProperty(globalThis, 'localStorage', { value, configurable: true, writable: true });
}

describe('storage', () => {
  const browser = new Map<string, string>();

  beforeEach(() => {
    items.clear();
    browser.clear();
    setLocalStorage({
      getItem: (key: string) => browser.get(key) ?? null,
      setItem: (key: string, value: string) => void browser.set(key, value),
      removeItem: (key: string) => void browser.delete(key),
    });
  });

  afterEach(() => {
    Platform.OS = 'ios';
  });

  for (const os of ['ios', 'android']) {
    it(`uses the secure store on ${os}`, async () => {
      Platform.OS = os;

      assert.equal(await readItem('token'), null);

      await writeItem('token', 'secret');

      assert.equal(items.get('token'), 'secret');
      assert.equal(await readItem('token'), 'secret');
      assert.equal(browser.size, 0);

      await removeItem('token');

      assert.equal(items.size, 0);
      assert.equal(await readItem('token'), null);
    });
  }

  it('uses localStorage in the browser', async () => {
    Platform.OS = 'web';

    assert.equal(await readItem('token'), null);

    await writeItem('token', 'secret');

    assert.equal(browser.get('token'), 'secret');
    assert.equal(await readItem('token'), 'secret');
    assert.equal(items.size, 0);

    await removeItem('token');

    assert.equal(browser.size, 0);
  });

  it('works without localStorage when the web target is rendered on a server', async () => {
    Platform.OS = 'web';
    setLocalStorage(undefined);

    await writeItem('token', 'secret');
    await removeItem('token');

    assert.equal(await readItem('token'), null);
    assert.equal(items.size, 0);
  });
});
