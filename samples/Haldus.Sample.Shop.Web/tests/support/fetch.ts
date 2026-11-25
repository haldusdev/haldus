// `fetch` taklidi: her isteği kaydeder ve testin verdiği işlevle yanıtlar.

export type Call = {
  url: string
  method: string
  headers: Headers
  body: unknown
  /** Form olarak gönderilen gövdenin alanları (kimlik sağlayıcının token ucuna giden istekler). */
  form: Record<string, string> | undefined
  /** İsteği yapanın vazgeçme işareti (zaman aşımı). */
  signal: AbortSignal | undefined
}

type Handler = (call: Call) => Response | Promise<Response>

export const calls: Call[] = []

let handler: Handler = () => {
  throw new Error("No response was configured for fetch.")
}

/** Kayıtlı istekleri temizler ve sonraki isteklerin yanıtını belirler. */
export function respondWith(next: Handler) {
  calls.length = 0
  handler = next
}

export function callsTo(path: string): Call[] {
  return calls.filter((call) => call.url.endsWith(path))
}

export function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  })
}

export function empty(status: number): Response {
  return new Response(null, { status })
}

export function text(
  status: number,
  body: string,
  statusText: string
): Response {
  return new Response(body, { status, statusText })
}

export type Deferred<T> = {
  promise: Promise<T>
  resolve: (value: T) => void
  reject: (reason: unknown) => void
}

/** Bir yanıtı testin seçtiği ana kadar bekletmek için. */
export function deferred<T>(): Deferred<T> {
  let resolve: (value: T) => void = () => undefined
  let reject: (reason: unknown) => void = () => undefined

  const promise = new Promise<T>((onResolve, onReject) => {
    resolve = onResolve
    reject = onReject
  })

  return { promise, resolve, reject }
}

/** Bekleyen mikro görevlerin ve onların başlattıklarının bitmesini bekler. */
export function settle(): Promise<void> {
  return new Promise((resolve) => setImmediate(resolve))
}

globalThis.fetch = async (input, init) => {
  const call: Call = {
    url: String(input),
    method: init?.method ?? "GET",
    headers: new Headers(init?.headers),
    body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
    form:
      init?.body instanceof URLSearchParams
        ? Object.fromEntries(init.body)
        : undefined,
    signal: init?.signal ?? undefined,
  }

  calls.push(call)
  return handler(call)
}
