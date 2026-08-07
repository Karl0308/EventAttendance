import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

// `base` is the URL prefix the built bundle expects to be served from, and it must match the IIS
// application's virtual path (or the Pages project path) exactly. A mismatch still serves index.html
// but 404s every asset, so the symptom is a blank white page rather than anything that names routing.
//
// Read from VITE_BASE_URL so a deployment sets it in `.env.production` without editing this file. The
// '/EventAttendance/' fallback is GitHub Pages, which is where `main` has always published — keeping
// it as the default means the existing Pages deploy is unaffected by this becoming configurable.
// The dev server always serves from '/', since Vite is the only thing listening on 5173.
//
// `main.tsx` passes `import.meta.env.BASE_URL` — which Vite derives from this — to react-router's
// `basename`, so the router prefix and the asset prefix cannot drift apart.
// https://vite.dev/config/
export default defineConfig(({ command, mode }) => {
  const env = loadEnv(mode, process.cwd())
  return {
    base: command === 'build' ? (env.VITE_BASE_URL || '/EventAttendance/') : '/',
    plugins: [react()],
  }
})
