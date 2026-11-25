import { Platform } from 'react-native';

import i18n from '@/i18n';
import { ProviderRejectionError, renewProviderTokens } from '@/lib/provider';
import { getSession, renewTokens, restoreSession, signOut } from '@/lib/session';
import type { Session } from '@/lib/session';

/**
 * Backend'in adresi. `EXPO_PUBLIC_API_URL` ile verilir; verilmezse yerel örnek backend varsayılır.
 * Android emülatöründe bilgisayarın localhost'u 10.0.2.2'dir; gerçek cihazda bilgisayarın ağ adresi yazılır.
 */
export const API_URL: string = process.env.EXPO_PUBLIC_API_URL ?? 'http://localhost:5080';

const CREDENTIAL_ROUTES = ['/auth/login', '/auth/register', '/auth/refresh'];

type Tokens = { accessToken: string; refreshToken: string };

/**
 * Parola oturumunun yenileme isteğini yapan işlev. Üretilen istemci bu dosyayı içe aktardığı için burada doğrudan
 * çağrılamaz (döngüsel bağımlılık olurdu); `lib/auth.ts` onu açılışta buraya verir. Sağlayıcı oturumu backend'den
 * değil sağlayıcının token ucundan yenilenir (`lib/provider.ts`); onun böyle bir bağımlılığı yoktur.
 */
type Refresher = (refreshToken: string) => Promise<Tokens>;

let refresher: Refresher | undefined;
let renewal: Promise<boolean> | undefined;

export function setSessionRefresher(next: Refresher) {
  refresher = next;
}

/**
 * orval'ın ürettiği her istek buradan geçer (orval.config.ts: `override.mutator`). Dört şey ekler: backend'in
 * adresi, oturumun token'ı, uygulamanın dili (backend mesajlarını o dilde döndürür) ve hata davranışı.
 *
 * Başarısız yanıtta gövde (ProblemDetails) fırlatılır; böylece TanStack Query isteği hatalı sayar ve
 * üretilen hook'ların hata tipi (`ProblemDetails | HttpValidationProblemDetails`) gerçekten gelen değerle eşleşir.
 */
export const apiFetch = <T>(url: string, options: RequestInit): Promise<T> =>
  request<T>(url, options, true);

async function request<T>(
  url: string,
  options: RequestInit,
  mayRenew: boolean,
  renewed = false
): Promise<T> {
  const headers = new Headers(options.headers);
  headers.set('Accept-Language', i18n.language);

  const session = getSession();
  // Giriş, kayıt ve yenileme token istemez; süresi dolmuş bir token onlara eklenmez.
  const credentialRoute = CREDENTIAL_ROUTES.some((route) => url.startsWith(route));

  // Çağıran kendi token'ını verdiyse (ör. girişten hemen sonra hesabı sormak) oturumunki eklenmez ve o isteğin 401'i
  // oturumu yenilemez: yinelenen istek yine çağıranın token'ını taşırdı ve bir yenileme boşa harcanırdı.
  const usesSession = session !== null && !credentialRoute && !headers.has('Authorization');

  if (session && usesSession) {
    headers.set('Authorization', `Bearer ${session.accessToken}`);
  }

  const response = await fetch(`${API_URL}${url}`, { ...options, headers });

  // Erişim token'ının süresi dolmuş olabilir: bir kez yenilenir ve istek yinelenir. Giriş ve yenileme uçlarının kendi
  // 401 yanıtı (yanlış parola, geçersiz yenileme token'ı) yenileme nedeni değildir.
  if (response.status === 401 && mayRenew && session && usesSession && (await renew(session))) {
    return request<T>(url, options, false, true);
  }

  // Sağlayıcı token'ı yeniledi ama backend yenisini de kabul etmiyor (hesap açılamıyor, audience değişti, sağlayıcıyla
  // giriş kapatıldı). Oturum kapatılmazsa her istek sağlayıcıda bir yenileme token'ı harcar ve uygulama "giriş
  // yapılmış" görünmeye devam eder. Parola oturumunda bu durum oluşmaz: token'ı veren de kabul eden de backend'dir.
  if (
    response.status === 401 &&
    renewed &&
    session?.kind === 'provider' &&
    usesSession &&
    getSession()?.accessToken === session.accessToken
  ) {
    await signOut();
  }

  const text = [204, 205, 304].includes(response.status) ? '' : await response.text();
  const data = parse(text);

  if (!response.ok) {
    // Bir Error değil, sözleşmedeki ProblemDetails gövdesi fırlatılır (üretilen hata tipiyle aynı biçim).
    throw typeof data === 'object' && data !== null
      ? data
      : { status: response.status, title: response.statusText };
  }

  return { data, status: response.status, headers: response.headers } as T;
}

/**
 * Aynı anda 401 alan istekler tek bir yenilemeyi bekler. Yenileme token'ı bir kez kullanılır: backend eskisini
 * (kısa bir toleransın dışında) yeniden görürse oturumu çalınmış sayıp kapatır. Sağlayıcılar da yenileme token'ını
 * çoğunlukla her kullanımda değiştirir; tek yenileme kuralı iki oturum türü için de aynıdır.
 */
