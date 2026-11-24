import i18n from "i18next"
import { initReactI18next } from "react-i18next"

import en from "./locales/en.json"
import tr from "./locales/tr.json"

export const languages = ["en", "tr"] as const
export type Language = (typeof languages)[number]

const STORAGE_KEY = "haldus-shop.language"

function isLanguage(value: string | null): value is Language {
  return languages.some((language) => language === value)
}

const stored = localStorage.getItem(STORAGE_KEY)

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, tr: { translation: tr } },
  lng: isLanguage(stored) ? stored : "en",
  fallbackLng: "en",
  interpolation: { escapeValue: false },
})

document.documentElement.lang = i18n.language

i18n.on("languageChanged", (language) => {
  localStorage.setItem(STORAGE_KEY, language)
  document.documentElement.lang = language
})

export default i18n
