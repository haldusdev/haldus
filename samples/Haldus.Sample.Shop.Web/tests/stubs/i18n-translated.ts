// Bileşen testlerinde `@/i18n` yerine yüklenir (support/ui.ts). Gerçeği `document` ister; bu, aynı çeviri
// dosyalarını ve aynı ayarları kullanan gerçek bir i18next örneğidir, yalnızca tarayıcıya dokunan kısmı yoktur.
import i18n from "i18next"
import { initReactI18next } from "react-i18next"

import en from "../../src/i18n/locales/en.json"
import tr from "../../src/i18n/locales/tr.json"

export const languages = ["en", "tr"] as const
export type Language = (typeof languages)[number]

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, tr: { translation: tr } },
  lng: "en",
  fallbackLng: "en",
  interpolation: { escapeValue: false },
})

export default i18n