function renew(sent: Session): Promise<boolean> {
  const current = getSession();

  // Oturum bu arada kapanmış veya başka bir hesaba geçmiş olabilir; o zaman bu isteğin yenileyeceği bir şey yoktur.
  if (!current || current.accountId !== sent.accountId) {
    return Promise.resolve(false);
  }

  if (current.accessToken !== sent.accessToken) {
    // Başka bir istek bu arada yeniledi.
    return Promise.resolve(true);
  }

  renewal ??= refresh(current).finally(() => {
    renewal = undefined;
  });

  return renewal;
}

/** Oturumu şimdi yeniler (ör. hesabın bilgisi değiştiğinde); süregelen bir yenileme varsa onu bekler. */
export function renewSession(): Promise<boolean> {
  const session = getSession();

  // Yenileme token'ı olmayan sağlayıcı oturumu yenilenemez. İstek üzerine yenileme onu kapatmaz; onu yalnızca
  // backend'in erişim token'ını reddetmesi bitirir.
  return session && session.refreshToken !== null ? renew(session) : Promise.resolve(false);
}

async function refresh(sent: Session): Promise<boolean> {
  // Yanıt beklenirken oturum değişebilir (çıkış, başka hesapla giriş); sonuç yalnızca hâlâ aynı oturuma uygulanır.
  const unchanged = () => {
    const current = getSession();

    return (
      current !== null &&
      current.accountId === sent.accountId &&
      current.accessToken === sent.accessToken &&
      current.refreshToken === sent.refreshToken
    );
  };

  if (sent.refreshToken === null) {
    // Sağlayıcı yenileme token'ı vermedi: yenilenecek bir şey yoktur ve erişim token'ı reddedilince oturum biter.
    // Tarayıcıda başka bir sekme bu arada yeniden giriş yapmış olabilir; onun oturumu kapatılmaz.
    if (Platform.OS === 'web') {
      await restoreSession();
    }

    if (unchanged()) {
      await signOut();
    }

    return false;
  }

  try {
    const pending =
      sent.kind === 'provider'
        ? renewProviderTokens(sent.provider, sent.refreshToken)
        : refresher?.(sent.refreshToken);

    if (!pending) {
      return false;
    }

    const tokens = await pending;

    // Tarayıcıda başka bir sekmenin yazdığı değişiklik, olayı henüz gelmemiş olsa da görülsün diye depo yeniden okunur.
    if (Platform.OS === 'web') {
      await restoreSession();
    }

    if (!unchanged()) {
      // Yenilenen token'lar başka bir hesabın oturumuna yazılmaz.
      return false;
    }

    await renewTokens(tokens.accessToken, tokens.refreshToken);
    return true;
  } catch (error) {
    // 409: oturumu az önce başka bir istek yeniledi (tarayıcıda başka bir sekme). Backend bu isteğe token vermez ama
    // oturumu da kapatmaz; yeni token'lar depoya yazılınca alınır. Oturum kapatılmaz.
    if (isRenewedElsewhere(error)) {
      return adoptRenewal(sent);
    }

    // Oturumu yalnızca kesin bir ret bitirir: backend'in 400 / 401 yanıtı ya da sağlayıcının ret kodu. Ağ hatasında,
    // sunucu hatasında veya bir vekilin hata sayfasında (5xx) oturum korunur ve istek hatasıyla döner; yoksa geçici
    // bir arıza herkesi oturumdan atardı.
    if (isRejection(error)) {
      // Sağlayıcının 409'a karşılık gelen bir yanıtı yoktur: tarayıcıda öteki sekme aynı yenileme token'ını az önce
      // kullandıysa bu sekmeninki reddedilir. Öteki sekmenin yazdığı token'lar varsa oturum onlarla sürer.
      if (sent.kind === 'provider' && Platform.OS === 'web' && (await adoptRenewal(sent))) {
        return true;
      }

      if (unchanged()) {
        await signOut();
      }
    }

    return false;
  }
}

function isRenewedElsewhere(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'status' in error && error.status === 409;
}

/** Yenilemeyi yapan öteki isteğin depoya yazdığı oturumu alır; yazılması için kısa bir süre bekler. */
async function adoptRenewal(sent: Session): Promise<boolean> {
  for (let attempt = 0; attempt < 5; attempt++) {
    await restoreSession();
    const current = getSession();

    if (
      current &&
      current.accountId === sent.accountId &&
      current.refreshToken !== sent.refreshToken
    ) {
      return true;
    }

    await new Promise((resolve) => setTimeout(resolve, 100));
  }

  return false;
}

function isRejection(error: unknown): boolean {
  return (
    error instanceof ProviderRejectionError ||
    (typeof error === 'object' &&
      error !== null &&
      'status' in error &&
      (error.status === 400 || error.status === 401))
  );
}
function parse(text: string): unknown {
  if (!text) {
    return undefined;
  }

  try {
    return JSON.parse(text);
  } catch {
    // JSON olmayan bir gövde (ör. bir vekil sunucunun hata sayfası) içerik olarak taşınmaz.
    return undefined;
  }
}
