/** Girişten sonra açılacak sayfa, başka bir dönüş adresi yoksa. */
export const HOME_PATH = "/products"

/**
 * Girişten sonra dönülecek adres. Değer yönlendirme durumundan veya depodan gelir; yalnızca panelin kendi içindeki bir
 * yol kabul edilir (`//evil.example` gibi başka bir siteye götüren değerler değil).
 */
export function returnPath(value: unknown): string {
  // Tarayıcılar ters eğik çizgiyi de eğik çizgi sayar: `/\evil.example` başka bir siteye götürür.
  return typeof value === "string" &&
    value.startsWith("/") &&
    !value.startsWith("//") &&
    !value.startsWith("/\\")
    ? value
    : HOME_PATH
}
