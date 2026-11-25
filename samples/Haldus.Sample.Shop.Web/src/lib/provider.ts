import {
  ErrorResponse,
  InMemoryWebStorage,
  User,
  UserManager,
  WebStorageStateStore,
} from "oidc-client-ts"
import type { IdTokenClaims } from "oidc-client-ts"

import { RenewalRejected } from "@/lib/api-fetch"
import type { Tokens } from "@/lib/api-fetch"
import type { ProviderSession } from "@/lib/session"

/**
 * Kimlik sağlayıcıyla giriş (OpenID Connect, Authorization Code + PKCE). Protokolün kendisi `oidc-client-ts`'in
 * işidir; bu dosya onu panelin oturumuna bağlar.
 *
 * Token'lar nerede durur: yalnızca panelin kendi oturum kaydında (`lib/session.ts`; `localStorage`), parola
 * oturumunun token'larıyla aynı yerde ve aynı biçimde. `oidc-client-ts`'in kullanıcı deposu bellektedir ve her
 * işlemden sonra boşaltılır; kimlik token'ı (`id_token`) ve profil hiçbir yere yazılmaz. Böylece oturumun tek bir
 * kaynağı olur: sekmeler arası eşitleme ve yenilemenin güvenlik kuralları (api-fetch.ts) iki oturum türü için de
 * aynı koddur ve başka bir sekmedeki yarım kalmış bir giriş bu sekmenin oturumuna dokunamaz.
 */

/** Sağlayıcının kullanıcıyı geri gönderdiği adres; sağlayıcıda izinli yönlendirme adresi olarak kayıtlı olmalıdır. */
export const CALLBACK_PATH = "/login/callback"

type Provider = { authority: string; clientId: string }

export type ProviderSignIn = Tokens & {
  /** Girişi başlatırken verilen dönüş adresi; depodan okunduğu için doğrulanmadan kullanılmaz. */
  returnTo: unknown
}

// Sağlayıcının kesin reddi sayılan yanıtlar (RFC 6749 §5.2): yenileme token'ı geçersiz, süresi dolmuş ya da iptal
// edilmiş, veya istemcinin bu yolu kullanmasına artık izin yok. Ötekiler (`server_error`, `temporarily_unavailable`,
// zaman aşımı, ağ hatası) geçicidir ve oturumu bitirmez.
const REJECTIONS = ["invalid_grant", "invalid_client", "unauthorized_client"]

// Yenilemede `oidc-client-ts` bir kullanıcı nesnesi ister ve kimlik bilgilerini (claim) ondan taşır. Panel onları
// saklamadığı için yerlerinde boş değerler durur; yenileme isteği yalnızca yenileme token'ını gönderir ve bu değerleri
// okumaz. Yenilemeden dönen kullanıcının profili de kullanılmaz: hesabın kim olduğunu backend söyler (`/auth/me`).
const NO_CLAIMS: IdTokenClaims = { sub: "", iss: "", aud: "", exp: 0, iat: 0 }

const REQUEST_TIMEOUT_SECONDS = 10

const managers = new Map<string, UserManager>()

function managerFor(provider: Provider): UserManager {
  const key = `${provider.authority}\n${provider.clientId}`
  let manager = managers.get(key)

  if (!manager) {
    manager = new UserManager({
      authority: provider.authority,
      client_id: provider.clientId,
      redirect_uri: new URL(CALLBACK_PATH, window.location.origin).href,
      // Yalnızca Authorization Code akışı; PKCE kitaplıkta varsayılan olarak açıktır ve kapatılmaz. İstemci
      // herkese açıktır: gizli anahtarı (`client_secret`) yoktur.
      response_type: "code",
      // Bellekte durur ve her işlemden sonra boşaltılır (yukarıda). Varsayılanı `sessionStorage`'dır.
      userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
      // `state` ve PKCE doğrulayıcısı yönlendirme süresince burada bekler. Giriş, başladığı sekmede biter; bu yüzden
      // varsayılan `localStorage` yerine sekmeye özgü ve sekme kapanınca silinen `sessionStorage` kullanılır.
      stateStore: new WebStorageStateStore({ store: window.sessionStorage }),
      // Token'ı kitaplığın zamanlayıcısı yenilemez. Yenileme 401 üzerine, sekmeler arası kilidin altında yapılır
      // (api-fetch.ts); zamanlayıcı o sırayı bilmez ve bir kez kullanılan yenileme token'ını iki sekmeden sunabilirdi.
      automaticSilentRenew: false,
      // Gizli iframe ile yenileme (`prompt=none`) kullanılmaz. Boş adres, yenileme token'ı olmayan bir kullanıcıyla
      // çağrılan `signinSilent`'ın iframe açmak yerine hata vermesini sağlar; varsayılanı `redirect_uri`'dir.
      silent_redirect_uri: "",
      // Oturum izleme iframe'i ve `userinfo` isteği yok: hesabı backend tanıtır.
      monitorSession: false,
      loadUserInfo: false,
      // Sağlayıcıya giden istekler (keşif belgesi, kodun token'a çevrilmesi) bu sürede yanıtlanmazsa bırakılır;
      // varsayılanda süre yoktur. Yanıt vermeyen bir sağlayıcı yenilemeyi, onunla birlikte sekmeler arası kilidi ve
      // bekleyen bütün istekleri süresiz tutardı. Zaman aşımı bir ret değildir: oturum korunur.
      requestTimeoutInSeconds: REQUEST_TIMEOUT_SECONDS,
    })
    managers.set(key, manager)
  }

  return manager
}

