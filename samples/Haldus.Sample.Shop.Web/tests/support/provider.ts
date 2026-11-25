// Kimlik sağlayıcıyla ilgili testlerin ortak kurulumu: `oidc-client-ts` yerine taklidini koyar (stubs/oidc-client-ts.ts).
// Sınanan modüller bundan SONRA, `await import(...)` ile yüklenmelidir.
import "./browser.ts"
import "./fetch.ts"

import { replaceModule } from "./modules.ts"

replaceModule("oidc-client-ts", "stubs/oidc-client-ts.ts")

export {
  ErrorResponse,
  ErrorTimeout,
  InMemoryWebStorage,
  provider,
  User,
  type Renewed,
} from "../stubs/oidc-client-ts.ts"
