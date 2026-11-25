// Bir modülün yerine test süresince başka bir dosya koyar. register.mjs'teki kancaların üstüne eklenir (sonradan
// eklenen kanca önce çalışır); bu yüzden yerine konan modülü kullanan kod bundan SONRA, `await import(...)` ile
// yüklenmelidir.
import { registerHooks } from "node:module"
import path from "node:path"
import { pathToFileURL } from "node:url"

const tests = path.resolve(import.meta.dirname, "..")
const sourceUrl = pathToFileURL(path.resolve(tests, "../src")).href + "/"

/**
 * @param specifier Kaynak kodda yazıldığı haliyle modül adı (`@/i18n`, `react-router`).
 * @param file Yerine yüklenecek dosya, `tests` klasörüne göre.
 * @param fromSourceOnly Yalnızca `src` altındaki dosyaların içe aktarımı değişir; taklidin kendisi gerçeğine ulaşır.
 */
export function replaceModule(
  specifier: string,
  file: string,
  fromSourceOnly = false
) {
  const url = pathToFileURL(path.join(tests, file)).href
  const format = file.endsWith(".tsx") ? "module" : "module-typescript"

  registerHooks({
    resolve(requested, context, nextResolve) {
      if (
        requested === specifier &&
        (!fromSourceOnly || context.parentURL?.startsWith(sourceUrl))
      ) {
        return { url, format, shortCircuit: true }
      }

      return nextResolve(requested, context)
    },
  })
}
