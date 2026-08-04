/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_WAKEBOARD_VERSION?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
