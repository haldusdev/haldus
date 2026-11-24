import i18n from '@/i18n';
import { getSession } from '@/lib/session';

/**
 * Backend'in adresi. `EXPO_PUBLIC_API_URL` ile verilir; verilmezse yerel örnek backend varsayılır.
 * Android emülatöründe bilgisayarın localhost'u 10.0.2.2'dir; gerçek cihazda bilgisayarın ağ adresi yazılır.
 */
export const API_URL: string = process.env.EXPO_PUBLIC_API_URL ?? 'http://localhost:5080';

/**
 * orval'ın ürettiği her istek buradan geçer (orval.config.ts: `override.mutator`). Dört şey ekler: backend'in
 * adresi, demo kimliğinin başlıkları, uygulamanın dili (backend mesajlarını o dilde döndürür) ve hata davranışı.
 *
 * Başarısız yanıtta gövde (ProblemDetails) fırlatılır; böylece TanStack Query isteği hatalı sayar ve
 * üretilen hook'ların hata tipi (`ProblemDetails | HttpValidationProblemDetails`) gerçekten gelen değerle eşleşir.
 */
export const apiFetch = async <T>(url: string, options: RequestInit): Promise<T> => {
  const headers = new Headers(options.headers);
  headers.set('Accept-Language', i18n.language);

  const session = getSession();

  if (session) {
    headers.set('X-User-Id', session.userId);
    headers.set('X-Tenant-Id', session.tenantId);
  }

  const response = await fetch(`${API_URL}${url}`, { ...options, headers });
  const text = [204, 205, 304].includes(response.status) ? '' : await response.text();
  const data = parse(text);

  if (!response.ok) {
    // Bir Error değil, sözleşmedeki ProblemDetails gövdesi fırlatılır (üretilen hata tipiyle aynı biçim).
    throw typeof data === 'object' && data !== null
      ? data
      : { status: response.status, title: response.statusText };
  }

  return { data, status: response.status, headers: response.headers } as T;
};

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
