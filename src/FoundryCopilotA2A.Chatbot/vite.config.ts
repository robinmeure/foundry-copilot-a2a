import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'

export default defineConfig({
  plugins: [react()],
  build: {
    rolldownOptions: {
      input: {
        main: fileURLToPath(new URL('./index.html', import.meta.url)),
        authRedirect: fileURLToPath(new URL('./auth-redirect.html', import.meta.url)),
      },
    },
  },
  server: {
    port: 5174,
    strictPort: true,
    fs: {
      allow: [
        fileURLToPath(new URL('.', import.meta.url)),
        fileURLToPath(new URL('../FoundryCopilotA2A.BrowserShared', import.meta.url)),
      ],
    },
  },
})
