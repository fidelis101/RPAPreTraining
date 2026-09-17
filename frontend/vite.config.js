import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // Everything under /api is forwarded to the .NET middleware.
    proxy: {
      '/api': 'http://localhost:5080'
    }
  }
})
