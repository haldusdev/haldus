// Etkileşim testlerinin konuştuğu sunucu: panelin derlenmiş halini (`dist`) ve örnek mağazanın API'sinin bellekte
// duran bir taklidini aynı adresten sunar (`/api`; panel de geliştirmede backend'e bu önekle ulaşır).
//
// Taklit backend'in yerine geçmez; panelin bir tıklamadan sonra hangi isteği gönderdiğini ve gelen yanıtı nasıl
// gösterdiğini sınamak içindir. Yanıtların biçimi orval'ın ürettiği tiplerle yazılır: şema değişirse bu dosya
// tip denetiminden geçmez (`pnpm typecheck`).
//
// Aynı sunucu, istenirse, bir kimlik sağlayıcı gibi de davranır (`/idp`; identity-provider.ts): panelin sağlayıcıyla
// giriş akışı gerçek yönlendirmelerle, başka bir sunucu açmadan sınanır.
import { readFile } from "node:fs/promises"
import {
  createServer,
  type IncomingMessage,
  type Server,
  type ServerResponse,
} from "node:http"
import type { AddressInfo } from "node:net"
import path from "node:path"

import type {
  AccessTokenResponse,
  AccountInfo,
  AuthConfig,
  CategoryResponse,
  HttpValidationProblemDetails,
  OrderResponse,
  OrderStatus,
  ProblemDetails,
  ProductResponse,
} from "@/api/model"

import {
  CLIENT_ID,
  IdentityProvider,
  PROVIDER_PATH,
  SCOPES,
} from "./identity-provider.ts"

const dist = path.resolve(import.meta.dirname, "../../../dist")

const types: Record<string, string> = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json",
  ".svg": "image/svg+xml",
  ".woff2": "font/woff2",
  ".ico": "image/x-icon",
}

// Backend mesajlarını isteğin diliyle yazar; taklit de öyle yapar ki panelin dili başlıkla taşıdığı görülsün.
const messages = {
  en: {
    invalidCredentials: "The e-mail address or password is incorrect.",
    emailTaken: "This e-mail address is already registered.",
    invalidCode: "The code is invalid or has expired.",
    staffOnly: "Only staff can change this resource.",
    nameRequired: "The Name field is required.",
    priceRange: "The field Price must be between 0 and 1000000.",
    passwordLength: "The password must be at least 8 characters.",
    reasonRequired: "The Reason field is required.",
    customerRequired: "The CustomerName field is required.",
    inUse: "The record is referenced by other records.",
    wrongState: "The order cannot do this in its current state.",
    notFound: "The record was not found.",
  },
  tr: {
    invalidCredentials: "E-posta adresi veya parola hatalı.",
    emailTaken: "Bu e-posta adresi zaten kayıtlı.",
    invalidCode: "Kod geçersiz veya süresi dolmuş.",
    staffOnly: "Bu kaynağı yalnızca personel değiştirebilir.",
    nameRequired: "Name alanı zorunludur.",
    priceRange: "Price alanı 0 ile 1000000 arasında olmalıdır.",
    passwordLength: "Parola en az 8 karakter olmalıdır.",
    reasonRequired: "Reason alanı zorunludur.",
    customerRequired: "CustomerName alanı zorunludur.",
    inUse: "Kayda başka kayıtlar başvuruyor.",
    wrongState: "Sipariş bu durumda bu işlemi yapamaz.",
    notFound: "Kayıt bulunamadı.",
  },
} as const

type MessageKey = keyof (typeof messages)["en"]

export type Recorded = {
  method: string
  path: string
  query: URLSearchParams
  headers: IncomingMessage["headers"]
  body: unknown
}

type Account = AccountInfo & { password: string }

type Reply = {
  status: number
  body?: unknown
  headers?: Record<string, string>
}

type Call = Recorded & { language: "en" | "tr"; account: Account | undefined }

class Rejection {
  readonly reply: Reply

  constructor(reply: Reply) {
    this.reply = reply
  }
}

function problem(status: number, detail: string): Reply {
  const body: ProblemDetails = { status, title: "Error", detail }
  return { status, body }
}

