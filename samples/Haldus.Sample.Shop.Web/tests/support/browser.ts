// Sınanan modüllerin dokunduğu tarayıcı nesnelerinin elle yazılmış taklitleri. Oturum modülü yüklenirken depoyu
// okuduğu ve `window`'a dinleyici eklediği için bu dosya ondan önce içe aktarılmalıdır.

class MemoryStorage {
  readonly items = new Map<string, string>()

  getItem(key: string): string | null {
    return this.items.get(key) ?? null
  }

  setItem(key: string, value: string) {
    this.items.set(key, value)
  }

  removeItem(key: string) {
    this.items.delete(key)
  }

  clear() {
    this.items.clear()
  }
}

type StorageListener = (event: { key: string | null }) => void

export const storage = new MemoryStorage()
export const lockRequests: string[] = []

const storageListeners: StorageListener[] = []
let lockQueue: Promise<unknown> = Promise.resolve()

// Gerçeği gibi aynı adı isteyenleri sıraya koyar.
const locks = {
  request<T>(name: string, work: () => Promise<T>): Promise<T> {
    lockRequests.push(name)

    const run = lockQueue.then(() => work())
    lockQueue = run.catch(() => undefined)
    return run
  },
}

function define(name: string, value: unknown) {
  Object.defineProperty(globalThis, name, {
    value,
    configurable: true,
    writable: true,
  })
}

/** Web Locks'u olmayan bir tarayıcıyı taklit etmek için kapatılabilir. */
export function setLocksAvailable(available: boolean) {
  define("navigator", available ? { locks } : {})
}

/** Başka bir sekmenin depoya yazdığını bu sekmeye bildiren olay. */
export function dispatchStorage(key: string | null) {
  storageListeners.forEach((listener) => listener({ key }))
}

define("localStorage", storage)
define("window", {
  addEventListener(type: string, listener: StorageListener) {
    if (type === "storage") {
      storageListeners.push(listener)
    }
  },
})
setLocksAvailable(true)
