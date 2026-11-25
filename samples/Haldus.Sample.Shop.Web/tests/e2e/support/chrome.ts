// Kurulu bir Chromium tarayıcısını (Chrome veya Edge) başsız açar ve DevTools protokolüyle sürer. Paket taşımaz:
// tarayıcı `node:child_process` ile başlatılır, protokol Node'un yerleşik WebSocket istemcisiyle konuşulur.
// Tıklama ve yazma gerçek girdi olaylarıdır (`Input.dispatchMouseEvent`, `Input.insertText`); bu yüzden Radix'in
// açılır pencereleri ve menüleri kullanıcının elindeki gibi davranır.
import { spawn, type ChildProcess } from "node:child_process"
import { existsSync } from "node:fs"
import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import path from "node:path"

const TIMEOUT = 8000
const POLL = 40

const candidates: Record<string, string[]> = {
  win32: [
    "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
    "C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe",
    "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
    "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
  ],
  darwin: [
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    "/Applications/Chromium.app/Contents/MacOS/Chromium",
  ],
  linux: [
    "/usr/bin/google-chrome",
    "/usr/bin/google-chrome-stable",
    "/usr/bin/chromium",
    "/usr/bin/chromium-browser",
    "/usr/bin/microsoft-edge",
  ],
}

/** Sürülecek tarayıcının yolu; `HALDUS_TEST_BROWSER` verilmediyse bilinen kurulum yerlerine bakılır. */
export function findBrowser(): string | undefined {
  const configured = process.env.HALDUS_TEST_BROWSER

  if (configured) {
    return existsSync(configured) ? configured : undefined
  }

  return (candidates[process.platform] ?? []).find((file) => existsSync(file))
}

type Message = {
  id?: number
  method?: string
  params?: Record<string, unknown>
  result?: Record<string, unknown>
  error?: { message: string }
  sessionId?: string
}

type Waiter = {
  resolve: (result: Record<string, unknown>) => void
  reject: (error: Error) => void
}

/** Bir öğeyi sayfada bulan JavaScript ifadesi; değeri öğenin kendisi ya da `null` olur. */
export type Locator = { expression: string; description: string }

function quote(value: string): string {
  return JSON.stringify(value)
}

/** Erişilebilir adıyla (aria-label ya da görünen metin) bir düğme veya bağlantı. */
export function button(name: string): Locator {
  return {
    expression: `__probe.named("button, a[href], [role=button]", ${quote(name)})`,
    description: `button "${name}"`,
  }
}

/** Etiketiyle (label, aria-label ya da placeholder) bir giriş alanı veya seçim kutusu. */
export function field(label: string): Locator {
  return {
    expression: `__probe.field(${quote(label)})`,
    description: `field "${label}"`,
  }
}

/** ARIA rolüyle ve adıyla bir öğe (`option`, `menuitem`, `menuitemradio`, `dialog`, `alertdialog`...). */
export function role(name: string, label?: string): Locator {
  return {
    expression: `__probe.named(${quote(`[role=${name}]`)}, ${label === undefined ? "undefined" : quote(label)})`,
    description: label === undefined ? name : `${name} "${label}"`,
  }
}

/** Metni tam olarak bu olan, görünen en içteki öğe. */
export function text(value: string): Locator {
  return {
    expression: `__probe.text(${quote(value)})`,
    description: `text "${value}"`,
  }
}

/** Metni bunu içeren, görünen en içteki öğe. */
export function containing(value: string): Locator {
  return {
    expression: `__probe.text(${quote(value)}, true)`,
    description: `text containing "${value}"`,
  }
}

export function css(selector: string): Locator {
  return {
    expression: `__probe.visible(${quote(selector)})[0] ?? null`,
    description: selector,
  }
}

