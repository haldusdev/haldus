import 'i18next';

import type en from './locales/en.json';

// Çeviri anahtarları derleme zamanında denetlenir: var olmayan bir anahtar tip hatasıdır.
declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'translation';
    resources: { translation: typeof en };
  }
}
