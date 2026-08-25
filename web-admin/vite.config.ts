import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

// `base` is the URL prefix the built bundle expects to be served from, and it must match the IIS
// application's virtual path exactly. A mismatch still serves index.html but 404s every asset, so the
// symptom is a blank white page rather than anything that names routing.
//
// Read from VITE_BASE_URL so a deployment sets it in `.env.production` without editing this file.
// The dev server always serves from '/', since Vite is the only thing listening on 5173.
//
// `main.tsx` passes `import.meta.env.BASE_URL` — which Vite derives from this — to react-router's
// `basename`, so the router prefix and the asset prefix cannot drift apart.
//
// ---------------------------------------------------------------------------------------------
// THE DEV PROXY IS A SESSION REQUIREMENT, NOT A CONVENIENCE
// ---------------------------------------------------------------------------------------------
//
// The SPA holds no refresh token — it is an `httpOnly`, `Secure`, `SameSite=Strict` cookie the
// browser attaches by itself (`AuthCookies.cs`). Two things follow for development, and both are why
// this proxy exists rather than the SPA calling `http://localhost:5080` directly:
//
//   - **`SameSite=Strict` and `credentials: "include"` across origins would need the API to opt into
//     credentialed CORS** — `AllowCredentials` plus an explicit origin allow-list. That is a wider
//     hole, permanently, so that a dev server can talk to a dev API. Proxying makes the two the same
//     origin and the question does not arise.
//   - **The refresh cookie's `Path` is the API's `/api/v1/auth`.** Proxying `/api` keeps the path the
//     browser sees identical to the one the server wrote, which is the single most common way this
//     mechanism fails silently in production (see `AuthCookies.RefreshCookiePath`) — so development
//     may as well exercise the same shape.
//
// `changeOrigin` is deliberately NOT set. The API is reached on localhost either way, and rewriting
// the Host header is the kind of thing that changes which absolute URLs a server generates. There is
// no `rewrite` either: the API is mounted at `/api/v1` and so is the proxy prefix, so the path passes
// through unchanged and `.env.example`'s VITE_API_BASE_URL stays the one place a URL is written.
//
// https://vite.dev/config/
const DEV_API_TARGET = 'http://localhost:5080'

export default defineConfig(({ command, mode }) => {
  const env = loadEnv(mode, process.cwd())
  return {
    base: command === 'build' ? (env.VITE_BASE_URL || '/') : '/',
    plugins: [react()],
    server: {
      proxy: {
        '/api': {
          target: env.VITE_DEV_API_TARGET || DEV_API_TARGET,
        },
      },
    },
  }
})
