import { defineConfig, devices } from '@playwright/test';

/**
 * Browser tests: the real web app and API, driven like a shop would use them, in Arabic.
 * The API serves the built app (`ng build` first). Locally an API already running on the port is reused; otherwise it is started
 * (Development settings: the Docker database on 5433). E2E_CHANNEL=chrome uses the installed Chrome instead of downloading a browser.
 */
const baseURL = process.env['E2E_BASE_URL'] ?? 'http://localhost:5081';

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  globalSetup: './e2e/global-setup.ts',
  // One shop is shared by the tests; they run one at a time (the laptop is also short of memory).
  workers: 1,
  fullyParallel: false,
  retries: process.env['CI'] ? 1 : 0,
  timeout: 120_000,
  expect: { timeout: 10_000 },
  // On GitHub each failure also becomes an annotation on the run, readable without downloading the report.
  reporter: process.env['CI'] ? [['github'], ['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL,
    locale: 'ar-QA',
    timezoneId: 'Asia/Qatar',
    channel: process.env['E2E_CHANNEL'] || undefined,
    // Off by default: while tracing, Chrome never finishes the Excel upload's request (E2E_TRACE=1 turns it on to investigate others).
    trace: process.env['E2E_TRACE'] ? 'retain-on-failure' : 'off',
    screenshot: 'only-on-failure'
  },
  projects: [
    { name: 'desktop', use: { viewport: { width: 1280, height: 800 } }, testIgnore: '**/*.phone.e2e.ts' },
    { name: 'phone', use: { ...devices['Pixel 7'], channel: process.env['E2E_CHANNEL'] || undefined }, testMatch: '**/*.phone.e2e.ts' }
  ],
  webServer: {
    command: 'dotnet run --project ../Erp.Api --no-launch-profile --urls ' + baseURL,
    url: baseURL + '/health',
    reuseExistingServer: !process.env['CI'],
    timeout: 300_000,
    env: { ASPNETCORE_ENVIRONMENT: 'Development', Web__Root: process.cwd() + '/dist/erp-web/browser' }
  }
});