function invalid(errors: Record<string, string[]>): Reply {
  const body: HttpValidationProblemDetails = {
    status: 400,
    title: "One or more validation errors occurred.",
    errors,
  }
  return { status: 400, body }
}

function record(value: unknown): Record<string, unknown> {
  return typeof value === "object" && value !== null
    ? (value as Record<string, unknown>)
    : {}
}

function text(value: unknown): string {
  return typeof value === "string" ? value : ""
}

/** Örnek mağazanın bellekteki taklidi. Testler durumu doğrudan kurar ve gelen istekleri `requests` içinden okur. */
export class Shop {
  readonly requests: Recorded[] = []
  readonly accounts: Account[] = []
  products: ProductResponse[] = []
  categories: CategoryResponse[] = []
  orders: OrderResponse[] = []

  /** Gönderilen son doğrulama / sıfırlama kodu (backend'in e-postayla yolladığı). */
  code = "K7M2P9QX"

  /** Yenileme isteğine verilecek yanıt; `rotate` yeni token'lar verir. */
  refreshBehaviour: "rotate" | "reject" | "fail" | "renewedElsewhere" = "rotate"

  /** Yolu buna uyan istekler sunucu hatası (500) alır. */
  failing: RegExp | undefined

  /** `GET /auth/config` yanıtı; varsayılanı yalnızca parola girişidir. */
  config: AuthConfig = {
    passwordSignIn: true,
    provider: null,
    serverSignIn: false,
  }

  /** Sahte kimlik sağlayıcı; `offerProvider` çağrılmadıkça panel ondan habersizdir. */
  readonly idp = new IdentityProvider()

  private readonly access = new Map<string, string>()
  private readonly refresh = new Map<string, string>()
  private readonly versions = new Map<string, number>()
  private readonly idempotent = new Map<string, OrderResponse>()
  private counter = 0
  private server: Server | undefined

  origin = ""

  /** Bir hesap ekler; varsayılanı e-postası doğrulanmış bir personeldir. */
  addAccount(
    email: string,
    password: string,
    overrides: Partial<AccountInfo> = {}
  ): Account {
    const account: Account = {
      id: `account-${this.accounts.length + 1}`,
      email,
      tenantId: `0000000${this.accounts.length + 1}-aaaa-bbbb-cccc-dddddddddddd`,
      emailVerified: true,
      isStaff: true,
      ...overrides,
      password,
    }

    this.accounts.push(account)
    return account
  }

  addProducts(...items: Array<Partial<ProductResponse> & { name: string }>) {
    for (const item of items) {
      this.products.push({
        id: `product-${++this.counter}`,
        price: 10,
        description: null,
        categoryId: null,
        metadata: {},
        ...item,
      })
    }
  }

  addOrder(
    customerName: string,
    status: OrderStatus,
    lines: Array<[name: string, quantity: number, unitPrice: number]> = []
  ): OrderResponse {
    const id = `order-${++this.counter}`
    const order: OrderResponse = {
      id,
      customerName,
      status,
      number: status === "Draft" ? null : `ORD-2026-${this.counter}`,
      total: lines.reduce(
        (sum, [, quantity, price]) => sum + quantity * price,
        0
      ),
      itemCount: lines.length,
      placementNote: null,
      cancellationReason: null,
      cartId: null,
      createdAt: "2026-11-20T09:30:00Z",
      updatedAt: null,
      orderItems: lines.map(([productName, quantity, unitPrice], index) => ({
        id: `${id}-line-${index + 1}`,
        productName,
        quantity,
        unitPrice,
        orderId: id,
      })),
    }

    this.orders.push(order)
    this.versions.set(id, 1)
    return order
  }

  /**
   * Backend'i kimlik sağlayıcıyla girişe açar. Sağlayıcıda giriş yapan kullanıcı verilen hesaptır; hesap verilmezse
   * sağlayıcının tanıttığı kullanıcıyı mağaza tanımaz ve token'ını reddeder.
   */
  offerProvider(account?: AccountInfo) {
    this.config = {
      ...this.config,
      provider: {
        authority: this.idp.authority,
        clientId: CLIENT_ID,
        scopes: SCOPES,
      },
    }
    this.idp.subject = account?.id ?? "nobody"
  }

