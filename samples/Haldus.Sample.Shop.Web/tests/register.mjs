// Testler paket eklemeden, Node'un kendi test koşucusu ve tip silmesiyle çalışır. Bu dosya Vite'ın derlerken
// yaptığı iki şeyi Node'a öğretir: `@/` takma adı ve `import.meta.env`. Ayrıca Node'da yüklenemeyen modüllerin
// (çeviri dosyalarını ve tarayıcıyı isteyen `@/i18n`) yerine elle yazılmış bir taklit koyar.
import { existsSync, statSync } from "node:fs"
import { registerHooks } from "node:module"
import path from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"

const tests = import.meta.dirname
const source = path.resolve(tests, "../src")
const sourceUrl = pathToFileURL(source).href + "/"

const stubs = new Map([["@/i18n", path.join(tests, "stubs/i18n.ts")]])

globalThis.__viteEnv = {}

function isFile(candidate) {
  return existsSync(candidate) && statSync(candidate).isFile()
}

/** Uzantısız yazılmış bir yolu paketleyicinin yaptığı gibi tamamlar. */
function complete(base) {
  if (isFile(base)) {
    return undefined
  }

  return [`${base}.ts`, path.join(base, "index.ts")].find(isFile)
}

function typescript(file) {
  return {
    url: pathToFileURL(file).href,
    format: "module-typescript",
    shortCircuit: true,
  }
}

registerHooks({
  resolve(specifier, context, nextResolve) {
    const stub = stubs.get(specifier)

    if (stub) {
      return typescript(stub)
    }

    if (specifier.startsWith("@/")) {
      const file = complete(path.join(source, specifier.slice(2)))

      if (file) {
        return typescript(file)
      }
    }

    // orval çıktısı göreli yolları uzantısız yazar (`../../lib/api-fetch`).
    const parent = context.parentURL

    if (
      specifier.startsWith(".") &&
      parent?.startsWith("file:") &&
      !parent.includes("/node_modules/")
    ) {
      const file = complete(
        path.resolve(path.dirname(fileURLToPath(parent)), specifier)
      )

      if (file) {
        return typescript(file)
      }
    }

    return nextResolve(specifier, context)
  },

  load(url, context, nextLoad) {
    const result = nextLoad(url, context)

    if (url.startsWith(sourceUrl) && result.source) {
      const text = result.source.toString()

      if (text.includes("import.meta.env")) {
        // Vite bu ifadeyi derlerken değerleriyle değiştirir; Node'da `import.meta.env` yoktur.
        return {
          ...result,
          source: text.replaceAll("import.meta.env", "globalThis.__viteEnv"),
        }
      }
    }

    return result
  },
})
