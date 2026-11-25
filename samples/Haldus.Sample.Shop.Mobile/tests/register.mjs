// Testler paket eklemeden, Node'un kendi test koşucusu ve tip silmesiyle çalışır. Bu dosya Metro'nun yaptığını
// Node'a öğretir (`@/` takma adı, uzantısız yollar) ve Node'da yüklenemeyen modüllerin (React Native, Expo'nun yerel
// modülleri ve onlara dayanan `expo-auth-session`, çeviri dosyalarını isteyen `@/i18n`) yerine elle yazılmış
// taklitler koyar.
import { existsSync, statSync } from 'node:fs';
import { registerHooks } from 'node:module';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const tests = import.meta.dirname;
const root = path.resolve(tests, '..');

const stubs = new Map([
  ['@/i18n', path.join(tests, 'stubs/i18n.ts')],
  ['react-native', path.join(tests, 'stubs/react-native.ts')],
  ['expo-secure-store', path.join(tests, 'stubs/expo-secure-store.ts')],
  ['expo-auth-session', path.join(tests, 'stubs/expo-auth-session.ts')],
]);

// Expo bu değişkeni derlerken koda gömer; testlerde adres sabit olsun diye burada verilir.
process.env.EXPO_PUBLIC_API_URL = 'http://shop.test';

function isFile(candidate) {
  return existsSync(candidate) && statSync(candidate).isFile();
}

/** Uzantısız yazılmış bir yolu paketleyicinin yaptığı gibi tamamlar. */
function complete(base) {
  return [base, `${base}.ts`, path.join(base, 'index.ts')].find(isFile);
}

// Paketin `type` alanı yoktur; biçim açıkça verilmezse Node `.ts` dosyasını önce CommonJS sayar ve uyarır. Test
// dosyalarının uzantısı aynı nedenle `.mts`'tir (onlar bu kancadan geçmeden yüklenir).
function typescript(file) {
  return { url: pathToFileURL(file).href, format: 'module-typescript', shortCircuit: true };
}

registerHooks({
  resolve(specifier, context, nextResolve) {
    const stub = stubs.get(specifier);

    if (stub) {
      return typescript(stub);
    }

    const parent = context.parentURL;
    let file;

    if (specifier.startsWith('@/')) {
      file = complete(path.join(root, specifier.slice(2)));
    } else if (
      specifier.startsWith('.') &&
      parent?.startsWith('file:') &&
      !parent.includes('/node_modules/')
    ) {
      // orval çıktısı göreli yolları uzantısız yazar (`../../lib/api-fetch`).
      file = complete(path.resolve(path.dirname(fileURLToPath(parent)), specifier));
    }

    return file?.endsWith('.ts') ? typescript(file) : nextResolve(specifier, context);
  },
});
