import { defineConfig, devices } from '@playwright/test';

// Base address of the site under test. Smoke tests pass any address (staging, production).
const baseURL = process.env['E2E_BASE_URL'] ?? 'http://localhost:5080';

// Optional installed browser channel (msedge, chrome) when the bundled Chromium is unavailable.
const channel = process.env['PW_CHANNEL'];
const browser = channel ? { channel } : {};

export default defineConfig({
  testDir: './tests',
  outputDir: '../test-artifacts/playwright/results',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  // No retries until every test gets its own season (E4): a retry on the shared seed would fail for
  // a different reason and hide the real one.
  retries: 0,
  reporter: [
    ['list'],
    ['html', { outputFolder: '../test-artifacts/playwright/report', open: 'never' }],
  ],
  use: {
    baseURL,
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
          url: 'http://localhost:5080/health',
          reuseExistingServer: false,
          timeout: 240_000,
          stdout: 'ignore',
          stderr: 'pipe',
        },
      }),
  projects: [
    {
      name: 'desktop',
      use: { ...devices['Desktop Chrome'], ...browser, viewport: { width: 1440, height: 900 } },
    },
    {
      name: 'phone',
      use: {
        ...devices['Pixel 7'],
        ...browser,
        viewport: { width: 390, height: 844 },
        isMobile: true,
        hasTouch: true,
      },
    },
  ],
});
