import react from '@vitejs/plugin-react'
import { defineConfig, type ProxyOptions } from 'vite'

/**
 * Aspire injects services__api__http__0 when the Vite app has WithReference(api).
 * Standalone `npm run dev` / `preview` needs VITE_API_PROXY (see web/README.md).
 */
function apiTarget(): string {
  const target =
    process.env.services__api__http__0 ??
    process.env.services__api__https__0 ??
    process.env.VITE_API_PROXY

  if (!target) {
    console.warn(
      '[web] No API proxy target (services__api__http__0 / VITE_API_PROXY). ' +
        'Set VITE_API_PROXY to the API base URL, or run via Aspire AppHost.',
    )
    return 'http://127.0.0.1:8080'
  }

  return target
}

function apiProxy(): Record<string, string | ProxyOptions> {
  const target = apiTarget()
  return {
    '/api': { target, changeOrigin: true },
    '/hubs': { target, changeOrigin: true, ws: true },
    '/health': { target, changeOrigin: true },
    '/openapi': { target, changeOrigin: true },
  }
}

// https://vite.dev/config/
export default defineConfig(({ command }) => {
  // `vite` and `vite preview` use command "serve"; `vite build` must not require API env.
  const proxy = command === 'serve' ? apiProxy() : undefined

  return {
    plugins: [react()],
    server: {
      host: true,
      port: Number(process.env.PORT) || 5173,
      strictPort: true,
      ...(proxy ? { proxy } : {}),
    },
    preview: {
      host: true,
      port: Number(process.env.PORT) || 4173,
      strictPort: true,
      ...(proxy ? { proxy } : {}),
    },
  }
})