// Sayfaya her belgeyle birlikte yüklenen yardımcılar. Yalnızca görünen öğeler bulunur: kapanmış bir pencerenin
// içeriği, `sr-only` olmayan gizli bir öğe veya yardımcı teknolojilerden gizlenmiş bir bölgenin (`aria-hidden`)
// içindekiler "var" sayılmaz. Sonuncusu açık bir pencerenin arkasında kalan sayfayı ve etkin olmayan sekmenin
// ekranını kapsar: ikisi de belgede durur ama kullanıcıya sunulmaz.
const probe = `
window.__probe = {
  shown(element) {
    if (!element.isConnected) return false
    if (element.closest('[aria-hidden="true"]')) return false
    const rect = element.getBoundingClientRect()
    if (rect.width === 0 && rect.height === 0) return false
    const style = getComputedStyle(element)
    return style.visibility !== "hidden" && style.display !== "none"
  },
  visible(selector) {
    return [...document.querySelectorAll(selector)].filter((element) => this.shown(element))
  },
  nameOf(element) {
    const label = element.getAttribute("aria-label")
    if (label) return label.trim()
    const labelledBy = element.getAttribute("aria-labelledby")
    if (labelledBy) {
      const text = labelledBy.split(/\\s+/).map((id) => document.getElementById(id)?.textContent ?? "").join(" ")
      if (text.trim()) return text.replace(/\\s+/g, " ").trim()
    }
    return (element.textContent ?? "").replace(/\\s+/g, " ").trim()
  },
  named(selector, name) {
    const found = this.visible(selector)
    return (name === undefined ? found[0] : found.find((element) => this.nameOf(element) === name)) ?? null
  },
  field(label) {
    for (const candidate of this.visible("label")) {
      if ((candidate.textContent ?? "").replace(/\\s+/g, " ").trim() === label) {
        const target = candidate.htmlFor ? document.getElementById(candidate.htmlFor) : candidate.querySelector("input, textarea, select")
        if (target) return target
      }
    }
    // Yalnızca ekran okuyucuya görünen etiketler (sr-only) boyutsuz olabilir.
    for (const candidate of document.querySelectorAll("label[for]")) {
      if ((candidate.textContent ?? "").replace(/\\s+/g, " ").trim() === label) {
        const target = document.getElementById(candidate.htmlFor)
        if (target && this.shown(target)) return target
      }
    }
    return this.visible("input, textarea, select, [role=combobox]").find((element) =>
      element.getAttribute("aria-label") === label || element.getAttribute("placeholder") === label) ?? null
  },
  text(value, partial) {
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_ELEMENT)
    let match = null
    for (let node = walker.currentNode; node; node = walker.nextNode()) {
      const content = (node.textContent ?? "").replace(/\\s+/g, " ").trim()
      if ((partial ? content.includes(value) : content === value) && this.shown(node)) match = node
    }
    return match
  },
  // Tıklanacak nokta: öğenin ortası. Üstünü başka bir şey örtüyorsa (açılış animasyonu, kapanmakta olan pencere)
  // null döner ve çağıran yeniden dener.
  target(element) {
    if (!element || !this.shown(element)) return null
    if (element.disabled || element.getAttribute("aria-disabled") === "true") return null
    element.scrollIntoView({ block: "center", inline: "center" })
    const rect = element.getBoundingClientRect()
    const x = rect.left + rect.width / 2
    const y = rect.top + rect.height / 2
    const hit = document.elementFromPoint(x, y)
    return hit && (element.contains(hit) || hit.contains(element)) ? { x, y } : null
  },
}
`

class Connection {
  private readonly socket: WebSocket
  private readonly waiters = new Map<number, Waiter>()
  private next = 1

  private constructor(socket: WebSocket) {
    this.socket = socket

    socket.addEventListener("message", (event) => {
      const message = JSON.parse(String(event.data)) as Message
      const waiter =
        message.id === undefined ? undefined : this.waiters.get(message.id)

      if (waiter && message.id !== undefined) {
        this.waiters.delete(message.id)

        if (message.error) {
          waiter.reject(new Error(message.error.message))
        } else {
          waiter.resolve(message.result ?? {})
        }
      }
    })

    socket.addEventListener("close", () => {
      for (const waiter of this.waiters.values()) {
        waiter.reject(new Error("The browser connection closed."))
      }

      this.waiters.clear()
    })
  }

  static open(url: string): Promise<Connection> {
    return new Promise((resolve, reject) => {
      const socket = new WebSocket(url)
      socket.addEventListener("open", () => resolve(new Connection(socket)))
      socket.addEventListener("error", () =>
        reject(new Error(`Could not connect to the browser at ${url}.`))
      )
    })
  }

  send(
    method: string,
    params: Record<string, unknown> = {},
    sessionId?: string
  ): Promise<Record<string, unknown>> {
    const id = this.next++

    return new Promise((resolve, reject) => {
      this.waiters.set(id, { resolve, reject })
      this.socket.send(JSON.stringify({ id, method, params, sessionId }))
    })
  }

  close() {
    this.socket.close()
  }
}

const keys: Record<string, { code: string; keyCode: number; text?: string }> = {
  Enter: { code: "Enter", keyCode: 13, text: "\r" },
  Escape: { code: "Escape", keyCode: 27 },
  Tab: { code: "Tab", keyCode: 9 },
  Backspace: { code: "Backspace", keyCode: 8 },
  ArrowDown: { code: "ArrowDown", keyCode: 40 },
  ArrowUp: { code: "ArrowUp", keyCode: 38 },
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds))
}

/** Bir sekme. Her işlem, hedefi görünür ve tıklanabilir olana kadar bekler; süre dolarsa neyi beklediğini söyler. */
export class Page {
  private readonly connection: Connection
  private readonly sessionId: string

