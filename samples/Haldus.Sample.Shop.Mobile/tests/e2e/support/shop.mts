// Ekran testlerinin konuştuğu sunucu: uygulamanın web dışa aktarımını (`dist-e2e`) ve örnek mağazanın API'sinin bellekte
// duran bir taklidini aynı adresten sunar. Uygulama backend'in adresini derlenirken alır (`EXPO_PUBLIC_API_URL`);
// dışa aktarım bir yer tutucuyla üretilir ve betik sunulurken bu sunucunun adresiyle değiştirilir.
//
// Taklit backend'in yerine geçmez; ekranın bir dokunuştan sonra hangi isteği gönderdiğini ve gelen yanıtı nasıl
// gösterdiğini sınamak içindir. Yanıtların biçimi orval'ın ürettiği tiplerle yazılır: şema değişirse bu dosya
// tip denetiminden geçmez (`pnpm typecheck:tests`).
import { readFile } from 'node:fs/promises';
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import path from 'node:path';

import type {
  AccessTokenResponse,
  AccountInfo,
  BasketResponse,
  HttpValidationProblemDetails,
  ProblemDetails,
  ProductResponse,
} from '@/api/model';

export const dist = path.resolve(import.meta.dirname, '../../../dist-e2e');

/** Dışa aktarımdaki backend adresi; sunulurken gerçek adresle değiştirilir. */
export const API_PLACEHOLDER = 'http://haldus-e2e.invalid';

const types: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.ttf': 'font/ttf',
};

// Backend mesajlarını isteğin diliyle yazar; taklit de öyle yapar ki uygulamanın dili başlıkla taşıdığı görülsün.
const messages = {
  en: {
    invalidCredentials: 'The e-mail address or password is incorrect.',
    emailTaken: 'This e-mail address is already registered.',
    invalidCode: 'The code is invalid or has expired.',
    passwordLength: 'The password must be at least 8 characters.',
    notFound: 'The record was not found.',
  },
  tr: {
    invalidCredentials: 'E-posta adresi veya parola hatalı.',
    emailTaken: 'Bu e-posta adresi zaten kayıtlı.',
    invalidCode: 'Kod geçersiz veya süresi dolmuş.',
    passwordLength: 'Parola en az 8 karakter olmalıdır.',
    notFound: 'Kayıt bulunamadı.',
  },
} as const;

type MessageKey = keyof (typeof messages)['en'];

export type Recorded = {
  method: string;
  path: string;
  query: URLSearchParams;
  headers: IncomingMessage['headers'];
  body: unknown;
};

type Account = AccountInfo & { password: string };

type Reply = { status: number; body?: unknown; headers?: Record<string, string> };

type Call = Recorded & { language: 'en' | 'tr' };

class Rejection {
  readonly reply: Reply;

  constructor(reply: Reply) {
    this.reply = reply;
  }
}

function problem(status: number, detail: string): Reply {
  const body: ProblemDetails = { status, title: 'Error', detail };
  return { status, body };
}

function invalid(errors: Record<string, string[]>): Reply {
  const body: HttpValidationProblemDetails = {
    status: 400,
    title: 'One or more validation errors occurred.',
    errors,
  };
  return { status: 400, body };
}

