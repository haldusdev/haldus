import { getLocales } from 'expo-localization';
import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';

import { readItem, writeItem } from '@/lib/storage';

import en from './locales/en.json';
import tr from './locales/tr.json';

export const languages = ['en', 'tr'] as const;
export type Language = (typeof languages)[number];

const STORAGE_KEY = 'haldus-shop.language';

function isLanguage(value: string | null | undefined): value is Language {
  return languages.some((language) => language === value);
}

// Uygulama cihazın diliyle açılır; kullanıcı Hesap ekranından değiştirirse seçimi saklanır.
const device = getLocales()[0]?.languageCode;

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, tr: { translation: tr } },
  lng: isLanguage(device) ? device : 'en',
  fallbackLng: 'en',
  interpolation: { escapeValue: false },
});

/** Saklanan dil seçimini uygular; uygulama açılırken bir kez çağrılır. */
export async function restoreLanguage(): Promise<void> {
  const stored = await readItem(STORAGE_KEY);

  if (isLanguage(stored) && stored !== i18n.language) {
    await i18n.changeLanguage(stored);
  }
}

export async function setLanguage(language: Language): Promise<void> {
  await i18n.changeLanguage(language);
  await writeItem(STORAGE_KEY, language);
}

export default i18n;
