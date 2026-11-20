// Unit and component tests of the web workspace (slice S1.12a). Browser end-to-end tests are Playwright (apps/web/e2e).
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    include: ['apps/*/src/**/*.test.{ts,tsx}', 'packages/*/src/**/*.test.{ts,tsx}', 'packages/*/scripts/**/*.test.{js,mjs,ts}'],
    css: false,
  },
});
