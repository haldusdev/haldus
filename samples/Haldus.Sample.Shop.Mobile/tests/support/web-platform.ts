// Expo'nun web hedefini taklit eder: platform `web` olur, depo `localStorage`'dır. Oturum modülü yüklenirken platforma
// baktığı için bu dosya ondan önce içe aktarılmalıdır.
import { Platform } from '../stubs/react-native.ts';

type StorageListener = (event: { key: string | null }) => void;

export const storage = new Map<string, string>();

const storageListeners: StorageListener[] = [];

function define(name: string, value: unknown) {
  Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
}

/** Başka bir sekmenin depoya yazdığını bu sekmeye bildiren olay. */
export function dispatchStorage(key: string | null) {
  storageListeners.forEach((listener) => listener({ key }));
}

Platform.OS = 'web';

define('localStorage', {
  getItem: (key: string) => storage.get(key) ?? null,
  setItem: (key: string, value: string) => void storage.set(key, value),
  removeItem: (key: string) => void storage.delete(key),
});
define('window', {
  addEventListener(type: string, listener: StorageListener) {
    if (type === 'storage') {
      storageListeners.push(listener);
    }
  },
});
