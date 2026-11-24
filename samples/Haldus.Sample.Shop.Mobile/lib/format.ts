import i18n from '@/i18n';

/** Tutarlar ve tarihler uygulamanın diline göre biçimlenir. Örnek mağazada para birimi yoktur; tutar yalın sayıdır. */
export function formatAmount(value: number): string {
  return new Intl.NumberFormat(i18n.language, {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value);
}

export function formatDate(value: string): string {
  return new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(value));
}
