/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Backend'in adresi; verilmezse geliştirme vekilinin öneki (/api) kullanılır. */
  readonly VITE_API_BASE?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
