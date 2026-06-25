import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Project Pages serve under /EventAttendance/; dev server stays at /.
// https://vite.dev/config/
export default defineConfig(({ command }) => ({
  base: command === 'build' ? '/EventAttendance/' : '/',
  plugins: [react()],
}))