  /**
   * Verilmiş bütün erişim token'larını (sağlayıcınınkiler dahil) geçersiz kılar: sonraki istek 401 alır ve panel
   * oturumu yeniler.
   */
  expireAccessTokens() {
    this.access.clear()
    this.idp.expireAccessTokens()
  }

  /** Bütün oturumları kapatır: yenileme de reddedilir. */
  revokeSessions() {
    this.expireAccessTokens()
    this.refresh.clear()
    this.idp.revokeRefreshTokens()
  }

  /** Yola ve metoda uyan istekler, geliş sırasıyla. */
  sent(method: string, route: string | RegExp): Recorded[] {
    return this.requests.filter(
      (request) =>
        request.method === method &&
        (typeof route === "string"
          ? request.path === route
          : route.test(request.path))
    )
  }

  last(method: string, route: string | RegExp): Recorded {
    const found = this.sent(method, route).at(-1)

    if (!found) {
      throw new Error(`No ${method} ${String(route)} request was sent.`)
    }

    return found
  }

  async start(): Promise<void> {
    this.server = createServer((request, response) => {
      void this.serve(request, response)
    })

    await new Promise<void>((resolve) =>
      this.server?.listen(0, "127.0.0.1", resolve)
    )

    const { port } = this.server.address() as AddressInfo
    this.origin = `http://127.0.0.1:${port}`
    this.idp.origin = this.origin
  }

  async stop(): Promise<void> {
    this.server?.closeAllConnections()
    await new Promise<void>((resolve) => this.server?.close(() => resolve()))
  }

  private async serve(request: IncomingMessage, response: ServerResponse) {
    const url = new URL(request.url ?? "/", "http://localhost")

    const provider = url.pathname.startsWith(`${PROVIDER_PATH}/`)

    if (!provider && !url.pathname.startsWith("/api/")) {
      await this.file(url.pathname, response)
      return
    }

    const chunks: Buffer[] = []
    for await (const chunk of request) {
      chunks.push(chunk as Buffer)
    }

    const raw = Buffer.concat(chunks).toString("utf8")

    if (provider) {
      const reply = this.idp.handle({
        method: request.method ?? "GET",
        path: url.pathname.slice(PROVIDER_PATH.length),
        query: url.searchParams,
        headers: request.headers,
        form: new URLSearchParams(raw),
      })

      // OAuth yanıtları, hatalar dahil, düz JSON'dur; token'lar önbelleğe alınmaz.
      response.writeHead(reply.status, {
        "content-type": "application/json",
        "cache-control": "no-store",
        ...reply.headers,
      })
      response.end(reply.body === undefined ? "" : JSON.stringify(reply.body))
      return
    }

    const call: Call = {
      method: request.method ?? "GET",
      path: url.pathname.slice(4),
      query: url.searchParams,
      headers: request.headers,
      body: raw ? JSON.parse(raw) : undefined,
      language: request.headers["accept-language"]?.startsWith("tr")
        ? "tr"
        : "en",
      account: undefined,
    }

    this.requests.push(call)

    let reply: Reply
    try {
      reply = this.handle(call)
    } catch (error) {
      if (!(error instanceof Rejection)) {
        throw error
      }
      reply = error.reply
    }

    response.writeHead(reply.status, {
      "content-type":
        reply.status >= 400 ? "application/problem+json" : "application/json",
      ...reply.headers,
    })
    response.end(reply.body === undefined ? "" : JSON.stringify(reply.body))
  }

  private async file(pathname: string, response: ServerResponse) {
    const requested = path.join(dist, pathname)
    // Dosya olmayan her yol uygulamanın kendisidir (istemci tarafı yönlendirme).
    const file =
      path.extname(pathname) && requested.startsWith(dist)
        ? requested
        : path.join(dist, "index.html")

    try {
      const content = await readFile(file)
      response.writeHead(200, {
        "content-type": types[path.extname(file)] ?? "application/octet-stream",
      })
      response.end(content)
    } catch {
      response.writeHead(404).end()
    }
  }

  private say(call: Call, key: MessageKey): string {
    return messages[call.language][key]
  }