  constructor(connection: Connection, sessionId: string) {
    this.connection = connection
    this.sessionId = sessionId
  }

  private send(method: string, params: Record<string, unknown> = {}) {
    return this.connection.send(method, params, this.sessionId)
  }

  async goto(url: string): Promise<void> {
    await this.send("Page.navigate", { url })
    await this.waitFor(
      `document.readyState === "complete" && typeof __probe === "object"`,
      `the page at ${url} to load`
    )
  }

  /** İfadeyi sayfada çalıştırır; sonucu JSON olarak döner. */
  async evaluate<T>(expression: string): Promise<T> {
    const response = await this.send("Runtime.evaluate", {
      expression,
      awaitPromise: true,
      returnByValue: true,
    })

    const details = response.exceptionDetails as
      { text: string; exception?: { description?: string } } | undefined

    if (details) {
      throw new Error(details.exception?.description ?? details.text)
    }

    return (response.result as { value: T }).value
  }

  /** İfade doğru bir değer verene kadar bekler ve o değeri döner. */
  async waitFor<T = unknown>(
    expression: string,
    what: string,
    timeout = TIMEOUT
  ): Promise<T> {
    const deadline = Date.now() + timeout
    let last: unknown

    for (;;) {
      try {
        const value = await this.evaluate<T>(expression)

        if (value) {
          return value
        }
      } catch (error) {
        // Gezinme sırasında bağlam değişebilir; bir sonraki yoklamada yeniden denenir.
        last = error
      }

      if (Date.now() > deadline) {
        const body = await this.evaluate<string>(
          `document.body ? document.body.innerText.slice(0, 600) : ""`
        ).catch(() => "")

        throw new Error(
          `Timed out waiting for ${what}.${last ? ` Last error: ${String(last)}.` : ""}\nPage text:\n${body}`
        )
      }

      await delay(POLL)
    }
  }

  async see(locator: Locator): Promise<void> {
    await this.waitFor(
      `(() => { const e = ${locator.expression}; return !!e && __probe.shown(e) })()`,
      `${locator.description} to appear`
    )
  }

  async gone(locator: Locator): Promise<void> {
    await this.waitFor(
      `(() => { const e = ${locator.expression}; return !e || !__probe.shown(e) })()`,
      `${locator.description} to disappear`
    )
  }

  async exists(locator: Locator): Promise<boolean> {
    return this.evaluate<boolean>(
      `(() => { const e = ${locator.expression}; return !!e && __probe.shown(e) })()`
    )
  }

  async textOf(locator: Locator): Promise<string> {
    await this.see(locator)
    return this.evaluate<string>(
      `(${locator.expression}).textContent.replace(/\\s+/g, " ").trim()`
    )
  }

  async valueOf(locator: Locator): Promise<string> {
    await this.see(locator)
    return this.evaluate<string>(`(${locator.expression}).value`)
  }

  async attribute(locator: Locator, name: string): Promise<string | null> {
    await this.see(locator)
    return this.evaluate<string | null>(
      `(${locator.expression}).getAttribute(${quote(name)})`
    )
  }

  async click(locator: Locator): Promise<void> {
    const point = await this.waitFor<{ x: number; y: number }>(
      `__probe.target(${locator.expression})`,
      `${locator.description} to be clickable`
    )

    const event = { x: point.x, y: point.y, button: "left", clickCount: 1 }
    await this.send("Input.dispatchMouseEvent", {
      type: "mouseMoved",
      x: point.x,
      y: point.y,
    })
    await this.send("Input.dispatchMouseEvent", {
      ...event,
      type: "mousePressed",
    })
    await this.send("Input.dispatchMouseEvent", {
      ...event,
      type: "mouseReleased",
    })
  }

  /** Alanı tıklar, içindekini seçer ve yerine verilen metni yazar. */
  async fill(locator: Locator, value: string): Promise<void> {
    await this.click(locator)
    await this.evaluate(
      `(() => { const e = ${locator.expression}; e.focus(); e.select?.() })()`
    )

    if (value === "") {
      await this.press("Backspace")
    } else {
      await this.send("Input.insertText", { text: value })
    }
  }

  /** Odaktaki alana, içindekini silmeden, imlecin olduğu yere yazar. */
  async type(value: string): Promise<void> {
    await this.send("Input.insertText", { text: value })
  }

  async press(key: keyof typeof keys | string): Promise<void> {
    const known = keys[key]

    if (!known) {
      throw new Error(`Unknown key: ${key}`)
    }

    const event = {
      key,
      code: known.code,
      windowsVirtualKeyCode: known.keyCode,
      nativeVirtualKeyCode: known.keyCode,
    }

    await this.send("Input.dispatchKeyEvent", {
      ...event,
      type: known.text ? "keyDown" : "rawKeyDown",
      text: known.text,
    })
    await this.send("Input.dispatchKeyEvent", { ...event, type: "keyUp" })
  }

