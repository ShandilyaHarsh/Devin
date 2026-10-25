import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@platform/sdk': fileURLToPath(new URL('./platform/web/src/sdk/index.ts', import.meta.url)),
    },
  },
  test: {
    environment: 'jsdom',
    include: [
      'tests/web/**/*.test.{ts,tsx}',
      'platform/web/src/**/*.test.{ts,tsx}',
      'apps/*/web/**/*.test.{ts,tsx}',
    ],
    setupFiles: ['./platform/web/src/test-setup.ts'],
    restoreMocks: true,
    passWithNoTests: false,
  },
});
