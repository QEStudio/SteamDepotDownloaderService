import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const proxyTarget = env.VITE_PROXY_TARGET

  return {
    plugins: [react()],
    server: proxyTarget
      ? {
          proxy: {
            '/health': { target: proxyTarget, changeOrigin: true },
            '/api': { target: proxyTarget, changeOrigin: true },
            '/ws': { target: proxyTarget, ws: true, changeOrigin: true },
          },
        }
      : undefined,
  }
})
