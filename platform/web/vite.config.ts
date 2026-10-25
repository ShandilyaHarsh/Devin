import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

const backend = 'https://localhost:5443';

// __Host- cookies need a secure origin, so the dev server also uses HTTPS (cert exported by scripts/dev.sh).
const certDir = fileURLToPath(new URL('../../.certs/', import.meta.url));
const https = existsSync(`${certDir}localhost.pem`)
  ? { cert: readFileSync(`${certDir}localhost.pem`), key: readFileSync(`${certDir}localhost.key`) }
  : undefined;

export default defineConfig({
  root: fileURLToPath(new URL('.', import.meta.url)),
  plugins: [react()],
  resolve: {
    alias: {
      '@platform/sdk': fileURLToPath(new URL('./src/sdk/index.ts', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    https,
    fs: { allow: [fileURLToPath(new URL('../..', import.meta.url))] },
    proxy: {
      '/api': { target: backend, changeOrigin: false, secure: false },
      '/health': { target: backend, changeOrigin: false, secure: false },
    },
  },
  build: {
    outDir: fileURLToPath(new URL('../host/wwwroot', import.meta.url)),
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 1500,
  },
});