  /** Adres çubuğundaki yol (`/products`). */
  path(): Promise<string> {
    return this.evaluate<string>("location.pathname")
  }

  async waitForPath(expected: string): Promise<void> {
    await this.waitFor(
      `location.pathname === ${quote(expected)}`,
      `the address to become ${expected}`
    )
  }

  storage(key: string): Promise<string | null> {
    return this.evaluate<string | null>(`localStorage.getItem(${quote(key)})`)
  }

  async setStorage(key: string, value: string): Promise<void> {
    await this.evaluate(`localStorage.setItem(${quote(key)}, ${quote(value)})`)
  }
}

/** Kendi çerezleri ve deposu olan, öteki testlerden yalıtılmış bir tarayıcı oturumu; içinde birden çok sekme açılır. */
export class Context {
  private readonly connection: Connection
  private readonly id: string

  constructor(connection: Connection, id: string) {
    this.connection = connection
    this.id = id
  }

  async page(): Promise<Page> {
    const { targetId } = await this.connection.send("Target.createTarget", {
      url: "about:blank",
      browserContextId: this.id,
    })
    const { sessionId } = await this.connection.send("Target.attachToTarget", {
      targetId,
      flatten: true,
    })

    const session = String(sessionId)
    await this.connection.send("Page.enable", {}, session)
    await this.connection.send("Runtime.enable", {}, session)
    await this.connection.send(
      "Page.addScriptToEvaluateOnNewDocument",
      { source: probe },
      session
    )
    await this.connection.send(
      "Emulation.setDeviceMetricsOverride",
      { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false },
      session
    )
    // Arka plandaki sekme de odaklıymış gibi davranır; yoksa odak isteyen bileşenler (menü, pencere) beklerdi.
    await this.connection.send(
      "Emulation.setFocusEmulationEnabled",
      { enabled: true },
      session
    )

    return new Page(this.connection, session)
  }

  async close(): Promise<void> {
    await this.connection
      .send("Target.disposeBrowserContext", { browserContextId: this.id })
      .catch(() => undefined)
  }
}

export class Browser {
  private readonly child: ChildProcess
  private readonly connection: Connection
  private readonly profile: string

  private constructor(
    child: ChildProcess,
    connection: Connection,
    profile: string
  ) {
    this.child = child
    this.connection = connection
    this.profile = profile
  }

  static async launch(executable: string): Promise<Browser> {
    const profile = await mkdtemp(path.join(tmpdir(), "haldus-e2e-"))

    const child = spawn(
      executable,
      [
        "--headless=new",
        "--remote-debugging-port=0",
        `--user-data-dir=${profile}`,
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-gpu",
        "--disable-extensions",
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-sync",
        "--lang=en-US",
        "--window-size=1280,900",
        "about:blank",
      ],
      { stdio: ["ignore", "ignore", "pipe"] }
    )

    // Tarayıcı dinlediği adresi hata akışına yazar; bağlantı noktasını işletim sistemi seçer (0).
    const endpoint = await new Promise<string>((resolve, reject) => {
      let output = ""
      const timer = setTimeout(
        () => reject(new Error(`The browser did not start:\n${output}`)),
        20000
      )

      child.stderr?.on("data", (chunk: Buffer) => {
        output += chunk.toString()
        const match = /DevTools listening on (ws:\/\/\S+)/.exec(output)

        if (match) {
          clearTimeout(timer)
          resolve(match[1])
        }
      })

      child.once("exit", (code) => {
        clearTimeout(timer)
        reject(new Error(`The browser exited with code ${code}:\n${output}`))
      })
    })

    return new Browser(child, await Connection.open(endpoint), profile)
  }

  async context(): Promise<Context> {
    const { browserContextId } = await this.connection.send(
      "Target.createBrowserContext",
      { disposeOnDetach: true }
    )

    return new Context(this.connection, String(browserContextId))
  }

  async close(): Promise<void> {
    const exited = new Promise<void>((resolve) =>
      this.child.once("exit", () => resolve())
    )

    await this.connection.send("Browser.close").catch(() => undefined)
    this.connection.close()

    await Promise.race([exited, delay(5000)])

    if (this.child.exitCode === null) {
      this.child.kill()
    }

    // Tarayıcı kapanırken profil dosyalarını bir süre daha tutabilir.
    await rm(this.profile, {
      recursive: true,
      force: true,
      maxRetries: 10,
      retryDelay: 200,
    }).catch(() => undefined)
  }
}