  /** Hesap için geçerli token'lar üretir (giriş yanıtındaki gibi). */
  issue(account: Account): AccessTokenResponse {
    const serial = ++this.counter
    const tokens: AccessTokenResponse = {
      tokenType: "Bearer",
      accessToken: `access-${serial}`,
      expiresIn: 3600,
      refreshToken: `refresh-${serial}`,
    }

    this.access.set(tokens.accessToken, account.id)
    this.refresh.set(tokens.refreshToken, account.id)
    return tokens
  }

  private authenticate(call: Call): Account {
    const token = (call.headers.authorization ?? "").replace(/^Bearer /, "")
    // Backend kendi token'ını da sağlayıcının token'ını da kabul eder; ikincisinde hesap, sağlayıcının tanıttığı
    // kullanıcıdan bulunur.
    const id = this.access.get(token) ?? this.idp.subjectOf(token)
    const account = this.accounts.find((candidate) => candidate.id === id)

    if (!account) {
      throw new Rejection({
        status: 401,
        body: { status: 401, title: "Unauthorized" } satisfies ProblemDetails,
        headers: { "www-authenticate": 'Bearer error="invalid_token"' },
      })
    }

    return account
  }

  /** Ortak kaynakları yalnızca e-postası doğrulanmış personel yazar (örnek mağazanın kuralı). */
  private staff(call: Call, account: Account) {
    if (!account.isStaff || !account.emailVerified) {
      throw new Rejection(problem(403, this.say(call, "staffOnly")))
    }
  }

  private handle(call: Call): Reply {
    const { method, path: route } = call
    const body = record(call.body)

    if (this.failing?.test(route)) {
      return problem(500, "Internal server error")
    }

    if (route.startsWith("/auth/")) {
      return this.auth(call, body)
    }

    const account = this.authenticate(call)
    const [, resource, id, action] = route.split("/")

    if (resource === "products") {
      return this.product(call, account, method, id, body)
    }

    if (resource === "categories") {
      return this.category(call, account, method, id, body)
    }

    if (resource === "orders") {
      return this.order(call, method, id, action, body)
    }

    return problem(404, this.say(call, "notFound"))
  }

  private auth(call: Call, body: Record<string, unknown>): Reply {
    const email = text(body.email).toLowerCase()
    const known = this.accounts.find((account) => account.email === email)

    switch (`${call.method} ${call.path}`) {
      case "GET /auth/config":
        return { status: 200, body: this.config }

      case "POST /auth/login":
        return known && known.password === body.password
          ? { status: 200, body: this.issue(known) }
          : problem(401, this.say(call, "invalidCredentials"))

      case "POST /auth/register": {
        if (text(body.password).length < 8) {
          return invalid({ password: [this.say(call, "passwordLength")] })
        }

        if (known) {
          return problem(409, this.say(call, "emailTaken"))
        }

        const account = this.addAccount(email, text(body.password), {
          emailVerified: false,
          isStaff: false,
        })
        return { status: 200, body: this.issue(account) }
      }

      case "POST /auth/refresh": {
        if (this.refreshBehaviour === "fail") {
          return problem(503, "Service unavailable")
        }

        const token = text(body.refreshToken)
        const id = this.refresh.get(token)
        const account = this.accounts.find((candidate) => candidate.id === id)

        if (!account || this.refreshBehaviour === "reject") {
          return problem(401, "Session expired")
        }

        // Oturumu az önce başka bir istek yeniledi: token verilmez ama oturum da kapanmaz.
        if (this.refreshBehaviour === "renewedElsewhere") {
          return problem(409, "The session was renewed elsewhere")
        }

        // Yenileme token'ı bir kez kullanılır.
        this.refresh.delete(token)
        return { status: 200, body: this.issue(account) }
      }

      case "GET /auth/me": {
        const account = this.authenticate(call)
        const info: AccountInfo = {
          id: account.id,
          email: account.email,
          tenantId: account.tenantId,
          emailVerified: account.emailVerified,
          isStaff: account.isStaff,
        }
        return { status: 200, body: info }
      }

      case "POST /auth/logout": {
        // Oturumu erişim token'ı da yenileme token'ı da tanıtabilir; yanıt her durumda aynıdır.
        const bearer = (call.headers.authorization ?? "").replace(
          /^Bearer /,
          ""
        )

        if (
          this.access.has(bearer) ||
          this.refresh.has(text(body.refreshToken))
        ) {
          this.revokeSessions()
        }

        return { status: 204 }
      }

      case "POST /auth/password/forgot":
        return { status: 202 }

      case "POST /auth/password/reset": {
        if (text(body.newPassword).length < 8) {
          return invalid({ newPassword: [this.say(call, "passwordLength")] })
        }

        if (!known || this.normalize(text(body.code)) !== this.code) {
          return problem(400, this.say(call, "invalidCode"))
        }

        known.password = text(body.newPassword)
        return { status: 204 }
      }

      case "POST /auth/password/change": {
        const account = this.authenticate(call)

        if (account.password !== body.currentPassword) {
          return problem(400, this.say(call, "invalidCredentials"))
        }

        if (text(body.newPassword).length < 8) {
          return invalid({ newPassword: [this.say(call, "passwordLength")] })
        }

        account.password = text(body.newPassword)

        // Bütün oturumlar kapanır; isteği yapana yeni bir oturum açılır.
        this.revokeSessions()
        return { status: 200, body: this.issue(account) }
      }

      case "POST /auth/email/verify": {
        const account = this.authenticate(call)

        if (this.normalize(text(body.code)) !== this.code) {
          return problem(400, this.say(call, "invalidCode"))
        }

        account.emailVerified = true
        return { status: 200, body: this.issue(account) }
      }

      case "POST /auth/email/resend":
        this.authenticate(call)
        return { status: 202 }

      default:
        return problem(404, this.say(call, "notFound"))
    }
  }

