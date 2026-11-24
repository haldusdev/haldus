import * as SecureStore from 'expo-secure-store';
import { Platform } from 'react-native';

/**
 * Küçük, kalıcı anahtar-değer deposu. Cihazda işletim sisteminin güvenli deposu (Keychain / Keystore) kullanılır;
 * tarayıcıda o yoktur, yerine localStorage geçer.
 */
export async function readItem(key: string): Promise<string | null> {
  if (Platform.OS === 'web') {
    return globalThis.localStorage?.getItem(key) ?? null;
  }

  return SecureStore.getItemAsync(key);
}

export async function writeItem(key: string, value: string): Promise<void> {
  if (Platform.OS === 'web') {
    globalThis.localStorage?.setItem(key, value);
    return;
  }

  await SecureStore.setItemAsync(key, value);
}

export async function removeItem(key: string): Promise<void> {
  if (Platform.OS === 'web') {
    globalThis.localStorage?.removeItem(key);
    return;
  }

  await SecureStore.deleteItemAsync(key);
}
