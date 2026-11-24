import { useSyncExternalStore } from 'react';

import { readItem, removeItem, writeItem } from '@/lib/storage';

/**
 * Demo kimliği. Örnek backend kullanıcıyı ve kiracıyı istek başlıklarından okur (DemoIdentity); parola veya token
 * yoktur. Gerçek bir uygulamada burada kimlik sağlayıcının oturumu durur.
 */
export type Session = {
  userId: string;
  tenantId: string;
};

const STORAGE_KEY = 'haldus-shop.session';
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isGuid(value: string): boolean {
  return GUID.test(value);
}

/**
 * Yeni bir örnek kiracı kimliği. Yalnızca demo verisini ayırmak içindir; güvenlik amacı taşımaz, bu yüzden
 * kriptografik bir üreteç gerekmez.
 */
export function newTenantId(): string {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (character) => {
    const random = Math.floor(Math.random() * 16);
    return (character === 'x' ? random : (random % 4) + 8).toString(16);
  });
}

function parse(text: string | null): Session | null {
  try {
    const parsed: unknown = JSON.parse(text ?? 'null');

    if (
      typeof parsed === 'object' &&
      parsed !== null &&
      'userId' in parsed &&
      'tenantId' in parsed &&
      typeof parsed.userId === 'string' &&
      typeof parsed.tenantId === 'string' &&
      parsed.userId.length > 0 &&
      isGuid(parsed.tenantId)
    ) {
      return { userId: parsed.userId, tenantId: parsed.tenantId };
    }
  } catch {
    // Bozuk kayıt oturum yok sayılır.
  }

  return null;
}

let current: Session | null = null;
const listeners = new Set<() => void>();

function publish(next: Session | null) {
  current = next;
  listeners.forEach((listener) => listener());
}

export function getSession(): Session | null {
  return current;
}

/** Saklanan oturumu okur; uygulama açılırken bir kez çağrılır. */
export async function restoreSession(): Promise<void> {
  publish(parse(await readItem(STORAGE_KEY)));
}

export async function signIn(session: Session): Promise<void> {
  await writeItem(STORAGE_KEY, JSON.stringify(session));
  publish(session);
}

export async function signOut(): Promise<void> {
  await removeItem(STORAGE_KEY);
  publish(null);
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function useSession(): Session | null {
  return useSyncExternalStore(subscribe, getSession, getSession);
}