  private normalize(code: string): string {
    return code.replace(/[\s-]/g, "").toUpperCase()
  }

  private product(
    call: Call,
    account: Account,
    method: string,
    id: string | undefined,
    body: Record<string, unknown>
  ): Reply {
    if (method === "GET" && !id) {
      const search = (call.query.get("search") ?? "").toLowerCase()
      const sort = call.query.get("sort") ?? "name"
      const field = sort.replace(/^-/, "") === "price" ? "price" : "name"
      const direction = sort.startsWith("-") ? -1 : 1

      const matching = this.products
        .filter((product) => product.name.toLowerCase().includes(search))
        .sort((left, right) =>
          field === "price"
            ? (left.price - right.price) * direction
            : left.name.localeCompare(right.name) * direction
        )

      return { status: 200, body: this.paged(call, matching) }
    }

    this.staff(call, account)
    const index = this.products.findIndex((product) => product.id === id)

    if (method === "DELETE") {
      if (index < 0) {
        return problem(404, this.say(call, "notFound"))
      }

      this.products.splice(index, 1)
      return { status: 204 }
    }

    const errors: Record<string, string[]> = {}

    if (text(body.name) === "") {
      errors.name = [this.say(call, "nameRequired")]
    }

    if (typeof body.price !== "number" || body.price < 0) {
      errors.price = [this.say(call, "priceRange")]
    }

    if (Object.keys(errors).length > 0) {
      return invalid(errors)
    }

    const values = {
      name: text(body.name),
      price: body.price as number,
      description:
        typeof body.description === "string" ? body.description : null,
      categoryId: typeof body.categoryId === "number" ? body.categoryId : null,
      // PUT çantayı tümüyle değiştirir; gönderilmeyen çanta boşalır.
      metadata: record(body.metadata) as ProductResponse["metadata"],
    }

    if (method === "POST" && !id) {
      const created: ProductResponse = {
        id: `product-${++this.counter}`,
        ...values,
      }
      this.products.push(created)
      return { status: 201, body: created }
    }

    if (method === "PUT" && index >= 0) {
      this.products[index] = { ...this.products[index], ...values }
      return { status: 200, body: this.products[index] }
    }

    return problem(404, this.say(call, "notFound"))
  }

