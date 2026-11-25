// Testler paket eklemeden, Node'un kendi test koşucusu ve tip silmesiyle çalışır. Bu dosya Vite'ın derlerken
// yaptığı şeyleri Node'a öğretir: `@/` takma adı, `import.meta.env`, JSX ve JSON modülleri. Ayrıca Node'da
// yüklenemeyen modüllerin (çeviri dosyalarını ve tarayıcıyı isteyen `@/i18n`) yerine elle yazılmış bir taklit koyar;
// bileşen testleri onun yerine gerçek çevirileri taşıyan bir örnek yükler (support/ui.tsx).
import { existsSync, readFileSync, statSync } from "node:fs"
import { createRequire, registerHooks } from "node:module"
import path from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"

const tests = import.meta.dirname
const source = path.resolve(tests, "../src")
const sourceUrl = pathToFileURL(source).href + "/"
const testsUrl = pathToFileURL(tests).href + "/"

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

  return [
    `${base}.ts`,
    `${base}.tsx`,
    path.join(base, "index.ts"),
    path.join(base, "index.tsx"),
  ].find(isFile)
}

function typescript(file) {
  return {
    url: pathToFileURL(file).href,
    // Node'un tip silmesi JSX bilmez; .tsx dosyaları yüklenirken TypeScript ile çevrilir (load).
    format: file.endsWith(".tsx") ? "module" : "module-typescript",
    shortCircuit: true,
  }
}

function isOurs(url) {
  return url.startsWith(sourceUrl) || url.startsWith(testsUrl)
}

let compiler

/** JSX'i çevirir. Derleyici yalnızca bir .tsx dosyası yüklenince belleğe alınır. */
function transpile(url) {
  compiler ??= createRequire(import.meta.url)("typescript")

  const file = fileURLToPath(url)

  return compiler.transpileModule(readFileSync(file, "utf8"), {
    fileName: file,
    compilerOptions: {
      target: compiler.ScriptTarget.ES2023,
      module: compiler.ModuleKind.ESNext,
      jsx: compiler.JsxEmit.ReactJSX,
      verbatimModuleSyntax: true,
      inlineSourceMap: true,
    },
  }).outputText
}

function withEnv(text) {
  // Vite bu ifadeyi derlerken değerleriyle değiştirir; Node'da `import.meta.env` yoktur.
  return text.replaceAll("import.meta.env", "globalThis.__viteEnv")
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

    const resolved = nextResolve(specifier, context)

    // Uzantısıyla yazılmış .tsx (test dosyasının kendisi dahil) ve içe aktarma niteliği olmadan istenen JSON.
    if (
      isOurs(resolved.url) &&
      (resolved.url.endsWith(".tsx") || resolved.url.endsWith(".json"))
    ) {
      return { ...resolved, format: "module", shortCircuit: true }
    }

    return resolved
  },

  load(url, context, nextLoad) {
    if (isOurs(url) && url.endsWith(".tsx")) {
      return {
        format: "module",
        source: withEnv(transpile(url)),
        shortCircuit: true,
      }
    }

    if (isOurs(url) && url.endsWith(".json")) {
      // Vite JSON'u varsayılan dışa aktarımı olan bir modül olarak verir.
      return {
        format: "module",
        source: `export default ${readFileSync(fileURLToPath(url), "utf8")}`,
        shortCircuit: true,
      }
    }

    const result = nextLoad(url, context)

    if (url.startsWith(sourceUrl) && result.source) {
      const text = result.source.toString()

      if (text.includes("import.meta.env")) {
        return { ...result, source: withEnv(text) }
      }
    }

    return result
  },
})
