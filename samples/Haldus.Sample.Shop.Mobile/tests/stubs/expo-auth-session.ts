// `expo-auth-session` yerine yüklenir (register.mjs); gerçeği React Native'i ve Expo'nun yerel modüllerini ister.
//
// Sağlayıcıyla konuşan kısım kütüphanenin yaptığını yapar (build/Discovery.js, build/TokenRequest.js, build/Fetch.js)
// ve istekleri `fetch` taklidinden geçirir: token isteği form olarak gider, yanıtın durumuna bakılmaz, gövde her
// zaman JSON olarak okunur ve `error` taşıyorsa `TokenError` fırlatılır. Sınanan kodun kesin ret ile geçici arızayı
// ayırması bu davranışa dayanır; kütüphane değişirse bu dosya da değişmelidir.
//
// Tarayıcıyı açan kısım (`promptAsync`) taklittir: testin verdiği sonucu döndürür.

type Result =
  | { type: 'cancel' | 'dismiss' | 'locked' }
  | { type: 'success' | 'error'; params: Record<string, string>; error?: { code: string } | null };

type Discovery = { authorizationEndpoint?: string; tokenEndpoint?: string };

type RequestConfig = { clientId: string; redirectUri: string; scopes?: string[] };

export class TokenError extends Error {
  readonly code: string;

  constructor(response: { error: string; error_description?: string }) {
    super(response.error_description ?? response.error);
    this.code = response.error;
  }
}

export class AuthRequest {
  static created = 0;

  readonly clientId: string;
  readonly redirectUri: string;
  readonly scopes: string[] | undefined;
  readonly state: string;
  codeVerifier: string | undefined;
  url: string | null = null;

  constructor(config: RequestConfig) {
    const serial = ++AuthRequest.created;

    this.clientId = config.clientId;
    this.redirectUri = config.redirectUri;
    this.scopes = config.scopes;
    this.state = `state-${serial}`;
    this.codeVerifier = `verifier-${serial}`;
  }

  async promptAsync(discovery: Discovery): Promise<Result> {
    prompts.push({ request: this, discovery });
    return answer(this);
  }
}

/** Açılan giriş sayfaları: hangi istekle ve hangi uçla. */
export const prompts: { request: AuthRequest; discovery: Discovery }[] = [];

/** `makeRedirectUri` çağrılarının seçenekleri. */
export const redirects: Record<string, unknown>[] = [];

let answer: (request: AuthRequest) => Result | Promise<Result> = () => ({ type: 'cancel' });

/** Sonraki giriş sayfalarının nasıl kapanacağını belirler ve kayıtları temizler. */
export function answerPrompt(next: Result | ((request: AuthRequest) => Result | Promise<Result>)) {
  prompts.length = 0;
  redirects.length = 0;
  answer = typeof next === 'function' ? next : () => next;
}

// Derlenmiş uygulamadaki gibi davranır: `native` verildiyse odur.
export function makeRedirectUri(options: { native?: string; path?: string } = {}): string {
  redirects.push(options);
  return options.native ?? `exp://127.0.0.1:8081/--/${options.path ?? ''}`;
}

export async function fetchDiscoveryAsync(issuer: string): Promise<Discovery> {
  const response = await fetch(`${issuer}/.well-known/openid-configuration`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });
  const document = (await response.json()) as Record<string, string | undefined>;

  return {
    authorizationEndpoint: document.authorization_endpoint,
    tokenEndpoint: document.token_endpoint,
  };
}

export async function loadAsync(config: RequestConfig, discovery: Discovery): Promise<AuthRequest> {
  const request = new AuthRequest(config);

  request.url = `${discovery.authorizationEndpoint}?state=${request.state}`;
  return request;
}

async function requestTokens(discovery: Discovery, body: Record<string, string>) {
  const response = await fetch(String(discovery.tokenEndpoint), {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
    body: new URLSearchParams(body).toString(),
  });
  const tokens = (await response.json()) as Record<string, string | undefined>;

  // Kütüphanedeki gibi: nesne olmayan bir JSON gövdesinde (`null`, metin) bu satırın kendisi hata fırlatır.
  if ('error' in tokens) {
    throw new TokenError({
      error: String(tokens.error),
      error_description: tokens.error_description,
    });
  }

  return { accessToken: tokens.access_token, refreshToken: tokens.refresh_token };
}

export function exchangeCodeAsync(
  config: {
    clientId: string;
    code: string;
    redirectUri: string;
    extraParams?: Record<string, string>;
  },
  discovery: Discovery
) {
  return requestTokens(discovery, {
    grant_type: 'authorization_code',
    client_id: config.clientId,
    ...config.extraParams,
    redirect_uri: config.redirectUri,
    code: config.code,
  });
}

export function refreshAsync(
  config: { clientId: string; refreshToken?: string },
  discovery: Discovery
) {
  return requestTokens(discovery, {
    grant_type: 'refresh_token',
    client_id: config.clientId,
    refresh_token: String(config.refreshToken),
  });
}