  private category(
    call: Call,
    account: Account,
    method: string,
    id: string | undefined,
    body: Record<string, unknown>
  ): Reply {
    if (method === "GET" && !id) {
      return { status: 200, body: this.paged(call, this.categories) }
    }

    this.staff(call, account)

    if (method === "POST" && !id) {
      if (text(body.name) === "") {
        return invalid({ name: [this.say(call, "nameRequired")] })
      }

      const created: CategoryResponse = {
        id: this.categories.length + 1,
        name: text(body.name),
        parentId: typeof body.parentId === "number" ? body.parentId : null,
        position: this.categories.length,
      }
      this.categories.push(created)
      return { status: 201, body: created }
    }

    if (method === "DELETE") {
      const key = Number(id)

      if (this.products.some((product) => product.categoryId === key)) {
        return problem(409, this.say(call, "inUse"))
      }

      this.categories = this.categories.filter(
        (category) => category.id !== key
      )
      return { status: 204 }
    }

    return problem(404, this.say(call, "notFound"))
  }

  private order(
    call: Call,
    method: string,
    id: string | undefined,
    action: string | undefined,
    body: Record<string, unknown>
  ): Reply {
    if (method === "GET" && !id) {
      const status = call.query.get("status")
      return {
        status: 200,
        body: this.paged(
          call,
          this.orders.filter((order) => !status || order.status === status)
        ),
      }
    }

    if (method === "POST" && !id) {
      const key = text(call.headers["idempotency-key"])
      const repeated = this.idempotent.get(key)

      if (repeated) {
        return { status: 201, body: repeated }
      }

      if (text(body.customerName) === "") {
        return invalid({ customerName: [this.say(call, "customerRequired")] })
      }

      const lines = Array.isArray(body.orderItems) ? body.orderItems : []
      const created = this.addOrder(
        text(body.customerName),
        "Draft",
        lines.map((line) => {
          const item = record(line)
          return [
            text(item.productName),
            Number(item.quantity),
            Number(item.unitPrice),
          ]
        })
      )

      this.idempotent.set(key, created)
      return { status: 201, body: created, headers: this.etag(created.id) }
    }

    const order = this.orders.find((candidate) => candidate.id === id)

    if (!order) {
      return problem(404, this.say(call, "notFound"))
    }

    if (method === "GET") {
      return { status: 200, body: order, headers: this.etag(order.id) }
    }

    // Sipariş sürüm kontrollüdür: yazan her istek okuduğu sürümü geri gönderir.
    const expected = this.etag(order.id).etag

    if (!call.headers["if-match"]) {
      return problem(428, "Precondition required")
    }

    if (call.headers["if-match"] !== expected) {
      return problem(412, "Precondition failed")
    }

    const move = (from: OrderStatus[], to: OrderStatus): Reply => {
      if (!from.includes(order.status)) {
        return problem(409, this.say(call, "wrongState"))
      }

      order.status = to
      order.number ??= `ORD-2026-${++this.counter}`
      this.versions.set(order.id, (this.versions.get(order.id) ?? 1) + 1)
      return { status: 200, body: order, headers: this.etag(order.id) }
    }

    if (method === "DELETE") {
      this.orders = this.orders.filter((candidate) => candidate !== order)
      return { status: 204 }
    }

    switch (action) {
      case "place":
        order.placementNote = typeof body.note === "string" ? body.note : null
        return move(["Draft"], "Placed")
      case "ship":
        return move(["Placed"], "Shipped")
      case "reopen":
        order.cancellationReason = null
        return move(["Cancelled"], "Draft")
      case "cancel":
        if (text(body.reason) === "") {
          return invalid({ reason: [this.say(call, "reasonRequired")] })
        }

        order.cancellationReason = text(body.reason)
        return move(["Draft", "Placed"], "Cancelled")
      default:
        return problem(404, this.say(call, "notFound"))
    }
  }

  /** Bir siparişi panelin haberi olmadan değiştirir (başka bir kullanıcı gibi): sürümü ilerler. */
  touch(id: string) {
    this.versions.set(id, (this.versions.get(id) ?? 1) + 1)
  }

  private etag(id: string): { etag: string } {
    return { etag: `"${this.versions.get(id) ?? 1}"` }
  }

  private paged<T>(call: Call, items: T[]) {
    const page = Number(call.query.get("page") ?? 1)
    const pageSize = Number(call.query.get("pageSize") ?? 20)

    return {
      items: items.slice((page - 1) * pageSize, page * pageSize),
      page,
      pageSize,
      totalCount: items.length,
    }
  }
}
