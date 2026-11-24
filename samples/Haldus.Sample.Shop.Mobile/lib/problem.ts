/**
 * Backend'in hata yanıtlarını (RFC 9457 ProblemDetails) okumak için yardımcılar. Hata `unknown` gelir ve burada
 * daraltılır; tipleri orval üretir, burada yalnızca biçimleri denetlenir.
 */

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

export function problemStatus(error: unknown): number | undefined {
  return isRecord(error) && typeof error.status === 'number' ? error.status : undefined;
}

/** Kullanıcıya gösterilecek metin: backend'in (isteğin dilinde yazdığı) açıklaması, yoksa verilen yedek. */
export function problemMessage(error: unknown, fallback: string): string {
  if (isRecord(error)) {
    if (typeof error.detail === 'string' && error.detail.length > 0) {
      return error.detail;
    }

    if (typeof error.title === 'string' && error.title.length > 0) {
      return error.title;
    }
  }

  return fallback;
}

/**
 * Alan düzeyindeki doğrulama hataları, alan adı küçük harfle başlayacak biçimde. Backend adları isteğin JSON
 * adlarıyla döndürür; baş harf yine de normalleştirilir.
 */
export function fieldErrors(error: unknown): Record<string, string[]> {
  if (!isRecord(error) || !isRecord(error.errors)) {
    return {};
  }

  const result: Record<string, string[]> = {};

  for (const [field, messages] of Object.entries(error.errors)) {
    if (Array.isArray(messages)) {
      const key = field
        .split('.')
        .map((part) => part.charAt(0).toLowerCase() + part.slice(1))
        .join('.');

      result[key] = messages.filter((message): message is string => typeof message === 'string');
    }
  }

  return result;
}
