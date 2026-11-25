import * as AuthSession from 'expo-auth-session';

import type { ExternalProviderConfig } from '@/api/model';
import type { ProviderLink } from '@/lib/session';

/**
 * Kimlik sağlayıcıyla (OpenID Connect) konuşan kısım: yetkilendirme kodu + PKCE akışının hazırlanması, kodun token'a
 * çevrilmesi ve token'ın yenilenmesi. Uygulama gizli anahtar taşımaz (public client). Backend'e ve oturumun
 * saklanmasına dokunmaz; onları `lib/auth.ts` ve `lib/api-fetch.ts` bağlar.
 */

/** Hazırlanmış bir giriş isteği: sağlayıcının uçları ve durum + PKCE değerleri üretilmiş yetkilendirme isteği. */
export type ProviderPrompt = {
  request: AuthSession.AuthRequest;
  authorizationEndpoint: string;
  tokenEndpoint: string;
};

export type ProviderTokens = {
  accessToken: string;
  refreshToken: string | null;
};

/** Girişin sağlayıcı tarafında başarısız olması (keşif, yetkilendirme yanıtı veya kodun token'a çevrilmesi). */
export class ProviderSignInError extends Error {
  constructor(reason: string, options?: ErrorOptions) {
    super(`The identity provider sign-in failed: ${reason}`, options);
    this.name = 'ProviderSignInError';
  }
}

/** Sağlayıcının yenileme token'ını kesin olarak reddetmesi; oturum bununla biter. */
export class ProviderRejectionError extends Error {
  constructor(code: string) {
    super(`The identity provider rejected the refresh token: ${code}`);
    this.name = 'ProviderRejectionError';
  }
}

// Token ucunun kesin ret kodları (RFC 6749 §5.2): istek aynı haliyle bir daha kabul edilmez. Bunların dışındaki her
// şey (ağ hatası, 5xx, JSON olmayan gövde, `server_error` / `temporarily_unavailable` gibi kodlar) geçici sayılır.
const REJECTIONS = new Set([
  'invalid_request',
  'invalid_client',
  'invalid_grant',
  'unauthorized_client',
  'unsupported_grant_type',
  'invalid_scope',
]);

/**
 * Sağlayıcının girişten sonra döneceği adres; sağlayıcıda kayıtlı olmalıdır. Yolu giriş ekranınınkidir: Android'de
 * dönüş bir derin bağlantıdır ve Expo Router onu da açar, var olmayan bir yol "bulunamadı" ekranına düşerdi.
 *
 * - Derleme (development build, mağaza derlemesi): `haldus-shop://sign-in` (şema `app.json` içindedir).
 * - Expo Go: `exp://<adres>:8081/--/sign-in`.
 * - Web: `<uygulamanın kaynağı>/sign-in`; sağlayıcı açılır pencerede açılır ve pencere bu adrese döner.
 */
export function providerRedirectUri(): string {
  return AuthSession.makeRedirectUri({ native: 'haldus-shop://sign-in', path: 'sign-in' });
}

