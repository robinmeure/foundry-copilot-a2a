import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'

export default defineConfig({
  plugins: [react()],
  resolve: {
    // The shared chat package resolves React from the workspace root; keep a single copy.
    dedupe: ['react', 'react-dom'],
  },
  build: {
    rolldownOptions: {
      input: {
        main: fileURLToPath(new URL('./index.html', import.meta.url)),
        authRedirect: fileURLToPath(new URL('./auth-redirect.html', import.meta.url)),
      },
    },
  },
  server: {
    port: 5175,
    strictPort: true,
    fs: {
      allow: [
        // Only the app, its shared sources and the hoisted workspace dependencies; never the repo root.
        fileURLToPath(new URL('.', import.meta.url)),
        fileURLToPath(new URL('../FoundryCopilotA2A.ChatShared', import.meta.url)),
        fileURLToPath(new URL('../../node_modules', import.meta.url)),
        fileURLToPath(new URL('../FoundryCopilotA2A.BrowserShared', import.meta.url)),
      ],
    },
  },
})