function record(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : {};
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/** Örnek mağazanın bellekteki taklidi. Testler durumu doğrudan kurar ve gelen istekleri `requests` içinden okur. */
export class Shop {
  readonly requests: Recorded[] = [];
  readonly accounts: Account[] = [];
  products: ProductResponse[] = [];

  /** Hesap başına favori ürün kimlikleri ve sepet; ikisi de kullanıcıya aittir (`/me`). */
  readonly favorites = new Map<string, Set<string>>();
  readonly baskets = new Map<string, BasketResponse>();

  /** Gönderilen son doğrulama / sıfırlama kodu (backend'in e-postayla yolladığı). */
  code = 'K7M2P9QX';

  /** Yolu buna uyan istekler sunucu hatası (500) alır. */
  failing: RegExp | undefined;

  private readonly access = new Map<string, string>();
  private readonly refresh = new Map<string, string>();
  private counter = 0;
  private server: Server | undefined;

  origin = '';

  /** Bir hesap ekler; varsayılanı e-postası doğrulanmış bir hesaptır. */
  addAccount(email: string, password: string, overrides: Partial<AccountInfo> = {}): Account {
    const account: Account = {
      id: `account-${this.accounts.length + 1}`,
      email,
      tenantId: `0000000${this.accounts.length + 1}-aaaa-bbbb-cccc-dddddddddddd`,
      emailVerified: true,
      isStaff: false,
      ...overrides,
      password,
    };

    this.accounts.push(account);
    return account;
  }

  addProducts(...items: (Partial<ProductResponse> & { name: string })[]) {
    for (const item of items) {
      this.products.push({
        id: `product-${++this.counter}`,
        price: 10,
        description: null,
        categoryId: null,
        metadata: {},
        ...item,
      });
    }
  }

  /** Hesap için geçerli token'lar üretir (giriş yanıtındaki gibi). */
  issue(account: Account): AccessTokenResponse {
    const serial = ++this.counter;
    const tokens: AccessTokenResponse = {
      tokenType: 'Bearer',
      accessToken: `access-${serial}`,
      expiresIn: 3600,
      refreshToken: `refresh-${serial}`,
    };

    this.access.set(tokens.accessToken, account.id);
    this.refresh.set(tokens.refreshToken, account.id);
    return tokens;
  }

  /** Verilmiş bütün erişim token'larını geçersiz kılar: sonraki istek 401 alır ve uygulama oturumu yeniler. */
  expireAccessTokens() {
    this.access.clear();
  }

  /** Bütün oturumları kapatır: yenileme de reddedilir. */
  revokeSessions() {
    this.access.clear();
    this.refresh.clear();
  }

  /** Yola ve metoda uyan istekler, geliş sırasıyla. */
  sent(method: string, route: string | RegExp): Recorded[] {
    return this.requests.filter(
      (request) =>
        request.method === method &&
        (typeof route === 'string' ? request.path === route : route.test(request.path))
    );
  }

  last(method: string, route: string | RegExp): Recorded {
    const found = this.sent(method, route).at(-1);

    if (!found) {
      throw new Error(`No ${method} ${String(route)} request was sent.`);
    }

    return found;
  }

  async start(): Promise<void> {
    this.server = createServer((request, response) => {
      void this.serve(request, response);
    });

    await new Promise<void>((resolve) => this.server?.listen(0, '127.0.0.1', resolve));

    const { port } = this.server.address() as AddressInfo;
    this.origin = `http://127.0.0.1:${port}`;
  }

  async stop(): Promise<void> {
    this.server?.closeAllConnections();
    await new Promise<void>((resolve) => this.server?.close(() => resolve()));
  }

  private async serve(request: IncomingMessage, response: ServerResponse) {
    const url = new URL(request.url ?? '/', 'http://localhost');

    if (!url.pathname.startsWith('/api/')) {
      await this.file(url.pathname, response);
      return;
    }

    const chunks: Buffer[] = [];
    for await (const chunk of request) {
      chunks.push(chunk as Buffer);
    }

    const raw = Buffer.concat(chunks).toString('utf8');
    const call: Call = {
      method: request.method ?? 'GET',
      path: url.pathname.slice(4),
      query: url.searchParams,
      headers: request.headers,
      body: raw ? JSON.parse(raw) : undefined,
      language: request.headers['accept-language']?.startsWith('tr') ? 'tr' : 'en',
    };

    this.requests.push(call);

    let reply: Reply;
    try {
      reply = this.handle(call);
    } catch (error) {
      if (!(error instanceof Rejection)) {
        throw error;
      }
      reply = error.reply;
    }

    response.writeHead(reply.status, {
      'content-type': reply.status >= 400 ? 'application/problem+json' : 'application/json',
      ...reply.headers,
    });
    response.end(reply.body === undefined ? '' : JSON.stringify(reply.body));
  }

  private async file(pathname: string, response: ServerResponse) {
    const requested = path.join(dist, pathname);
    // Dosya olmayan her yol uygulamanın kendisidir (istemci tarafı yönlendirme).
    const file =
      path.extname(pathname) && requested.startsWith(dist)
        ? requested
        : path.join(dist, 'index.html');

    try {
      const extension = path.extname(file);
      const content = await readFile(file);

      response.writeHead(200, {
        'content-type': types[extension] ?? 'application/octet-stream',
      });
      response.end(
        extension === '.js'
          ? content.toString('utf8').replaceAll(API_PLACEHOLDER, `${this.origin}/api`)
          : content
      );
    } catch {
      response.writeHead(404).end();
    }
  }

  private say(call: Call, key: MessageKey): string {
    return messages[call.language][key];
  }

  private authenticate(call: Call): Account {
    const header = call.headers.authorization ?? '';
    const id = this.access.get(header.replace(/^Bearer /, ''));
    const account = this.accounts.find((candidate) => candidate.id === id);

    if (!account) {
      throw new Rejection({
        status: 401,
        body: { status: 401, title: 'Unauthorized' } satisfies ProblemDetails,
        headers: { 'www-authenticate': 'Bearer error="invalid_token"' },
      });
    }

    return account;
  }

  private handle(call: Call): Reply {
    const body = record(call.body);

    if (this.failing?.test(call.path)) {
      return problem(500, 'Internal server error');
    }

    if (call.path.startsWith('/auth/')) {
      return this.auth(call, body);
    }

    const account = this.authenticate(call);

    if (call.path === '/products' && call.method === 'GET') {
      const search = (call.query.get('search') ?? '').toLowerCase();
      return {
        status: 200,
        body: this.paged(
          call,
          this.products
            .filter((product) => product.name.toLowerCase().includes(search))
            .sort((left, right) => left.name.localeCompare(right.name))
        ),
      };
    }

    if (call.path.startsWith('/me/favorites')) {
      return this.favorite(call, account);
    }

    if (call.path.startsWith('/me/baskets')) {
      return this.basket(call, account, body);
    }

    return problem(404, this.say(call, 'notFound'));
  }

  private favorite(call: Call, account: Account): Reply {
    const mine = this.favorites.get(account.id) ?? new Set<string>();
    this.favorites.set(account.id, mine);
    const productId = call.path.split('/')[3];

    if (!productId && call.method === 'GET') {
      // Liste bağları değil, bağlı ürünlerin kendisini döndürür.
      return {
        status: 200,
        body: this.paged(
          call,
          this.products.filter((product) => mine.has(product.id))
        ),
      };
    }

    if (!this.products.some((product) => product.id === productId)) {
      return problem(404, this.say(call, 'notFound'));
    }

    if (call.method === 'PUT') {
      mine.add(productId);
      return { status: 204 };
    }

    if (call.method === 'DELETE') {
      mine.delete(productId);
      return { status: 204 };
    }

    return problem(404, this.say(call, 'notFound'));
  }

  private basket(call: Call, account: Account, body: Record<string, unknown>): Reply {
    const existing = this.baskets.get(account.id);
    const id = call.path.split('/')[3];

    if (!id && call.method === 'GET') {
      return { status: 200, body: this.paged(call, existing ? [existing] : []) };
    }

    const creating = !id && call.method === 'POST';
    const updating = existing && id === existing.id && call.method === 'PUT';

    if (!creating && !updating) {
      return problem(404, this.say(call, 'notFound'));
    }

    const basketId = existing?.id ?? `basket-${++this.counter}`;
    const lines = Array.isArray(body.basketItems) ? body.basketItems.map(record) : [];

    // Satırlar isteğe eşitlenir: kimliği verilen kalır, verilmeyen eklenir, istekte olmayan silinir.
    const basket: BasketResponse = {
      id: basketId,
      note: typeof body.note === 'string' ? body.note : null,
      expiresAt: '2026-12-01T10:00:00Z',
      basketItems: lines.map((line) => ({
        id: text(line.id) || `line-${++this.counter}`,
        productName: text(line.productName),
        quantity: typeof line.quantity === 'number' ? line.quantity : 1,
        basketId,
      })),
    };

    this.baskets.set(account.id, basket);
    return { status: creating ? 201 : 200, body: basket };
  }

  private auth(call: Call, body: Record<string, unknown>): Reply {
    const email = text(body.email).toLowerCase();
    const known = this.accounts.find((account) => account.email === email);

    switch (`${call.method} ${call.path}`) {
      case 'POST /auth/login':
        return known && known.password === body.password
          ? { status: 200, body: this.issue(known) }
          : problem(401, this.say(call, 'invalidCredentials'));

      case 'POST /auth/register': {
        if (text(body.password).length < 8) {
          return invalid({ password: [this.say(call, 'passwordLength')] });
        }

        if (known) {
          return problem(409, this.say(call, 'emailTaken'));
        }

        const account = this.addAccount(email, text(body.password), { emailVerified: false });
        return { status: 200, body: this.issue(account) };
      }

      case 'POST /auth/refresh': {
        const token = text(body.refreshToken);
        const id = this.refresh.get(token);
        const account = this.accounts.find((candidate) => candidate.id === id);

        if (!account) {
          return problem(401, 'Session expired');
        }

        // Yenileme token'ı bir kez kullanılır.
        this.refresh.delete(token);
        return { status: 200, body: this.issue(account) };
      }

      case 'GET /auth/me': {
        const account = this.authenticate(call);
        const info: AccountInfo = {
          id: account.id,
          email: account.email,
          tenantId: account.tenantId,
          emailVerified: account.emailVerified,
          isStaff: account.isStaff,
        };
        return { status: 200, body: info };
      }

      case 'POST /auth/logout':
        this.authenticate(call);
        this.revokeSessions();
        return { status: 204 };

      case 'POST /auth/password/forgot':
        return { status: 202 };

      case 'POST /auth/password/reset': {
        if (text(body.newPassword).length < 8) {
          return invalid({ newPassword: [this.say(call, 'passwordLength')] });
        }

        if (!known || this.normalize(text(body.code)) !== this.code) {
          return problem(400, this.say(call, 'invalidCode'));
        }

        known.password = text(body.newPassword);
        return { status: 204 };
      }

      case 'POST /auth/password/change': {
        const account = this.authenticate(call);

        if (account.password !== body.currentPassword) {
          return problem(400, this.say(call, 'invalidCredentials'));
        }

        if (text(body.newPassword).length < 8) {
          return invalid({ newPassword: [this.say(call, 'passwordLength')] });
        }

        account.password = text(body.newPassword);
        return { status: 204 };
      }

      case 'POST /auth/email/verify': {
        const account = this.authenticate(call);

        if (this.normalize(text(body.code)) !== this.code) {
          return problem(400, this.say(call, 'invalidCode'));
        }

        account.emailVerified = true;
        return { status: 200, body: this.issue(account) };
      }

      case 'POST /auth/email/resend':
        this.authenticate(call);
        return { status: 202 };

      default:
        return problem(404, this.say(call, 'notFound'));
    }
  }

  private normalize(code: string): string {
    return code.replace(/[\s-]/g, '').toUpperCase();
  }

  private paged<T>(call: Call, items: T[]) {
    const page = Number(call.query.get('page') ?? 1);
    const pageSize = Number(call.query.get('pageSize') ?? 20);

    return {
      items: items.slice((page - 1) * pageSize, page * pageSize),
      page,
      pageSize,
      totalCount: items.length,
    };
  }
}