/** Adres HTTPS mi, ya da yalnızca cihazın kendisine (veya emülatörün bilgisayarına) giden bir geliştirme adresi mi? */
function isTrustworthy(address: string): boolean {
  const match = /^(https?):\/\/(\[[^\]]+\]|[^/:?#]+)/i.exec(address);

  if (!match) {
    return false;
  }

  return (
    match[1].toLowerCase() === 'https' ||
    ['localhost', '127.0.0.1', '10.0.2.2', '[::1]'].includes(match[2].toLowerCase())
  );
}

function scopesOf(provider: ExternalProviderConfig): string[] {
  return provider.scopes.split(/\s+/).filter((scope) => scope.length > 0);
}

/**
 * Girişi hazırlar: sağlayıcının uçlarını keşfeder (`/.well-known/openid-configuration`) ve yetkilendirme adresini
 * kurar. Düğmeye basılmadan önce yapılır, çünkü tarayıcılar açılır pencereye yalnızca dokunuşun hemen ardından izin
 * verir; o sırada ağ beklenirse pencere engellenir.
 */
export async function prepareProviderSignIn(
  provider: ExternalProviderConfig
): Promise<ProviderPrompt> {
  // Adres sonundaki eğik çizgiyle de yazılabilir; keşif adresi çift eğik çizgi taşımamalıdır.
  const issuer = provider.authority.replace(/\/+$/, '');

  // Sağlayıcının adresi backend'den gelir ve kullanıcı parolasını oradaki sayfaya yazar. Şifresiz bir bağlantıda
  // araya giren biri sayfayı da token ucunu da değiştirebilir; düz HTTP yalnızca geliştirme adreslerinde kabul edilir.
  if (!isTrustworthy(issuer)) {
    throw new ProviderSignInError('the provider address is not https');
  }

  let discovery: AuthSession.DiscoveryDocument;
  try {
    discovery = await AuthSession.fetchDiscoveryAsync(issuer);
  } catch (error) {
    throw new ProviderSignInError('the discovery document could not be read', { cause: error });
  }

  const { authorizationEndpoint, tokenEndpoint } = discovery;

  // Keşif belgesi, istendiği adresin belgesi olmalıdır (OpenID Connect Discovery 4.3).
  const declared: unknown = discovery.discoveryDocument?.issuer;
  if (typeof declared !== 'string' || declared.replace(/\/+$/, '') !== issuer) {
    throw new ProviderSignInError('the discovery document belongs to another issuer');
  }

  // Keşif isteği yanıtın durumuna bakmaz; bir hata sayfasının JSON gövdesi de buraya kadar gelir.
  if (!authorizationEndpoint || !tokenEndpoint) {
    throw new ProviderSignInError('the discovery document names no endpoints');
  }

  // Yanıt tipi (code) ve PKCE (S256) kütüphanenin varsayılanlarıdır.
  const request = await AuthSession.loadAsync(
    {
      clientId: provider.clientId,
      scopes: scopesOf(provider),
      redirectUri: providerRedirectUri(),
    },
    discovery
  );

  return { request, authorizationEndpoint, tokenEndpoint };
}

/** Sistem tarayıcısını (web'de açılır pencereyi) açar; kullanıcı dönene veya vazgeçene kadar bekler. */
export function promptProviderSignIn(
  prompt: ProviderPrompt
): Promise<AuthSession.AuthSessionResult> {
  return prompt.request.promptAsync({ authorizationEndpoint: prompt.authorizationEndpoint });
}

/** Yetkilendirme kodunu token'lara çevirir; sağlayıcı isteği PKCE doğrulayıcısıyla eşleştirir. */
export async function redeemProviderCode(
  provider: ExternalProviderConfig,
  prompt: ProviderPrompt,
  code: string
): Promise<ProviderTokens & { link: ProviderLink }> {
  const { codeVerifier, redirectUri } = prompt.request;

  if (!codeVerifier) {
    throw new ProviderSignInError('the request carries no PKCE verifier');
  }

  let accessToken: unknown;
  let refreshToken: unknown;
  try {
    ({ accessToken, refreshToken } = await AuthSession.exchangeCodeAsync(
      {
        clientId: provider.clientId,
        code,
        redirectUri,
        extraParams: { code_verifier: codeVerifier },
      },
      { tokenEndpoint: prompt.tokenEndpoint }
    ));
  } catch (error) {
    throw new ProviderSignInError('the code could not be exchanged', { cause: error });
  }

  // Token isteği de yanıtın durumuna bakmaz: `error` taşımayan bir hata gövdesi token'sız bir yanıt gibi görünür.
  if (typeof accessToken !== 'string' || accessToken.length === 0) {
    throw new ProviderSignInError('the token response carries no access token');
  }

  return {
    accessToken,
    refreshToken: typeof refreshToken === 'string' && refreshToken.length > 0 ? refreshToken : null,
    link: { clientId: provider.clientId, tokenEndpoint: prompt.tokenEndpoint },
  };
}

/**
 * Sağlayıcı oturumunun token'larını yeniler (yenileme token'ı ile, RFC 6749 §6). Sağlayıcı yeni bir yenileme token'ı
 * vermezse eskisi geçerli kalır. Kesin rette `ProviderRejectionError`, öteki her hatada başka bir hata fırlatır.
 */
export async function renewProviderTokens(
  link: ProviderLink,
  current: string
): Promise<{ accessToken: string; refreshToken: string }> {
  let accessToken: unknown;
  let refreshToken: unknown;
  try {
    ({ accessToken, refreshToken } = await AuthSession.refreshAsync(
      { clientId: link.clientId, refreshToken: current },
      { tokenEndpoint: link.tokenEndpoint }
    ));
  } catch (error) {
    if (error instanceof AuthSession.TokenError && REJECTIONS.has(error.code)) {
      throw new ProviderRejectionError(error.code);
    }

    throw error;
  }

  if (typeof accessToken !== 'string' || accessToken.length === 0) {
    throw new Error('The token response of the identity provider carries no access token.');
  }

  return {
    accessToken,
    refreshToken:
      typeof refreshToken === 'string' && refreshToken.length > 0 ? refreshToken : current,
  };
}