/**
 * Kullanıcıyı sağlayıcının giriş sayfasına gönderir. Sayfa oradan ayrıldığı için dönen söz normalde sonuçlanmaz;
 * yalnızca sağlayıcıya ulaşılamazsa (keşif belgesi okunamazsa) hata verir.
 *
 * Dönüş adresi adreste değil, `state` kaydının yanında (`sessionStorage`) taşınır.
 */
export async function startProviderSignIn(
  provider: Provider & { scopes: string },
  returnTo: string
): Promise<void> {
  await managerFor(provider).signinRedirect({
    scope: provider.scopes.trim() || "openid",
    state: { returnTo },
  })
}

/**
 * Sağlayıcıdan dönen adresi işler: `state`'i doğrular, kodu PKCE doğrulayıcısıyla token'lara çevirir. Token'lar
 * çağırana verilir ve kitaplığın deposunda bırakılmaz.
 */
export async function completeProviderSignIn(
  provider: Provider,
  url: string
): Promise<ProviderSignIn> {
  const manager = managerFor(provider)

  try {
    const user = await manager.signinRedirectCallback(url)
    const state: unknown = user.state

    return {
      accessToken: user.access_token,
      refreshToken: user.refresh_token ?? "",
      returnTo:
        typeof state === "object" && state !== null && "returnTo" in state
          ? state.returnTo
          : undefined,
    }
  } finally {
    await manager.removeUser()
  }
}

/**
 * Oturumun token'larını sağlayıcının yenileme token'ı ile yeniler (`signinSilent`; iframe değil). Sağlayıcı yenileme
 * token'ı vermediyse ya da artık kabul etmiyorsa `RenewalRejected` fırlatır; oturum o zaman biter.
 */
export async function renewProviderTokens(
  session: ProviderSession
): Promise<Tokens> {
  if (!session.refreshToken) {
    throw new RenewalRejected("The provider issued no refresh token.")
  }

  const manager = managerFor(session)

  try {
    await manager.storeUser(
      new User({
        access_token: session.accessToken,
        refresh_token: session.refreshToken,
        token_type: "Bearer",
        profile: NO_CLAIMS,
      })
    )

    // Süre burada ayrıca verilir: kitaplığın bu sürümü (3.5.0) yenileme isteğinde ayarlardaki süreyi, çağrıda
    // verilmeyen (tanımsız) değerle ezer ve istek süresiz kalır.
    const user = await manager.signinSilent({
      silentRequestTimeoutInSeconds: REQUEST_TIMEOUT_SECONDS,
    })

    if (!user?.access_token) {
      throw new Error("The provider returned no access token.")
    }

    return {
      accessToken: user.access_token,
      // Yenileme token'ını döndürmeyen sağlayıcı yanıtta yenisini vermez; eskisi geçerli kalır.
      refreshToken: user.refresh_token ?? session.refreshToken,
    }
  } catch (error) {
    if (
      error instanceof ErrorResponse &&
      error.error !== null &&
      REJECTIONS.includes(error.error)
    ) {
      throw new RenewalRejected(error.error)
    }

    throw error
  } finally {
    await manager.removeUser()
  }
}

/** Kitaplığın bellekte tuttuğu kullanıcıyı (varsa) bırakır; çıkışta çağrılır. Sağlayıcıya istek göndermez. */
export async function forgetProviderUser(provider: Provider): Promise<void> {
  await managerFor(provider).removeUser()
}

/** Hata sağlayıcının kendi yanıtı mı (ör. kullanıcı izni reddetti: `access_denied`)? */
export function isProviderError(error: unknown): boolean {
  return error instanceof ErrorResponse
}
