/// <reference types="vite/client" />

// `vite/client` types `ImportMetaEnv` with an `any` index signature. Declaring the vars we actually
// read gives them a real type — an explicit member wins over the index signature — so nothing in
// `api.ts` has to touch `any` to read configuration.
interface ImportMetaEnv {
  /**
   * Absolute or origin-relative base URL of the EAMS .NET API, **including** the `/api/v1` prefix
   * and without a trailing slash. Unset falls back to the local dev default in `api.ts`.
   */
  readonly VITE_API_BASE_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
