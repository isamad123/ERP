// Browser end-to-end tests (slice S1.12a, ruling Q19). Started by the xunit test WebEndToEndTests (Suite=WebE2E), which
// runs the real API host over HTTPS on a provisioned customer and passes E2E_BASE_URL, E2E_HOST and E2E_CONTROL_URL.
// Chromium is the browser already on the build machine (Playwright 1.63.0); no other browser is installed.
import { defineConfig, devices } from '@playwright/test';

const host = process.env.E2E_HOST ?? 'e2e.erp.test';

export default defineConfig({
  testDir: './e2e',
  timeout: 180_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  outputDir: 'test-results/output',
  use: {
    baseURL: process.env.E2E_BASE_URL,
    ignoreHTTPSErrors: true, // the test host's own self-signed certificate
    actionTimeout: 20_000,
    navigationTimeout: 30_000,
    trace: 'off',
    screenshot: 'only-on-failure',
    // The test host is local: no proxy (a build machine may set one for outbound traffic).
    launchOptions: { args: [`--host-resolver-rules=MAP ${host} 127.0.0.1`, '--no-proxy-server'] },
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 900 } } }],
});
