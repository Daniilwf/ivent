import { defineConfig, devices } from '@playwright/test';

// Base address of the site under test. Smoke tests pass any address (staging, production).
// E2E_PORT moves the local E2E site off :5080 (another worktree's run may hold it).
const port = process.env['E2E_PORT'] ?? '5080';
const baseURL = process.env['E2E_BASE_URL'] ?? `http://localhost:${port}`;

// Optional installed browser channel (msedge, chrome) when the bundled Chromium is unavailable.
const channel = process.env['PW_CHANNEL'];
const browser = channel ? { channel } : {};

// The scenarios that run after the rest (tests/late), on either path separator
const late = /[\\/]late[\\/][^\\/]+\.spec\.ts$/;

const desktop = {
  ...devices['Desktop Chrome'],
  ...browser,
  viewport: { width: 1440, height: 900 },
};
const phone = {
  ...devices['Pixel 7'],
  ...browser,
  viewport: { width: 390, height: 844 },
  isMobile: true,
  hasTouch: true,
};

export default defineConfig({
  testDir: './tests',
  globalSetup: './support/global-setup.ts',
  outputDir: '../test-artifacts/playwright/results',
  fullyParallel: true,
  // A scenario plays several people through a whole cycle
  timeout: 90_000,
  forbidOnly: !!process.env['CI'],
  // No retries: a scenario that fails once is a finding (D-231), not noise to paper over.
  retries: 0,
  reporter: [
    ['list'],
    ['html', { outputFolder: '../test-artifacts/playwright/report', open: 'never' }],
  ],
  use: {
    baseURL,
    // The Docker site on https://localhost has Caddy's own certificate: E2E_IGNORE_HTTPS_ERRORS=1 there, never elsewhere
    ignoreHTTPSErrors: process.env['E2E_IGNORE_HTTPS_ERRORS'] === '1',
    locale: 'ru-RU',
    timezoneId: 'Europe/Moscow',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    // Animations are disabled in E2E; the site honours reduced motion.
    contextOptions: { reducedMotion: 'reduce' },
  },
  // The site on a fresh database with the development seed; smoke runs against a given address instead.
  ...(process.env['E2E_BASE_URL']
    ? {}
    : {
        webServer: {
          command: 'node ../scripts/e2e-server.mjs',
          url: `http://localhost:${port}/health`,
          reuseExistingServer: false,
          timeout: 240_000,
          stdout: 'ignore',
          stderr: 'pipe',
        },
      }),
  // The scenarios run in parallel, each in its own season (TESTING.md «E2E-сценарии», I1). A few look at what is
  // global to the site — the newest season a spectator opens by default, the page speed on the demo season — and run
  // after the rest, one project at a time (tests/late, D-232).
  projects: [
    { name: 'desktop', testIgnore: late, use: desktop },
    { name: 'phone', testIgnore: late, use: phone },
    {
      name: 'desktop-late',
      testMatch: late,
      dependencies: ['desktop', 'phone'],
      use: desktop,
    },
    {
      name: 'phone-late',
      testMatch: late,
      dependencies: ['desktop-late'],
      use: phone,
    },
  ],
});
